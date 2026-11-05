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
no_build=0
no_import=0
godot_args=()
seen_dash_dash=0

while [[ $# -gt 0 ]]; do
  if [[ "$seen_dash_dash" -eq 1 ]]; then
    godot_args+=("$1")
    shift
    continue
  fi

  case "$1" in
    --)
      seen_dash_dash=1
      godot_args+=("$1")
      shift
      ;;
    --no-build)
      no_build=1
      shift
      ;;
    --no-import)
      no_import=1
      shift
      ;;
    *)
      godot_args+=("$1")
      shift
      ;;
  esac
done

build_output="$repo_root/src/Game.Godot/.godot/mono/temp/bin/Debug/Game.Godot.dll"
if [[ "$no_build" -eq 1 && ! -f "$build_output" ]]; then
  echo "Cannot skip build: build output '$build_output' not found. Run without --no-build first." >&2
  exit 1
fi

import_cache="$repo_root/src/Game.Godot/.godot/imported"
if [[ "$no_import" -eq 1 && ! -d "$import_cache" ]]; then
  echo "Cannot skip import: import cache '$import_cache' not found. Run without --no-import first." >&2
  exit 1
fi

cd "$repo_root"

if [[ "$no_build" -eq 0 ]]; then
  dotnet build src/Game.Godot/Game.Godot.csproj
fi

if [[ "$no_import" -eq 0 ]]; then
  "$godot_bin" --headless --editor --path "$repo_root/src/Game.Godot" --import
fi

if [[ ${#godot_args[@]} -gt 0 ]]; then
  exec "$godot_bin" --path "$repo_root/src/Game.Godot" "${godot_args[@]}"
else
  exec "$godot_bin" --path "$repo_root/src/Game.Godot"
fi
