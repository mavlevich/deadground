#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$repo_root/.git/hooks"
cp "$repo_root/tools/git-hooks/pre-push" "$repo_root/.git/hooks/pre-push"
chmod +x "$repo_root/.git/hooks/pre-push"
echo "Git pre-push hook installed."
