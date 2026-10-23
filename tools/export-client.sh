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

preset=""
output=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --preset|-p)
      preset="$2"
      shift 2
      ;;
    --output|-o)
      output="$2"
      shift 2
      ;;
    macOS|macos|mac|"macOS (Apple Silicon)")
      preset="macOS"
      shift
      ;;
    "Windows Desktop"|windows|win|"Windows")
      preset="Windows Desktop"
      shift
      ;;
    *)
      if [[ -z "$preset" ]]; then
        preset="$1"
      elif [[ -z "$output" ]]; then
        output="$1"
      else
        echo "Unexpected argument: $1" >&2
        exit 1
      fi
      shift
      ;;
  esac
done

if [[ -z "$preset" ]]; then
  preset="macOS"
fi

if [[ "$preset" == "macos" || "$preset" == "mac" ]]; then
  preset="macOS"
elif [[ "$preset" == "windows" || "$preset" == "win" ]]; then
  preset="Windows Desktop"
fi

if [[ -z "$output" ]]; then
  if [[ "$preset" == "macOS" ]]; then
    output="$repo_root/build/export/macos/DeadGround.zip"
  else
    output="$repo_root/build/export/windows/DeadGround.exe"
  fi
fi

if [[ "$output" != /* ]]; then
  output="$repo_root/$output"
fi

mkdir -p "$(dirname "$output")"
cd "$repo_root"

echo "Building C# solution for Godot client..."
dotnet build src/Game.Godot/Game.Godot.csproj --configuration Release

echo "Exporting preset '$preset' to '$output'..."
"$godot_bin" --headless --path "$repo_root/src/Game.Godot" --export-release "$preset" "$output"
echo "Export completed successfully: $output"
