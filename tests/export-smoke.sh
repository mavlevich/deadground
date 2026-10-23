#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
export EXPECTED_GODOT_VERSION="$(bash "$repo_root/tools/godot-version.sh").stable.mono.test"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT

mkdir -p "$fixture/bin" "$fixture/Godot With Spaces"

cat > "$fixture/bin/dotnet" <<'MOCK'
#!/usr/bin/env bash
[[ "$*" == 'build src/Game.Godot/Game.Godot.csproj --configuration Release' ]]
MOCK

cat > "$fixture/Godot With Spaces/Godot" <<'MOCK'
#!/usr/bin/env bash
if [[ "$*" == '--version' ]]; then
  echo "$EXPECTED_GODOT_VERSION"
  exit 0
fi

[[ $# -eq 6 && "$1" == '--headless' && "$2" == '--path' && "$3" == "$EXPECTED_PROJECT" &&
   "$4" == '--export-release' && "$5" == "$EXPECTED_PRESET" && "$6" == "$EXPECTED_OUTPUT" ]]
MOCK

chmod +x "$fixture/bin/dotnet" "$fixture/Godot With Spaces/Godot"

# 1. Test default macOS export
PATH="$fixture/bin:$PATH" GODOT_BIN="$fixture/Godot With Spaces/Godot" \
  EXPECTED_PROJECT="$repo_root/src/Game.Godot" \
  EXPECTED_PRESET="macOS" \
  EXPECTED_OUTPUT="$repo_root/build/export/macos/DeadGround.zip" \
  bash "$repo_root/tools/export-client.sh"

# 2. Test Windows export
PATH="$fixture/bin:$PATH" GODOT_BIN="$fixture/Godot With Spaces/Godot" \
  EXPECTED_PROJECT="$repo_root/src/Game.Godot" \
  EXPECTED_PRESET="Windows Desktop" \
  EXPECTED_OUTPUT="$repo_root/build/export/windows/DeadGround.exe" \
  bash "$repo_root/tools/export-client.sh" "Windows Desktop"

# 3. Test custom output
PATH="$fixture/bin:$PATH" GODOT_BIN="$fixture/Godot With Spaces/Godot" \
  EXPECTED_PROJECT="$repo_root/src/Game.Godot" \
  EXPECTED_PRESET="macOS" \
  EXPECTED_OUTPUT="$fixture/custom/game.zip" \
  bash "$repo_root/tools/export-client.sh" --preset macOS --output "$fixture/custom/game.zip"

# Verify the packaging settings that previously passed with ignored architecture keys
# and a Release-to-Debug solution mapping.
presets="$repo_root/src/Game.Godot/export_presets.cfg"
[[ "$(awk '/^\[preset.0.options\]/{active=1;next} /^\[/{active=0} active && /^binary_format\/architecture=/{print}' "$presets")" == 'binary_format/architecture="universal"' ]]
[[ "$(awk '/^\[preset.1.options\]/{active=1;next} /^\[/{active=0} active && /^binary_format\/architecture=/{print}' "$presets")" == 'binary_format/architecture="x86_64"' ]]
if grep -q '^architecture/target=' "$presets"; then
  echo 'Export presets must use binary_format/architecture.' >&2
  exit 1
fi

solution="$repo_root/src/Game.Godot/Game.Godot.sln"
client_guid="$(awk -F'"' '/= "Game.Godot",/{print $8}' "$solution")"
[[ -n "$client_guid" ]]
for platform in 'Any CPU' x64 x86; do
  for mapping in ActiveCfg Build.0; do
    grep -Fq "$client_guid.Release|$platform.$mapping = ExportRelease|Any CPU" "$solution"
  done
done

echo 'EXPORT_SMOKE_OK'
