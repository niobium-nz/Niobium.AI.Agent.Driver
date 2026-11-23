#!/usr/bin/env bash
set -euo pipefail
mkdir -p artifacts/compatibility
azurite --silent --skipApiVersionCheck --location "$RUNNER_TEMP/agent-gateway-azurite" > artifacts/compatibility/azurite.log 2>&1 &
azurite_pid=$!
func_pid=''
cleanup() {
  [[ -z "$func_pid" ]] || kill "$func_pid" 2>/dev/null || true
  kill "$azurite_pid" 2>/dev/null || true
}
trap cleanup EXIT
for _ in {1..40}; do
  if curl --silent --output /dev/null http://127.0.0.1:10002/devstoreaccount1; then break; fi
  sleep 1
done
dotnet test --project src/Niobium.AI.Agent.Driver.Integration.Tests/Niobium.AI.Agent.Driver.Integration.Tests.csproj --configuration Release --no-build --no-restore --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory artifacts/local-integration
dotnet publish src/Niobium.AI.Agent.Driver.Server/Niobium.AI.Agent.Driver.Server.csproj --configuration Release --output artifacts/host /p:PublishReadyToRun=false
export FUNCTIONS_WORKER_RUNTIME=dotnet-isolated
export AzureWebJobsStorage=UseDevelopmentStorage=true
export AgentGatewayOptions__Enabled=false
func start --script-root artifacts/host --no-build --port 7071 > artifacts/compatibility/functions-host.log 2>&1 &
func_pid=$!
ready=false
for _ in {1..60}; do
  if curl --fail --silent --output /dev/null http://127.0.0.1:7071/health; then ready=true; break; fi
  kill -0 "$func_pid" 2>/dev/null || break
  sleep 1
done
[[ "$ready" == true ]] || { echo 'Native Functions host did not become ready' >&2; exit 1; }
node scripts/test-mcp-wire.mjs | tee artifacts/compatibility/mcp-wire.json
func --version > artifacts/compatibility/functions-core-tools-version.txt
dotnet list src/Niobium.AI.Agent.Driver.Server/Niobium.AI.Agent.Driver.Server.csproj package --include-transitive --format json > artifacts/compatibility/package-graph.json
