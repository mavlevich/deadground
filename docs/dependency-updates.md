# Dependency updates

## Version sources

- `global.json` pins the .NET SDK and disallows prerelease SDK selection.
- `src/Game.Godot/Game.Godot.csproj` pins `Godot.NET.Sdk`. This is also the single source for the
  matching Godot editor, export template download and launcher version checks.
- Shell and PowerShell tools use `tools/godot-version.sh` and `tools/godot-version.ps1`.
- CI downloads the editor version returned by the same helpers.

The Godot editor writes an explicit SDK version back into the client project during import.
Keeping that project as the canonical pin avoids fighting the editor. Godot SDK versions have
three components; engine release tags omit a zero patch. For example, SDK `4.8.0` maps to
engine tag `4.8-stable` and templates folder `4.8.stable.mono`.

## Renovate policy

`renovate.json` monitors NuGet libraries, the .NET SDK, the Godot SDK, GitHub Actions and the
pinned Renovate configuration validator. Updates are checked weekly on Monday morning in
Europe/Warsaw. Unstable versions are ignored and automerge is disabled.

- Godot updates get their own toolchain PR. Updating the SDK pin changes the editor and template
  versions consumed by scripts and CI without independently replacing download URL literals.
- Minor and patch library updates are grouped together.
- GitHub Actions and the .NET SDK have separate groups.
- Major updates require Dependency Dashboard approval before opening a PR.
- Configuration changes run the official strict Renovate validator in a separate CI workflow.

After this configuration is merged, a repository owner must install or enable the Renovate GitHub
App for this repository and finish onboarding. Committing a config file alone does not activate
the hosted bot. This change does not install an app or change repository permissions.

Official setup: https://docs.renovatebot.com/getting-started/installing-onboarding/

## Accepting an engine update

1. Check the engine release notes and .NET compatibility.
2. Run the required build, formatting, tests and client/server smoke checks from `AGENTS.md`.
3. Run the version helper and launcher/export regression checks:

   ```sh
   bash tests/toolchain-version-smoke.sh
   bash tests/launcher-smoke.sh
   bash tests/export-smoke.sh
   ```

   Windows CI also runs `tests/toolchain-version-smoke.ps1`.

4. Confirm CI exports macOS and Windows clients and their exported smoke tests pass.
5. Run `bash tests/vehicle-rendering-smoke.sh` on a desktop with a real renderer.
6. Play the tank battle, checking input, visibility, effects, movement, LOD changes and restart.

Godot updates require a human merge. Headless tests do not exercise GPU resource allocation.
