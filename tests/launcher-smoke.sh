#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
export EXPECTED_GODOT_VERSION="$(bash "$repo_root/tools/godot-version.sh").stable.mono.test"
sdk_raw_version="$(sed -nE 's@.*Sdk="Godot\.NET\.Sdk/([0-9]+\.[0-9]+\.[0-9]+)".*@\1@p' "$repo_root/src/Game.Godot/Game.Godot.csproj" | tr -d '\r')"

fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/bin" "$fixture/Godot With Spaces"

cat > "$fixture/bin/dotnet" <<'MOCK'
#!/usr/bin/env bash
[[ "$*" == 'build src/Game.Godot/Game.Godot.csproj' ]]
printf 'dotnet\n' >> "$EXPECTED_CALLS"
MOCK

cat > "$fixture/Godot With Spaces/Godot" <<'MOCK'
#!/usr/bin/env bash
if [[ "$*" == '--version' ]]; then
  echo "$EXPECTED_GODOT_VERSION"
  exit 0
fi
if [[ $# -eq 5 && "$1" == '--headless' && "$2" == '--editor' && "$3" == '--path' &&
      "$4" == "$EXPECTED_PROJECT" && "$5" == '--import' ]]; then
  printf 'import\n' >> "$EXPECTED_CALLS"
  exit 0
fi
[[ $# -eq 5 && "$1" == '--path' && "$2" == "$EXPECTED_PROJECT" &&
   "$3" == '--headless' && "$4" == '--' && "$5" == '--smoke-test' ]]
printf 'run\n' >> "$EXPECTED_CALLS"
MOCK
chmod +x "$fixture/bin/dotnet" "$fixture/Godot With Spaces/Godot"

# Create a self-contained fixture repo so tests do not depend on local build outputs
test_repo="$fixture/repo"
mkdir -p "$test_repo/tools" \
         "$test_repo/src/Game.Godot/.godot/mono/temp/bin/Debug" \
         "$test_repo/src/Game.Godot/.godot/imported"

cp "$repo_root/tools/godot-version.sh" "$test_repo/tools/"
cp "$repo_root/tools/run-client.sh" "$test_repo/tools/"
cat > "$test_repo/src/Game.Godot/Game.Godot.csproj" <<CSPROJ
<Project Sdk="Godot.NET.Sdk/${sdk_raw_version}">
</Project>
CSPROJ

dll_path="$test_repo/src/Game.Godot/.godot/mono/temp/bin/Debug/Game.Godot.dll"
touch "$dll_path"

run_test_client() {
  PATH="$fixture/bin:$PATH" GODOT_BIN="$fixture/Godot With Spaces/Godot" \
    EXPECTED_PROJECT="$test_repo/src/Game.Godot" \
    EXPECTED_CALLS="$fixture/calls" \
    bash "$test_repo/tools/run-client.sh" "$@"
}

# 1. Full run: build, import and run
: > "$fixture/calls"
run_test_client --headless -- --smoke-test
[[ "$(cat "$fixture/calls")" == $'dotnet\nimport\nrun' ]]

# 2. --no-build: skip dotnet build, run import and godot
: > "$fixture/calls"
run_test_client --no-build --headless -- --smoke-test
[[ "$(cat "$fixture/calls")" == $'import\nrun' ]]

# 3. --no-import: run dotnet build, skip import, run godot
: > "$fixture/calls"
run_test_client --no-import --headless -- --smoke-test
[[ "$(cat "$fixture/calls")" == $'dotnet\nrun' ]]

# 4. --no-build --no-import: skip build and import, run godot
: > "$fixture/calls"
run_test_client --no-build --no-import --headless -- --smoke-test
[[ "$(cat "$fixture/calls")" == 'run' ]]

# 5. Error handling: --no-build fails if build output is missing
rm -f "$dll_path"
set +e
output="$(run_test_client --no-build 2>&1)"
status=$?
set -e
[[ $status -ne 0 ]]
[[ "$output" == *"Cannot skip build"* ]]
touch "$dll_path"

# 6. Error handling: --no-import fails if import cache is missing
rm -rf "$test_repo/src/Game.Godot/.godot/imported"
set +e
output="$(run_test_client --no-import 2>&1)"
status=$?
set -e
[[ $status -ne 0 ]]
[[ "$output" == *"Cannot skip import"* ]]

echo 'LAUNCHER_SMOKE_OK'
