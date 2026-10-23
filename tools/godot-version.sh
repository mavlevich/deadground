#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
sdk_version="$(sed -nE 's@.*Sdk="Godot\.NET\.Sdk/([0-9]+\.[0-9]+\.[0-9]+)".*@\1@p' "$repo_root/src/Game.Godot/Game.Godot.csproj" | tr -d '\r')"
if [[ ! "$sdk_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo 'Game.Godot.csproj must pin one stable Godot.NET.Sdk version.' >&2
  exit 1
fi
# Godot release tags and template folders omit the zero patch: SDK 4.8.0 uses 4.8-stable.
echo "${sdk_version%.0}"
