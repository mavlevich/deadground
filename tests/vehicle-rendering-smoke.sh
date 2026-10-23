#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
log="$(mktemp)"
trap 'rm -f "$log"' EXIT
# No --headless: a dummy renderer cannot exercise instance-uniform allocation.
if ! "$repo_root/tools/run-client.sh" --quit-after 600 res://VehicleRenderingProbe.tscn > "$log" 2>&1; then
  cat "$log"
  exit 1
fi
cat "$log"
if grep -Eq '(^ERROR:|VEHICLE_RENDER_STRESS_FAIL|Too many instances using shader instance variables)' "$log"; then
  exit 1
fi
grep -q '^VEHICLE_RENDER_STRESS_OK ' "$log"
