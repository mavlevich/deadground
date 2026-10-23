#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/tools" "$fixture/bin" "$fixture/src/Game.Godot"
cp "$repo_root/tools/godot-version.sh" "$repo_root/tools/run-client.sh" "$repo_root/tools/export-client.sh" "$repo_root/tools/install-templates.sh" "$fixture/tools/"
cat > "$fixture/bin/dotnet" <<'MOCK'
#!/usr/bin/env bash
[[ "$1" == build && "$2" == src/Game.Godot/Game.Godot.csproj ]]
MOCK
cat > "$fixture/bin/godot" <<'MOCK'
#!/usr/bin/env bash
if [[ "$*" == '--version' ]]; then
  echo "$EXPECTED_VERSION.stable.mono.test"
  exit 0
fi
printf 'run\n' >> "$EXPECTED_CALLS"
MOCK
cat > "$fixture/bin/uname" <<'MOCK'
#!/usr/bin/env bash
echo Linux
MOCK
cat > "$fixture/bin/curl" <<'MOCK'
#!/usr/bin/env bash
exit 99
MOCK
chmod +x "$fixture/bin/"*

for sdk_version in 4.8.0 4.8.1 5.0.0; do
  printf '<Project Sdk="Godot.NET.Sdk/%s">\n</Project>\n' "$sdk_version" > "$fixture/src/Game.Godot/Game.Godot.csproj"
  expected="${sdk_version%.0}"
  [[ "$(bash "$fixture/tools/godot-version.sh")" == "$expected" ]]
  export EXPECTED_VERSION="$expected" EXPECTED_CALLS="$fixture/calls" GODOT_BIN="$fixture/bin/godot"
  PATH="$fixture/bin:$PATH" bash "$fixture/tools/run-client.sh" --headless -- --smoke-test
  PATH="$fixture/bin:$PATH" bash "$fixture/tools/export-client.sh" macOS "$fixture/output/game.zip"
  templates="$fixture/data/godot/export_templates/$expected.stable.mono"
  mkdir -p "$templates"
  printf '%s.stable.mono\n' "$expected" > "$templates/version.txt"
  PATH="$fixture/bin:$PATH" XDG_DATA_HOME="$fixture/data" bash "$fixture/tools/install-templates.sh"
  # Two launcher calls (import, run) and one export call use the updated pin without script edits.
  [[ "$(wc -l < "$fixture/calls" | tr -d ' ')" == 3 ]]
  rm "$fixture/calls"

  # A mismatched installed editor must fail before import or export.
  for wrong_editor in 0.0.1 "$expected.1"; do
    export EXPECTED_VERSION="$wrong_editor"
    if PATH="$fixture/bin:$PATH" bash "$fixture/tools/run-client.sh" > /dev/null 2>&1; then
      echo 'Launcher accepted a mismatched editor.' >&2
      exit 1
    fi
    if PATH="$fixture/bin:$PATH" bash "$fixture/tools/export-client.sh" > /dev/null 2>&1; then
      echo 'Exporter accepted a mismatched editor.' >&2
      exit 1
    fi
    [[ ! -e "$fixture/calls" ]]
  done
done

for bad_version in 4.8.0-beta1 4.8 latest ''; do
  printf '<Project Sdk="Godot.NET.Sdk/%s">\n</Project>\n' "$bad_version" > "$fixture/src/Game.Godot/Game.Godot.csproj"
  if bash "$fixture/tools/godot-version.sh" > /dev/null 2>&1; then
    echo "Version helper accepted an invalid SDK pin: $bad_version" >&2
    exit 1
  fi
done
echo 'TOOLCHAIN_VERSION_SMOKE_OK'
