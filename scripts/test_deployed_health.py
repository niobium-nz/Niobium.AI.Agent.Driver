import io
import json
from email.message import Message
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import Mock, patch
from urllib.error import HTTPError

from test_delivery_tools import load

health = load("health", "read-only-deployment.py")
resources = load("resources", "test-resources.py")


class DeployedHealthTests(unittest.TestCase):
    def test_requires_a_ready_json_response_from_the_expected_commit(self):
        expected = "a" * 40
        for payload, mime, success in [
            ({"status": "ready", "commit": expected}, "application/json", True),
            ({"status": "ready", "commit": "b" * 40}, "application/json", False),
            ({"status": "starting", "commit": expected}, "application/json", False),
            ({"status": "ready", "commit": expected}, "text/html", False),
            ([], "application/json", False),
        ]:
            with self.subTest(payload=payload, mime=mime):
                response = io.BytesIO(json.dumps(payload).encode())
                response.status = 200
                response.headers = Message()
                response.headers["Content-Type"] = mime
                opener = Mock()
                opener.open.return_value = response
                self.assertEqual(health.check("https://driver.example.invalid/", expected, 0, opener), success)
                request = opener.open.call_args.args[0]
                self.assertEqual((request.get_method(), request.full_url), ("GET", "https://driver.example.invalid/health"))
                self.assertFalse(request.has_header("Authorization"))

    def test_authenticated_mode_requires_exact_commit_and_denial_of_anonymous_get(self):
        expected = "a" * 40
        origin = "https://driver.example.invalid/"
        for public in (False, True):
            with self.subTest(public=public):
                response = io.BytesIO(json.dumps({"status": "ready", "commit": expected}).encode())
                response.status = 200
                response.headers = Message()
                response.headers["Content-Type"] = "application/json"
                anonymous = io.BytesIO()
                anonymous.status = 200
                denial = HTTPError(origin + "health", 401, "Unauthorized", {}, io.BytesIO())
                opener = Mock()
                opener.open.side_effect = [response, anonymous if public else denial]
                self.assertEqual(not public, health.check(origin, expected, 0, opener, access_token="synthetic-health"))
                authorized, anonymous = [call.args[0] for call in opener.open.call_args_list]
                self.assertEqual((authorized.full_url, authorized.get_method()), (anonymous.full_url, "GET"))
                self.assertEqual("GET", anonymous.get_method())
                self.assertEqual("Bearer synthetic-health", authorized.get_header("Authorization"))
                self.assertFalse(anonymous.has_header("Authorization"))

    def test_authenticated_cli_fails_closed_when_the_credential_is_missing(self):
        expected = "a" * 40
        with TemporaryDirectory() as directory:
            report = Path(directory) / "health.json"
            with patch.dict("os.environ", {"READONLY_EXPECTED_COMMIT": expected,
                                            "READONLY_API_URL": "https://driver.example.invalid"}, clear=True):
                with patch("sys.argv", ["health", "--report", str(report), "--require-authentication"]):
                    with patch.object(health, "build_opener") as opener:
                        with self.assertRaisesRegex(ValueError, "environment-supplied health bearer"):
                            health.main()
                        opener.assert_not_called()
            data = json.loads(report.read_text())
            self.assertEqual("authenticated", data["mode"])
            self.assertEqual(expected, data["commit"])
            self.assertFalse(data["success"])

    def test_invalid_deployment_inputs_fail_before_requests_and_redirects_are_disabled(self):
        opener = Mock()
        for url, sha in [
            ("http://service.invalid", "a" * 40), ("https://service.invalid/action", "a" * 40),
            ("https://service.invalid/?query=x", "a" * 40), ("https://service.invalid", "main"),
            ("https://user@service.invalid", "a" * 40),
        ]:
            with self.subTest(url=url, sha=sha), self.assertRaises(ValueError):
                health.check(url, sha, 0, opener)
        opener.open.assert_not_called()
        self.assertIsNone(health.NoRedirect().redirect_request(None, None, 302, None, None, "https://other.invalid"))

    def test_origin_is_read_from_the_named_function_in_each_environment(self):
        for environment in ("test", "prod"):
            function = f"driver-func-{environment}"
            hostname = f"{function}-abcd.region.azurewebsites.net"
            with patch.object(resources.subprocess, "check_output", return_value=hostname) as azure:
                origin = resources.resolve_origin(environment, "driver", "driver-group")
                args = azure.call_args.args[0]
                self.assertEqual(args[:3], ["az", "resource", "show"])
                self.assertEqual(args[args.index("--resource-group") + 1], "driver-group")
                self.assertEqual(args[args.index("--name") + 1], function)
                self.assertEqual(origin, "https://" + hostname)

    def test_unexpected_default_host_fails(self):
        with patch.object(resources.subprocess, "check_output", return_value="other.azurewebsites.net"):
            with self.assertRaises(ValueError):
                resources.resolve_origin("test", "driver", "driver-group")
