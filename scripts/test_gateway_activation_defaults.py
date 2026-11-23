"""Guard activation defaults without weakening existing delivery/coverage policy."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


class GatewayActivationDefaultsTests(unittest.TestCase):
    def test_browser_cors_defaults_to_no_origins_and_no_credentials(self):
        source = (ROOT / "infra/function-app.bicep").read_text()
        self.assertIn("param allowedCorsOrigins array = []", source)
        cors = re.search(r"cors:\s*\{([^}]+)\}", source).group(1)
        self.assertIn("allowedOrigins: allowedCorsOrigins", cors)
        self.assertIn("supportCredentials: false", cors)
        self.assertNotIn("'*'", cors)

    def test_optional_features_are_forced_off_and_route_storage_is_opt_in(self):
        source = (ROOT / "infra/main.bicep").read_text()
        for flag in ("EnableProviderRunPolling", "EnableParentContinuation"):
            self.assertRegex(source, r"name: 'AgentGatewayOptions__" + flag + r"'\s+value: 'false'")
        ordinary = (ROOT / "infra/main.bicepparam").read_text()
        self.assertNotIn("AgentContinuationRoute", ordinary)
        opt_in = (ROOT / "infra/parent-continuation.bicepparam").read_text()
        self.assertIn("'AgentContinuationRoute'", opt_in)
        self.assertNotIn("Enabled=true", opt_in)


if __name__ == "__main__":
    unittest.main()
