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
MCP_PATH = "/runtime/webhooks/mcp"
TOOL_FIELDS = {
    "list_agents": set(),
    "dispatch_agent": {"target_agent", "handoff_id", "input", "conversation_key"},
    "start_agent_job": {"target_agent", "handoff_id", "input", "result_schema", "completion_mode",
                        "continuation_ref", "conversation_key"},
    "submit_job_result": {"job_id", "completion_capability", "outcome", "result"},
    "get_job_status": {"job_id"},
    "get_job_result": {"job_id"},
}
NEGATIVE_TOKEN_ENV = {
    "expired": "GATEWAY_EXPIRED_TEST_TOKEN",
    "wrong_audience": "GATEWAY_WRONG_AUDIENCE_TEST_TOKEN",
    "wrong_scope": "GATEWAY_WRONG_SCOPE_TEST_TOKEN",
    "wrong_tenant": "GATEWAY_WRONG_TENANT_TEST_TOKEN",
    "unauthorized_client": "GATEWAY_UNAUTHORIZED_CLIENT_TEST_TOKEN",
}


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
    if not re.fullmatch(r"[!-~]{1,16384}", value):
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
        if ((method, path) not in (("GET", "/health"), ("POST", MCP_PATH), ("OPTIONS", MCP_PATH))
                or (method != "POST" and any(name.lower() == "x-functions-key" for name in headers))
                or (method == "POST") != (payload is not None)):
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


def list_agents(send: Callable, headers: dict, request_id: int) -> Response:
    return send("POST", MCP_PATH, headers, {"jsonrpc": "2.0", "id": request_id, "method": "tools/call",
                "params": {"name": "list_agents", "arguments": {}}})


def call_denied(response: Response, request_id: int) -> bool:
    if response.status in (401, 403):
        return True
    result = parse_rpc(response.body, request_id) if response.status == 200 else {}
    structured = result.get("structuredContent", {})
    return (result.get("isError") is True and isinstance(structured, dict)
            and structured.get("error_code") in ("unauthenticated", "forbidden"))


def token_denied(send: Callable, base: dict, initialize: dict, token: str) -> bool:
    """Scope can be enforced at the native tool boundary, not at discovery."""
    headers = base | {"Authorization": "Bearer " + credential(token)}
    response = send("POST", MCP_PATH, headers, initialize)
    if response.status in (401, 403):
        return True
    try:
        context = session_headers(headers, response)
        ready = send("POST", MCP_PATH, context, {"jsonrpc": "2.0", "method": "notifications/initialized"})
        if ready.status not in (200, 202, 204):
            return False
        return call_denied(list_agents(send, context, 3), 3)
    except ProbeError:
        return False


def tool_discovery_passed(tools: object) -> bool:
    if not isinstance(tools, list) or len(tools) != len(TOOL_FIELDS):
        return False
    names = set()
    for tool in tools:
        if not isinstance(tool, dict) or not isinstance(tool.get("name"), str):
            return False
        schema = tool.get("inputSchema")
        if not isinstance(schema, dict) or schema.get("type") != "object":
            return False
        name = tool["name"]
        properties = schema.get("properties", {})
        if not isinstance(properties, dict) or properties.keys() != TOOL_FIELDS.get(name):
            return False
        names.add(name)
    return names == TOOL_FIELDS.keys()


def text_matches_structured(result: dict, structured: dict) -> bool:
    content = result.get("content")
    if not isinstance(content, list):
        return False
    text_blocks = [block.get("text") for block in content if isinstance(block, dict) and block.get("type") == "text"]
    if len(text_blocks) != 1 or not isinstance(text_blocks[0], str):
        return False
    try:
        return json.loads(text_blocks[0]) == structured
    except ValueError:
        return False


