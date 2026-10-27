#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
output_dir="${1:-"$repo_root/artifacts/telemetry"}"
mkdir -p "$output_dir"
exec dotnet run --project "$repo_root/src/Game.Server" --configuration Release --no-restore -- --generate-steering-telemetry "$output_dir"
