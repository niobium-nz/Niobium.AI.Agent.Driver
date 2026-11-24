import json
import unittest
from unittest.mock import patch

from probe_gateway_auth import LIMIT, TOOL_FIELDS, ProbeError, Response, Transport, parse_rpc, run_probe, validate_origin


SHA = "a" * 40
TOOLS = ("list_agents", "dispatch_agent", "start_agent_job", "submit_job_result", "get_job_status", "get_job_result")
NEGATIVE_TOKENS = ("expired", "wrong_audience", "wrong_scope", "wrong_tenant", "unauthorized_client")


class FakeGateway:
    def __init__(self):
        self.calls = []
        self.anonymous = False
        self.commit = SHA
        self.wildcard = False
        self.credentialed_cors = False
        self.error_result = False
        self.text_mismatch = False
        self.discovered = TOOLS
        self.invalid_schema = False
        self.requires_system_key = False
        self.health_can_call_tools = False
        self.session_bypasses_bearer = False

    def __call__(self, method, path, headers, payload=None):
        self.calls.append((method, path, headers, payload))
        token = headers.get("Authorization")
        if method == "OPTIONS":
            response_headers = {}
            if self.wildcard:
                response_headers["access-control-allow-origin"] = "*"
            if self.credentialed_cors:
                response_headers["access-control-allow-credentials"] = "true"
            return Response(204, response_headers, b"")
        if path == "/health":
            if token not in ("Bearer synthetic-good-token", "Bearer synthetic-health-token"):
                return Response(200 if self.anonymous else 401, {}, b"{}")
            return Response(200, {}, json.dumps({"status": "ready", "commit": self.commit}).encode())
        if self.requires_system_key and headers.get("x-functions-key") != "synthetic-system-key":
            return Response(401, {}, b"{}")
        authorized = token == "Bearer synthetic-good-token"
        authorized |= self.health_can_call_tools and token == "Bearer synthetic-health-token"
        authorized |= self.session_bypasses_bearer and headers.get("Mcp-Session-Id") == "synthetic-session"
        if not authorized:
            return Response(200 if self.anonymous else 401, {}, b"{}")
        rpc = payload["method"]
        if rpc == "notifications/initialized":
            return Response(202, {}, b"")
        if rpc == "initialize":
            result = {"protocolVersion": "2025-06-18"}
        elif rpc == "tools/list":
            result = {"tools": [{"name": name, "inputSchema": {"type": "object",
                        "properties": dict.fromkeys(TOOL_FIELDS[name], {})}} for name in self.discovered]}
            if self.invalid_schema:
                result["tools"][1]["inputSchema"]["properties"].pop("handoff_id")
        elif rpc == "tools/call":
            self.assert_read_only(payload)
            result = {"isError": self.error_result, "structuredContent": {"agents": []},
                      "content": [{"type": "text", "text": "{}" if self.text_mismatch else '{"agents": []}'}]}
        else:
            raise AssertionError("Unexpected method")
        return Response(200, {"mcp-session-id": "synthetic-session"},
                        json.dumps({"jsonrpc": "2.0", "id": payload["id"], "result": result}).encode())

    @staticmethod
    def assert_read_only(payload):
        assert payload["params"] == {"name": "list_agents", "arguments": {}}


