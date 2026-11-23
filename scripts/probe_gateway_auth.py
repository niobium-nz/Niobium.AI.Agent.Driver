"""Explicit read-only acceptance checks for one approved protected gateway.

Tokens come from environment variables, never URLs or command-line arguments.
Redirects and proxies are disabled. Reports never contain URLs, tokens, response
text or exception strings. No agent execution or configuration mutation exists.
This is not proof of ChatGPT interactive OAuth discovery/consent/refresh.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from typing import Callable

LIMIT = 65536
PROTOCOL = "2025-06-18"


class ProbeError(Exception):
    """Safe fixed-category failure without private transport details."""


@dataclass(frozen=True)
class Response:
    status: int
    headers: dict[str, str]
    body: bytes


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def validate_origin(value: str) -> str:
    parsed = urllib.parse.urlsplit(value)
    if (parsed.scheme != "https" or not parsed.hostname or parsed.username is not None
            or parsed.password is not None or parsed.query or parsed.fragment
            or parsed.path not in ("", "/") or parsed.port not in (None, 443)
            or any(c.isspace() for c in value) or "\\" in value
            or not re.fullmatch(r"[A-Za-z0-9.-]+", parsed.hostname)):
        raise ProbeError("invalid_origin")
    return "https://" + parsed.hostname.lower()


def credential(value: str) -> str:
    if not value or len(value) > 16384 or any(c.isspace() for c in value):
        raise ProbeError("missing_or_invalid_credential")
    return value


def parse_rpc(body: bytes, request_id: int) -> dict:
    if len(body) > LIMIT:
        raise ProbeError("response_too_large")
    try:
        text = body.decode("utf-8")
        if text.lstrip().startswith("{"):
            messages = [json.loads(text)]
        else:
            messages = []
            for event in re.split(r"\r?\n\r?\n", text):
                data = "\n".join(line[5:].lstrip() for line in event.splitlines() if line.startswith("data:"))
                if data:
                    messages.append(json.loads(data))
        matching = [message for message in messages if isinstance(message, dict) and message.get("id") == request_id]
        if len(matching) != 1 or "error" in matching[0] or not isinstance(matching[0].get("result"), dict):
            raise ProbeError("invalid_rpc_response")
        return matching[0]["result"]
    except (ValueError, UnicodeError):
        raise ProbeError("invalid_rpc_response") from None


class Transport:
    def __init__(self, origin: str):
        self.origin = validate_origin(origin)
        self.opener = urllib.request.build_opener(NoRedirect(), urllib.request.ProxyHandler({}))

    def __call__(self, method: str, path: str, headers: dict[str, str], payload: dict | None = None) -> Response:
        if path not in ("/health", "/runtime/webhooks/mcp") or method not in ("GET", "POST", "OPTIONS"):
            raise ProbeError("unsupported_probe")
        if payload is not None:
            allowed = payload.get("method") in ("initialize", "notifications/initialized", "tools/list")
            allowed |= payload.get("method") == "tools/call" and payload.get("params") == {"name": "list_agents", "arguments": {}}
            if not allowed:
                raise ProbeError("unsupported_probe")
        data = None if payload is None else json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(self.origin + path, data=data, headers=headers, method=method)
        try:
            response = self.opener.open(request, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        except Exception:
            raise ProbeError("transport_failed") from None
        try:
            with response:
                body = response.read(LIMIT + 1)
                if len(body) > LIMIT:
                    raise ProbeError("response_too_large")
                return Response(response.code, {k.lower(): v for k, v in response.headers.items()}, body)
        except ProbeError:
            raise
        except Exception:
            raise ProbeError("transport_failed") from None


def session_headers(headers: dict[str, str], response: Response) -> dict[str, str]:
    if response.status != 200 or parse_rpc(response.body, 1).get("protocolVersion") != PROTOCOL:
        raise ProbeError("initialization_failed")
    session = response.headers.get("mcp-session-id")
    if session is not None and (len(session) > 256 or any(c.isspace() for c in session)):
        raise ProbeError("invalid_session")
    result = headers | {"MCP-Protocol-Version": PROTOCOL}
    if session is not None:
        result["Mcp-Session-Id"] = session
    return result


def token_denied(send: Callable, base: dict, initialize: dict, token: str) -> bool:
    """Scope can be enforced at the native tool boundary, not at discovery."""
    headers = base | {"Authorization": "Bearer " + credential(token)}
    response = send("POST", "/runtime/webhooks/mcp", headers, initialize)
    if response.status in (401, 403):
        return True
    try:
        context = session_headers(headers, response)
        ready = send("POST", "/runtime/webhooks/mcp", context, {"jsonrpc": "2.0", "method": "notifications/initialized"})
        if ready.status not in (200, 202, 204):
            return False
        call = send("POST", "/runtime/webhooks/mcp", context,
                    {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "list_agents", "arguments": {}}})
        if call.status in (401, 403):
            return True
        result = parse_rpc(call.body, 3) if call.status == 200 else {}
        structured = result.get("structuredContent", {})
        return (result.get("isError") is True and isinstance(structured, dict)
                and structured.get("error_code") in ("unauthenticated", "forbidden"))
    except ProbeError:
        return False


def run_probe(origin: str, expected_commit: str, bearer: str, *,
              invalid_tokens: dict[str, str] | None = None, transport: Callable | None = None) -> dict:
    origin = validate_origin(origin)
    if not re.fullmatch(r"[0-9a-f]{40}", expected_commit):
        raise ProbeError("invalid_commit")
    bearer = credential(bearer)
    send = transport if transport is not None else Transport(origin)
    checks: dict[str, bool] = {}
    skipped: list[str] = []
    base = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    initialize = {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": PROTOCOL, "capabilities": {}, "clientInfo": {"name": "niobium-readonly-auth-probe", "version": "1"}}}
    for name, extra in (
        ("missing_bearer_denied", {}),
        ("spoofed_principal_denied", {"X-MS-CLIENT-PRINCIPAL": "eyJjbGFpbXMiOltdfQ==", "X-MS-CLIENT-PRINCIPAL-ID": "synthetic-spoof"}),
        ("malformed_bearer_denied", {"Authorization": "Bearer intentionally-invalid"}),
    ):
        checks[name] = send("POST", "/runtime/webhooks/mcp", base | extra, initialize).status in (401, 403)
    for category in ("expired", "wrong_audience", "wrong_scope"):
        value = (invalid_tokens or {}).get(category)
        if value is None:
            skipped.append(category + "_token_denied")
        else:
            checks[category + "_token_denied"] = token_denied(send, base, initialize, value)
    checks["health_requires_authentication"] = send("GET", "/health", {}).status in (401, 403)
    authorized = base | {"Authorization": "Bearer " + bearer}
    health = send("GET", "/health", authorized)
    try:
        value = json.loads(health.body) if health.status == 200 and len(health.body) <= LIMIT else {}
        checks["authenticated_exact_commit"] = value.get("status") == "ready" and value.get("commit") == expected_commit
    except (ValueError, AttributeError):
        checks["authenticated_exact_commit"] = False
    cors = send("OPTIONS", "/runtime/webhooks/mcp", {"Origin": "https://untrusted.invalid", "Access-Control-Request-Method": "POST"})
    checks["untrusted_browser_origin_not_allowed"] = cors.headers.get("access-control-allow-origin") not in ("*", "https://untrusted.invalid")
    response = send("POST", "/runtime/webhooks/mcp", authorized, initialize)
    checks["authenticated_initialize"] = False
    checks["authenticated_tool_discovery"] = False
    checks["validated_caller_can_list_agents"] = False
    try:
        context = session_headers(authorized, response)
        checks["authenticated_initialize"] = True
        ready = send("POST", "/runtime/webhooks/mcp", context, {"jsonrpc": "2.0", "method": "notifications/initialized"})
        if ready.status not in (200, 202, 204):
            raise ProbeError("initialization_failed")
        discovered = send("POST", "/runtime/webhooks/mcp", context, {"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
        tools = parse_rpc(discovered.body, 2).get("tools", []) if discovered.status == 200 else []
        names = {tool.get("name") for tool in tools if isinstance(tool, dict)}
        checks["authenticated_tool_discovery"] = {"list_agents", "dispatch_agent"}.issubset(names)
        listing = send("POST", "/runtime/webhooks/mcp", context,
            {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "list_agents", "arguments": {}}})
        data = parse_rpc(listing.body, 3) if listing.status == 200 else {}
        structured = data.get("structuredContent", {})
        checks["validated_caller_can_list_agents"] = (data.get("isError") is not True and isinstance(structured, dict)
            and isinstance(structured.get("agents"), list))
    except ProbeError:
        pass
    return {"commit": expected_commit, "checks": checks, "skipped": skipped,
            "success": all(checks.values()) and not skipped,
            "interactive_chatgpt_oauth_proven": False, "live_agent_execution_tested": False}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--origin", required=True, help="One approved test HTTPS origin, without a path or credentials.")
    parser.add_argument("--commit", required=True, help="Expected forty-character deployed commit SHA.")
    args = parser.parse_args()
    try:
        invalid = {category: os.environ[name] for category, name in (
            ("expired", "GATEWAY_EXPIRED_TEST_TOKEN"), ("wrong_audience", "GATEWAY_WRONG_AUDIENCE_TEST_TOKEN"),
            ("wrong_scope", "GATEWAY_WRONG_SCOPE_TEST_TOKEN")) if os.environ.get(name)}
        result = run_probe(args.origin, args.commit, os.environ.get("GATEWAY_TEST_ACCESS_TOKEN", ""), invalid_tokens=invalid)
    except Exception:
        result = {"success": False, "error": "probe_failed_without_private_details"}
    print(json.dumps(result, sort_keys=True))
    return 0 if result["success"] else 1


if __name__ == "__main__":
    sys.exit(main())
