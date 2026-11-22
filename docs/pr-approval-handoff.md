# PR review and approval

## Feature changes

1. Push a feature branch. Verify the build, local tests, coverage, test deployment, candidate integration tests and the independent integration tests from the current `main` commit. Record the run URL and feature SHA.
2. After verification, the branch author opens or updates a ready PR into `main`. Request `niobium-nz` for review, or `5he11` when `niobium-nz` authored the PR. Verify the reviewer assignment. A reviewer counting toward the protected approval must have the required repository permission. At setup, GitHub reported write access for `5he11` on Invoicing and read access on Driver. Grant the corresponding Driver access before relying on that reviewer.
3. Check the native Codex code and security review receipt for the current head. Monitor review submissions, inline threads and conversation comments. Fix findings and rerun the pipeline after a push.
4. If the current head has no qualifying formal approval, explicitly invoke a separate `pr_approver` session. Give it the PR URL, exact head and base SHAs, diff, workflow evidence, native receipts and outstanding findings. Request an independent decision. The reviewer verifies its GitHub identity and repository permission, inspects the change and test evidence, and submits `APPROVE` or `REQUEST_CHANGES` on that commit. The implementation session checks the decision, author and SHA reported by GitHub.
5. Resolve threads. Verify one valid current-head approval, successful required checks and test deployment, current main ancestry, and equality of the tested and proposed merge trees. Squash merge with the expected head SHA. A changed head goes through review and verification again.
6. Monitor the merged commit's production deployment, GET-only verification and any applicable package publication to completion. Investigate failures through the same feature and PR process.

## Repository settings

Import the [main ruleset](../.github/rulesets/main.json) in Driver’s repository settings after the first reviewed installation. The reference requires linear history and squash merges, prevents branch deletion and force pushes, blocks all code-quality severities with AI findings set to none, and requires at least 80% coverage with a maximum drop of 10. These GitHub rules complement the CI coverage gate. [Invoicing’s published ruleset](https://github.com/niobium-nz/Niobium.Invoicing/rules/26202562)

Protect `main` with PR-only changes, one approval, stale approval dismissal, conversation resolution, up-to-date branches, required deployment to `test`, and the `Test deployment verified` status from GitHub Actions. Keep the bypass list empty. The reference leaves signed commits optional. In Actions settings, disable **Allow GitHub Actions to create and approve pull requests**. The trusted controller publishes deployment evidence. A separate eligible reviewer makes the approval decision.

For this repository, enable native code review for **All PRs** on **every push** and security review for **All PRs** **whenever code review runs**. Verify the settings by observing the first review receipt on the actual PR head. A GitHub reviewer request assigns a reviewer. The implementation session is responsible for invoking the approver and verifying the formal decision. [Codex code review settings](https://chatgpt.com/settings/code-review)

## First installation

The readiness controller runs from the immutable commit on `main`. It authenticates the feature pipeline and checks that a separate runner actually executed the reviewed harness from that same commit. Driver's initial `main` contains only the license and ignore file. The first candidate therefore cannot produce the trusted status. An administrator must authorize installation of the independently reviewed controller and harness using the candidate test evidence and formal approval. Record that decision, apply the full protections and verify the trusted check on a subsequent feature commit. Protected pipeline changes likewise require reviewed activation. A skipped trusted step cannot establish deployment verification.

[Invoicing approval handoff at the source revision](https://github.com/niobium-nz/Niobium.Invoicing/blob/0b4ffa7a7c6d255e7d3f0db7553cc87a807959a9/docs/pr-approval-handoff.md)