class AuthenticationProbeTests(unittest.TestCase):
    def execute(self, fake, invalid=True, *, mcp_system_key=None, health_bearer="synthetic-health-token"):
        return run_probe("https://approved-test.invalid", SHA, "synthetic-good-token", transport=fake,
                         invalid_tokens={name: "synthetic-invalid-token" for name in NEGATIVE_TOKENS} if invalid else None,
                         mcp_system_key=mcp_system_key, health_bearer=health_bearer)

    def test_pass_is_bounded_read_only_and_does_not_claim_interactive_acceptance(self):
        fake = FakeGateway()
        report = self.execute(fake)
        self.assertTrue(report["success"])
        self.assertFalse(report["interactive_chatgpt_oauth_proven"])
        self.assertFalse(report["live_agent_execution_tested"])
        self.assertLessEqual(len(fake.calls), 20)
        serialized = json.dumps(report)
        for private in ("synthetic-good-token", "synthetic-invalid-token", "synthetic-health-token",
                        "approved-test.invalid", "synthetic-session"):
            self.assertNotIn(private, serialized)
        for _, _, _, payload in fake.calls:
            if payload and payload["method"] == "tools/call":
                fake.assert_read_only(payload)

    def test_missing_prerequisites_are_skipped_not_passed(self):
        report = self.execute(FakeGateway(), invalid=False, health_bearer=None)
        self.assertFalse(report["success"])
        self.assertEqual({*(name + "_token_denied" for name in NEGATIVE_TOKENS), "health_verifier_mcp_denied"},
                         set(report["skipped"]))

    def test_anonymous_spoofing_commit_cors_and_caller_failures_are_detected(self):
        for name, value in (("anonymous", True), ("commit", "b" * 40), ("wildcard", True),
                            ("credentialed_cors", True), ("error_result", True), ("text_mismatch", True),
                            ("health_can_call_tools", True), ("session_bypasses_bearer", True),
                            ("discovered", TOOLS[:2]), ("invalid_schema", True)):
            with self.subTest(name=name):
                fake = FakeGateway()
                setattr(fake, name, value)
                self.assertFalse(self.execute(fake)["success"])

    def test_dual_gate_checks_send_the_native_key_only_on_mcp_posts(self):
        fake = FakeGateway()
        fake.requires_system_key = True
        report = self.execute(fake, mcp_system_key="synthetic-system-key")
        self.assertTrue(report["success"])
        self.assertTrue(report["checks"]["missing_native_mcp_key_denied"])
        self.assertNotIn("synthetic-system-key", json.dumps(report))
        without_key = [headers for method, _, headers, _ in fake.calls
                       if method == "POST" and "x-functions-key" not in headers]
        self.assertEqual(["Bearer synthetic-good-token"], [headers.get("Authorization") for headers in without_key])
        for method, _, headers, _ in fake.calls:
            if method != "POST":
                self.assertNotIn("x-functions-key", headers)
        self.assertFalse(self.execute(fake)["success"])

    def test_dual_gate_fails_when_the_native_key_is_not_enforced(self):
        report = self.execute(FakeGateway(), mcp_system_key="synthetic-system-key")
        self.assertFalse(report["success"])
        self.assertFalse(report["checks"]["missing_native_mcp_key_denied"])

    def test_untrusted_destinations_and_bad_credentials_are_rejected_before_io(self):
        for origin in ("http://test.invalid", "https://user:secret@test.invalid", "https://test.invalid/path",
                       "https://test.invalid?token=secret", "https://test.invalid#fragment", "https://test.invalid:444", "https://test.invalid\\evil"):
            with self.assertRaises((ProbeError, ValueError)):
                validate_origin(origin)
        for token in ("", "has whitespace", "line\nbreak", "control\x00"):
            with self.assertRaises(ProbeError):
                run_probe("https://test.invalid", SHA, token, transport=lambda *_: self.fail("Unexpected network request"))
        with self.assertRaises(ProbeError):
            run_probe("https://test.invalid", "main", "token")

    def test_sse_and_json_require_the_matching_rpc_result(self):
        payload = json.dumps({"jsonrpc": "2.0", "id": 3, "result": {"ok": True}})
        self.assertEqual({"ok": True}, parse_rpc(payload.encode(), 3))
        self.assertEqual({"ok": True}, parse_rpc(("event: message\ndata: " + payload + "\n\n").encode(), 3))
        for body in (b"null", b"{}", b"private html", b"x" * (LIMIT + 1),
                     b'{"id":3,"error":{"message":"private"}}', b'{"id":2,"result":{}}'):
            with self.assertRaises(ProbeError):
                parse_rpc(body, 3)

    def test_transport_never_follows_redirect_or_allows_mutating_methods(self):
        transport = Transport("https://test.invalid")
        with self.assertRaises(ProbeError):
            transport("POST", "/runtime/webhooks/mcp", {}, {"method": "tools/call", "params": {"name": "start_agent_job"}})
        with self.assertRaises(ProbeError):
            transport("GET", "https://foreign.invalid", {})
        with self.assertRaises(ProbeError):
            transport("GET", "/health", {"X-Functions-Key": "synthetic-system-key"})
        from probe_gateway_auth import NoRedirect
        self.assertIsNone(NoRedirect().redirect_request(None, None, 302, None, {}, "https://foreign.invalid"))

    def test_transport_errors_cannot_expose_credentials_in_report(self):
        with patch("probe_gateway_auth.Transport.__call__", side_effect=OSError("private bearer token")):
            from probe_gateway_auth import main
            import contextlib
            import io
            capture = io.StringIO()
            with patch("sys.argv", ["probe", "--origin", "https://test.invalid", "--commit", SHA]), patch.dict("os.environ", {"GATEWAY_TEST_ACCESS_TOKEN": "secret"}), contextlib.redirect_stdout(capture):
                self.assertEqual(1, main())
            self.assertNotIn("private bearer", capture.getvalue())
            self.assertNotIn("secret", capture.getvalue())


if __name__ == "__main__":
    unittest.main()
