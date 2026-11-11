#!/usr/bin/env bash
# Cross-compile and test on Linux. The WPF app is a Windows executable; this script does not run the UI.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
if [[ -x "${HOME}/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="${HOME}/.dotnet"
  export PATH="${HOME}/.dotnet:${PATH}"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet test "${ROOT}/PerfMonNext.sln" -c Release
dotnet build "${ROOT}/src/PerfMonNext.App/PerfMonNext.App.csproj" -c Release
echo "Built PerfMonNext (Windows target). Run it on Windows with: dotnet run --project src/PerfMonNext.App"
