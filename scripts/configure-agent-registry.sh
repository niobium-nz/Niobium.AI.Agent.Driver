#!/bin/sh
# Run locally under the operator's Azure identity. Never supply provider tokens as CLI arguments.
set -eu
if [ "$#" -ne 1 ]; then
  printf '%s\n' 'Usage: scripts/configure-agent-registry.sh reviewed-change.json' >&2
  exit 2
fi
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
exec dotnet run --project "$root/src/Niobium.AI.Agent.Driver.Server/Niobium.AI.Agent.Driver.Server.csproj" --configuration Release -- --configure-registry "$1"
