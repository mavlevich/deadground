#!/usr/bin/env bash
set -euo pipefail

VERSION="4.7.2.stable.mono"
RELEASE_TAG="4.7.2-stable"
TEMPLATES_URL="https://github.com/godotengine/godot-builds/releases/download/${RELEASE_TAG}/Godot_v${RELEASE_TAG}_mono_export_templates.tpz"

force=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --force|-f)
      force=1
      shift
      ;;
    *)
      echo "Unknown option: $1" >&2
      exit 1
      ;;
  esac
done

uname_s="$(uname -s)"
case "$uname_s" in
  Darwin)
    target_dir="$HOME/Library/Application Support/Godot/export_templates/$VERSION"
    ;;
  Linux)
    target_dir="${XDG_DATA_HOME:-$HOME/.local/share}/godot/export_templates/$VERSION"
    ;;
  MINGW*|MSYS*|CYGWIN*)
    target_dir="${APPDATA:-$HOME/AppData/Roaming}/Godot/export_templates/$VERSION"
    ;;
  *)
    echo "Unsupported OS: $uname_s" >&2
    exit 1
    ;;
esac

if [[ $force -eq 0 && -f "$target_dir/version.txt" ]]; then
  existing_version="$(cat "$target_dir/version.txt" | tr -d '\r\n')"
  if [[ "$existing_version" == "$VERSION" ]]; then
    echo "Export templates for $VERSION are already installed at:"
    echo "  $target_dir"
    exit 0
  fi
fi

echo "Installing Godot $VERSION export templates to:"
echo "  $target_dir"

tmp_file="$(mktemp /tmp/godot_templates_XXXXXX.tpz)"
trap 'rm -f "$tmp_file"' EXIT

echo "Downloading export templates..."
curl -fSL --progress-bar -o "$tmp_file" "$TEMPLATES_URL"

mkdir -p "$target_dir"
echo "Extracting templates..."
unzip -q -j -o "$tmp_file" "templates/*" -d "$target_dir"

if [[ ! -f "$target_dir/version.txt" ]]; then
  echo "Error: version.txt missing after extraction." >&2
  exit 1
fi

echo "Godot $VERSION export templates installed successfully."
