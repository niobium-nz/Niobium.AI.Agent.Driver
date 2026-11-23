#!/usr/bin/env bash
# Run the complete prepackage test suite with an emulator owned by this invocation.
# azd invokes this through npm exec with the exact Azurite version; no cloud credentials
# or configurable storage endpoint are used by the persistence tests.
set -euo pipefail
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
command -v azurite >/dev/null || { echo 'Azurite is required. Use the azd prepackage hook.' >&2; exit 2; }
mkdir -p "$root/artifacts/packaging"
for port in 10000 10001 10002; do
  if curl --silent --output /dev/null --max-time 1 "http://127.0.0.1:$port/devstoreaccount1"; then
    echo "Port $port is already in use. Stop the existing emulator before packaging." >&2
    exit 2
  fi
done
data=$(mktemp -d "${TMPDIR:-/tmp}/niobium-packaging.XXXXXX")
azurite_pid=''
cleanup() {
  if [[ -n "$azurite_pid" ]]; then
    kill "$azurite_pid" 2>/dev/null || true
    wait "$azurite_pid" 2>/dev/null || true
  fi
  rm -rf -- "$data"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
azurite --silent --skipApiVersionCheck --location "$data" > "$root/artifacts/packaging/azurite.log" 2>&1 &
azurite_pid=$!
ready=false
for _ in {1..40}; do
  kill -0 "$azurite_pid" 2>/dev/null || break
  if curl --silent --output /dev/null --max-time 1 http://127.0.0.1:10002/devstoreaccount1; then
    ready=true
    break
  fi
  sleep 1
done
[[ "$ready" == true ]] || { echo 'Packaging emulator failed to become ready; see artifacts/packaging/azurite.log.' >&2; exit 1; }
dotnet test --solution "$root/Niobium.AI.Agent.Driver.slnx" --configuration Release
