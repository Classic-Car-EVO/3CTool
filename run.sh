#!/usr/bin/env bash
# Builds (if needed) and starts the ThreeCTool desktop app. Extra arguments are passed to the app.
set -euo pipefail

cd "$(dirname "$0")"

if ! command -v dotnet > /dev/null && [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
fi

exec dotnet run --project src/ThreeCTool.Desktop -c Release -- "$@"
