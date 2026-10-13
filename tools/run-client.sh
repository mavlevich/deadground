#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
godot_bin="${GODOT_BIN:-}"
if [[ -z "$godot_bin" ]]; then
  if command -v godot >/dev/null 2>&1; then
    godot_bin="$(command -v godot)"
  elif [[ -x /Applications/Godot_mono.app/Contents/MacOS/Godot ]]; then
    godot_bin=/Applications/Godot_mono.app/Contents/MacOS/Godot
  else
    echo 'Set GODOT_BIN to the Godot 4.7.2 .NET executable.' >&2
    exit 1
  fi
fi
if [[ "$("$godot_bin" --version)" != 4.7.2.*.mono.* ]]; then
  echo 'This project requires Godot 4.7.2 .NET. Check GODOT_BIN.' >&2
  exit 1
fi
cd "$repo_root"
dotnet build src/Game.Godot/Game.Godot.csproj
"$godot_bin" --headless --editor --path "$repo_root/src/Game.Godot" --import
exec "$godot_bin" --path "$repo_root/src/Game.Godot" "$@"
