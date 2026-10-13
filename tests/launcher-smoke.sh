#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/bin" "$fixture/Godot With Spaces"
cat > "$fixture/bin/dotnet" <<'MOCK'
#!/usr/bin/env bash
[[ "$*" == 'build src/Game.Godot/Game.Godot.csproj' ]]
MOCK
cat > "$fixture/Godot With Spaces/Godot" <<'MOCK'
#!/usr/bin/env bash
if [[ "$*" == '--version' ]]; then
  echo '4.7.2.stable.mono.test'
  exit 0
fi
if [[ $# -eq 5 && "$1" == '--headless' && "$2" == '--editor' && "$3" == '--path' &&
      "$4" == "$EXPECTED_PROJECT" && "$5" == '--import' ]]; then
  printf 'import\n' >> "$EXPECTED_CALLS"
  exit 0
fi
[[ "$(cat "$EXPECTED_CALLS")" == 'import' ]]
[[ $# -eq 5 && "$1" == '--path' && "$2" == "$EXPECTED_PROJECT" &&
   "$3" == '--headless' && "$4" == '--' && "$5" == '--smoke-test' ]]
printf 'run\n' >> "$EXPECTED_CALLS"
MOCK
chmod +x "$fixture/bin/dotnet" "$fixture/Godot With Spaces/Godot"
PATH="$fixture/bin:$PATH" GODOT_BIN="$fixture/Godot With Spaces/Godot" \
  EXPECTED_PROJECT="$repo_root/src/Game.Godot" \
  EXPECTED_CALLS="$fixture/calls" \
  bash "$repo_root/tools/run-client.sh" --headless -- --smoke-test
[[ "$(cat "$fixture/calls")" == $'import\nrun' ]]
echo 'LAUNCHER_SMOKE_OK'