def run_probe(origin: str, expected_commit: str, bearer: str, *,
              invalid_tokens: dict[str, str] | None = None, mcp_system_key: str | None = None,
              health_bearer: str | None = None, transport: Callable | None = None) -> dict:
    origin = validate_origin(origin)
    if not re.fullmatch(r"[0-9a-f]{40}", expected_commit):
        raise ProbeError("invalid_commit")
    bearer = credential(bearer)
    send = transport if transport is not None else Transport(origin)
    checks: dict[str, bool] = {}
    skipped: list[str] = []
    protocol_headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    base = protocol_headers.copy()
    if mcp_system_key is not None:
        base["x-functions-key"] = credential(mcp_system_key)
    health_authorization = "Bearer " + (credential(health_bearer) if health_bearer is not None else bearer)
    initialize = {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": PROTOCOL, "capabilities": {}, "clientInfo": {"name": "niobium-readonly-auth-probe", "version": "1"}}}
    for name, extra in (
        ("missing_bearer_denied", {}),
        ("spoofed_principal_denied", {"X-MS-CLIENT-PRINCIPAL": "eyJjbGFpbXMiOltdfQ==", "X-MS-CLIENT-PRINCIPAL-ID": "synthetic-spoof"}),
        ("malformed_bearer_denied", {"Authorization": "Bearer intentionally-invalid"}),
    ):
        checks[name] = send("POST", MCP_PATH, base | extra, initialize).status in (401, 403)
    for category in NEGATIVE_TOKEN_ENV:
        value = (invalid_tokens or {}).get(category)
        if value is None:
            skipped.append(category + "_token_denied")
        else:
            checks[category + "_token_denied"] = token_denied(send, base, initialize, value)
    checks["health_requires_authentication"] = send("GET", "/health", {}).status in (401, 403)
    health = send("GET", "/health", {"Authorization": health_authorization})
    try:
        value = json.loads(health.body) if health.status == 200 and len(health.body) <= LIMIT else {}
        checks["authenticated_exact_commit"] = value.get("status") == "ready" and value.get("commit") == expected_commit
    except (ValueError, AttributeError):
        checks["authenticated_exact_commit"] = False
    if health_bearer is None:
        skipped.append("health_verifier_mcp_denied")
    else:
        checks["health_verifier_mcp_denied"] = token_denied(send, base, initialize, health_bearer)
    cors = send("OPTIONS", MCP_PATH, {"Origin": "https://untrusted.invalid", "Access-Control-Request-Method": "POST",
                "Access-Control-Request-Headers": "authorization, content-type"})
    checks["untrusted_browser_origin_not_allowed"] = (
        cors.headers.get("access-control-allow-origin") not in ("*", "https://untrusted.invalid")
        and cors.headers.get("access-control-allow-credentials", "").lower() != "true")
    delegated_authorization = {"Authorization": "Bearer " + bearer}
    if mcp_system_key is not None:
        no_key = send("POST", MCP_PATH, protocol_headers | delegated_authorization, initialize)
        checks["missing_native_mcp_key_denied"] = no_key.status in (401, 403)
    response = send("POST", MCP_PATH, base | delegated_authorization, initialize)
    checks["authenticated_initialize"] = False
    checks["authenticated_tool_discovery"] = False
    checks["validated_caller_can_list_agents"] = False
    checks["list_agents_text_matches_structured"] = False
    checks["mcp_call_requires_bearer_after_initialize"] = False
    session_id_issued = False
    try:
        context = session_headers(base | delegated_authorization, response)
        checks["authenticated_initialize"] = True
        session_id_issued = "Mcp-Session-Id" in context
        ready = send("POST", MCP_PATH, context, {"jsonrpc": "2.0", "method": "notifications/initialized"})
        if ready.status not in (200, 202, 204):
            raise ProbeError("initialization_failed")
        discovered = send("POST", MCP_PATH, context, {"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
        discovery = parse_rpc(discovered.body, 2) if discovered.status == 200 else {}
        checks["authenticated_tool_discovery"] = (discovery.get("nextCursor") is None
            and tool_discovery_passed(discovery.get("tools")))
        listing = list_agents(send, context, 3)
        data = parse_rpc(listing.body, 3) if listing.status == 200 else {}
        structured = data.get("structuredContent", {})
        checks["validated_caller_can_list_agents"] = (data.get("isError") is not True and isinstance(structured, dict)
            and isinstance(structured.get("agents"), list))
        checks["list_agents_text_matches_structured"] = (checks["validated_caller_can_list_agents"]
            and text_matches_structured(data, structured))
        without_bearer = {name: value for name, value in context.items() if name != "Authorization"}
        checks["mcp_call_requires_bearer_after_initialize"] = call_denied(list_agents(send, without_bearer, 4), 4)
    except ProbeError:
        pass
    return {"commit": expected_commit, "checks": checks, "skipped": skipped, "session_id_issued": session_id_issued,
            "success": all(checks.values()) and not skipped,
            "interactive_chatgpt_oauth_proven": False, "live_agent_execution_tested": False}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--origin", required=True, help="One approved test HTTPS origin, without a path or credentials.")
    parser.add_argument("--commit", required=True, help="Expected forty-character deployed commit SHA.")
    args = parser.parse_args()
    try:
        invalid = {category: os.environ[name] for category, name in NEGATIVE_TOKEN_ENV.items() if os.environ.get(name)}
        result = run_probe(args.origin, args.commit, os.environ.get("GATEWAY_TEST_ACCESS_TOKEN", ""),
                           invalid_tokens=invalid, mcp_system_key=os.environ.get("GATEWAY_TEST_MCP_SYSTEM_KEY"),
                           health_bearer=os.environ.get("GATEWAY_TEST_HEALTH_ACCESS_TOKEN"))
    except Exception:
        result = {"success": False, "error": "probe_failed_without_private_details"}
    print(json.dumps(result, sort_keys=True))
    return 0 if result["success"] else 1


if __name__ == "__main__":
    sys.exit(main())
