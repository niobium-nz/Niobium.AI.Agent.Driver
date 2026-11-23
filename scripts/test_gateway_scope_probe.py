import json
import unittest
from probe_gateway_auth import PROTOCOL, Response, token_denied


class GatewayScopeProbeTests(unittest.TestCase):
    def test_native_scope_rejection_can_follow_successful_initialization(self):
        calls = []
        def send(method, path, headers, payload):
            calls.append(payload)
            if payload["method"] == "initialize":
                return Response(200, {}, json.dumps({"id": 1, "result": {"protocolVersion": PROTOCOL}}).encode())
            if payload["method"] == "notifications/initialized":
                return Response(202, {}, b"")
            self.assertEqual({"name": "list_agents", "arguments": {}}, payload["params"])
            return Response(200, {}, json.dumps({"id": 3, "result": {"isError": True,
                "structuredContent": {"error_code": "forbidden"}}}).encode())
        self.assertTrue(token_denied(send, {}, {"method": "initialize"}, "synthetic-no-scope"))
        self.assertEqual(3, len(calls))

    def test_disabled_or_broken_tool_is_not_authentication_evidence(self):
        def send(method, path, headers, payload):
            if payload["method"] == "initialize":
                return Response(200, {}, json.dumps({"id": 1, "result": {"protocolVersion": PROTOCOL}}).encode())
            if payload["method"] == "notifications/initialized":
                return Response(202, {}, b"")
            return Response(200, {}, json.dumps({"id": 3, "result": {"isError": True,
                "structuredContent": {"error_code": "gateway_disabled"}}}).encode())
        self.assertFalse(token_denied(send, {}, {"method": "initialize"}, "synthetic-no-scope"))


if __name__ == "__main__":
    unittest.main()
