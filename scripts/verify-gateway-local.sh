#!/usr/bin/env bash
# The integration assembly owns its pinned local services in CI, packaging and local runs.
set -euo pipefail
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$root"
dotnet test --project src/Niobium.AI.Agent.Driver.Integration.Tests/Niobium.AI.Agent.Driver.Integration.Tests.csproj --configuration Release --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory artifacts/local-integration
