#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
godot_version="$(bash "$repo_root/tools/godot-version.sh")"
godot_bin="${GODOT_BIN:-}"
if [[ -z "$godot_bin" ]]; then
  if command -v godot >/dev/null 2>&1; then
    godot_bin="$(command -v godot)"
  elif [[ -x /Applications/Godot_mono.app/Contents/MacOS/Godot ]]; then
    godot_bin=/Applications/Godot_mono.app/Contents/MacOS/Godot
  else
    echo "Set GODOT_BIN to the Godot $godot_version .NET executable." >&2
    exit 1
  fi
fi
if [[ "$("$godot_bin" --version)" != "$godot_version".stable.mono.* ]]; then
  echo "This project requires Godot $godot_version .NET. Check GODOT_BIN." >&2
  exit 1
fi
cd "$repo_root"
dotnet build src/Game.Godot/Game.Godot.csproj
"$godot_bin" --headless --editor --path "$repo_root/src/Game.Godot" --import
exec "$godot_bin" --path "$repo_root/src/Game.Godot" "$@"
