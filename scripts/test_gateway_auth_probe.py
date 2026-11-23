import json
import unittest
from unittest.mock import patch

from probe_gateway_auth import LIMIT, ProbeError, Response, Transport, parse_rpc, run_probe, validate_origin


SHA = "a" * 40


class FakeGateway:
    def __init__(self):
        self.calls = []
        self.anonymous = False
        self.commit = SHA
        self.wildcard = False
        self.error_result = False

    def __call__(self, method, path, headers, payload=None):
        self.calls.append((method, path, headers, payload))
        token = headers.get("Authorization")
        if method == "OPTIONS":
            return Response(204, {"access-control-allow-origin": "*"} if self.wildcard else {}, b"")
        if token != "Bearer synthetic-good-token":
            return Response(200 if self.anonymous else 401, {}, b"{}")
        if path == "/health":
            return Response(200, {}, json.dumps({"status": "ready", "commit": self.commit}).encode())
        rpc = payload["method"]
        if rpc == "notifications/initialized":
            return Response(202, {}, b"")
        if rpc == "initialize":
            result = {"protocolVersion": "2025-06-18"}
        elif rpc == "tools/list":
            result = {"tools": [{"name": "list_agents"}, {"name": "dispatch_agent"}]}
        elif rpc == "tools/call":
            self.assert_read_only(payload)
            result = {"isError": self.error_result, "structuredContent": {"agents": []}}
        else:
            raise AssertionError("Unexpected method")
        return Response(200, {"mcp-session-id": "synthetic-session"},
                        json.dumps({"jsonrpc": "2.0", "id": payload["id"], "result": result}).encode())

    @staticmethod
    def assert_read_only(payload):
        assert payload["params"] == {"name": "list_agents", "arguments": {}}


class AuthenticationProbeTests(unittest.TestCase):
    def execute(self, fake, invalid=True):
        return run_probe("https://approved-test.invalid", SHA, "synthetic-good-token", transport=fake,
                         invalid_tokens={name: "synthetic-invalid-token" for name in ("expired", "wrong_audience", "wrong_scope")} if invalid else None)

    def test_pass_is_bounded_read_only_and_does_not_claim_interactive_acceptance(self):
        fake = FakeGateway()
        report = self.execute(fake)
        self.assertTrue(report["success"])
        self.assertFalse(report["interactive_chatgpt_oauth_proven"])
        self.assertFalse(report["live_agent_execution_tested"])
        self.assertLessEqual(len(fake.calls), 15)
        serialized = json.dumps(report)
        for private in ("synthetic-good-token", "synthetic-invalid-token", "approved-test.invalid", "synthetic-session"):
            self.assertNotIn(private, serialized)
        for _, _, _, payload in fake.calls:
            if payload and payload["method"] == "tools/call":
                fake.assert_read_only(payload)

    def test_missing_prerequisites_are_skipped_not_passed(self):
        report = self.execute(FakeGateway(), invalid=False)
        self.assertFalse(report["success"])
        self.assertEqual(3, len(report["skipped"]))

    def test_anonymous_spoofing_commit_cors_and_caller_failures_are_detected(self):
        for name, value in (("anonymous", True), ("commit", "b" * 40), ("wildcard", True), ("error_result", True)):
            fake = FakeGateway()
            setattr(fake, name, value)
            self.assertFalse(self.execute(fake)["success"])

    def test_untrusted_destinations_and_bad_credentials_are_rejected_before_io(self):
        for origin in ("http://test.invalid", "https://user:secret@test.invalid", "https://test.invalid/path",
                       "https://test.invalid?token=secret", "https://test.invalid#fragment", "https://test.invalid:444", "https://test.invalid\\evil"):
            with self.assertRaises((ProbeError, ValueError)):
                validate_origin(origin)
        for token in ("", "has whitespace", "line\nbreak"):
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
