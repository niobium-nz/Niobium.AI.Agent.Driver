"""Regression guards for feature branch deployment using the unchanged trusted pipeline."""
from pathlib import Path
import re
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]


class FeatureDeliveryTests(unittest.TestCase):
    def test_every_feature_push_is_included_without_path_filters(self):
        workflow = (ROOT / ".github/workflows/azure-deploy.yml").read_text(encoding="utf-8")
        triggers = workflow.split("on:\n", 1)[1].split("\npermissions:", 1)[0]
        self.assertRegex(triggers, r"(?m)^  push:\n    branches-ignore: \[main\]$")
        self.assertNotRegex(triggers, r"(?m)^\s+(paths|paths-ignore):")
        self.assertIn("AZURE_ENV_NAME: ${{ github.event_name == 'push' && 'test' || 'prod' }}", workflow)
        self.assertIn("AZURE_RESOURCE_GROUP: ${{ github.event_name == 'push' && 'rg-niobium-agents-test' || 'rg-niobium-agents-prod' }}", workflow)
        self.assertIn("needs: [test, trusted-test]", workflow)

    def test_trusted_policy_accepts_all_feature_names_only_with_successful_evidence(self):
        script = r"""
const assert = require('node:assert/strict');
const { eligibleRun } = require('./scripts/delivery-policy.cjs');
for (const head_branch of ['feature/agent-gateway-stage1', 'feature/fix-tests', 'feature/team/nested-change']) {
  const run = { head_sha: 'candidate', event: 'push', head_branch, conclusion: 'success',
    head_repository: { full_name: 'niobium-nz/Niobium.AI.Agent.Driver' }, path: '.github/workflows/azure-deploy.yml' };
  assert.equal(eligibleRun(run, 'candidate', 'niobium-nz/Niobium.AI.Agent.Driver'), true);
  for (const conclusion of ['failure', 'cancelled', 'skipped', null]) {
    assert.equal(eligibleRun({ ...run, conclusion }, 'candidate', 'niobium-nz/Niobium.AI.Agent.Driver'), false);
  }
  assert.equal(eligibleRun({ ...run, head_branch: 'main' }, 'candidate', 'niobium-nz/Niobium.AI.Agent.Driver'), false);
  assert.equal(eligibleRun(run, 'another-commit', 'niobium-nz/Niobium.AI.Agent.Driver'), false);
}
"""
        subprocess.run(["node", "-e", script], cwd=ROOT, check=True, timeout=20)


if __name__ == "__main__":
    unittest.main()
