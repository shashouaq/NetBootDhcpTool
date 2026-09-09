# Maintenance Guide

This guide is the operating standard for maintaining NetBoot DHCP Tool. Follow it for every code, configuration, documentation, packaging, and release change.

## Repository

- GitHub repository: `https://github.com/shashouaq/NetBootDhcpTool`
- Default branch: `main`
- Release tag format: `v<version>`
- Current application version: `1.0.10`
- Current release manifest URL:

```text
https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json
```

## Non-Negotiable Maintenance Rules

- Inspect the existing code and documents before editing.
- Keep changes scoped to the requested work.
- Preserve unrelated user changes.
- Record every code, config, document, test, build, packaging, or release change in `docs/FEATURE_CHANGELOG.md`.
- Do not mark work complete until the change log entry exists.
- Run relevant validation when feasible.
- For documentation-only changes, run lightweight checks instead of a full build unless the docs affect packaging or release behavior.
- Unless the user explicitly says to publish/release, finish every completed change by building and opening the local program for preview; do not push or publish externally.
- Before opening the preview, always check whether `NetBootDhcpTool` is already running. If it is running, close and restart it from the current source `Release` output; if it is not running, start it from the current source `Release` output. `build\\run-app-admin.ps1` prefers `src\\NetBootDhcpTool.App\\bin\\Release\\net8.0-windows` and falls back to the packaged `release\\NetBootDhcpTool` output only when the source build is absent.
- Only when the user explicitly says to publish/release may source, packages, tags, or GitHub Releases be pushed. After publishing, verify the remote commit/release/assets and report the exact GitHub URL or commit; never claim publication from a local command alone.
- Every version upgrade must update local release artifacts and GitHub Release assets.
- Every application run must create a new UTF-8 session log whose filename contains the start timestamp; user actions and slow-operation start/completion, elapsed time, and errors must be traceable in that run's log.
- Prioritize Chinese/English bilingual support for every future user-visible change, including UI labels, buttons, dialogs, status messages, logs, help text, and maintenance documentation. Reuse the language resources where practical; do not add Chinese-only or English-only text without documenting the reason.
- Every user-facing application button must have a separate compact round `?` help button beside it. Keep the indicator small and visible by default; hovering the action or indicator shows a contextual Chinese/English explanation, and clicking the indicator opens the same explanation in a dialog without executing the neighboring action. Run-time-created buttons must use `HelpButtonService.Attach`.
- Before handoff, compare the keys in `src\NetBootDhcpTool.Core\Defaults.cs` with both `i18n\zh-CN.json` and `i18n\en-US.json`; missing or extra language keys are a localization defect.
- Adapter restart and MAC changes are explicit administrator-level operations. They must log the selected adapter identity, requested action, result, and error; settings switches control whether wireless, virtual, disconnected, or other adapter types may be targeted.
- MAC restoration is session-scoped: only a normal application exit restores adapters whose per-change checkbox is selected; forced termination and system crashes cannot guarantee restoration.
- `docs/FEATURE_CHANGELOG.md` is the source of truth for every code, configuration, documentation, test, build, packaging, and release change. Keep new work in its `Unreleased` section until a version is deliberately released; `build/publish.ps1` carries that section into `latest.json` as release notes and change items.
- Manufacturer-published BMC preset credentials may be stored in plaintext only for entries explicitly marked as public defaults. Model-dependent values must say to use the chassis label/manual and must never be guessed.

## Standard Change Workflow

1. Check local state:

```powershell
git status -sb
git pull --ff-only
```

2. Make the scoped change.
3. Update `docs/FEATURE_CHANGELOG.md` with:

- date
- type
- affected files/modules
- concrete change
- verification
- user impact

4. Run validation:

```powershell
$dotnet = .\build\resolve-dotnet.ps1
& $dotnet build .\NetBootDhcpTool.sln -c Release --no-restore
& $dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release
```

For documentation-only changes, at minimum run:

```powershell
git diff --check
git status -sb
```

5. Commit and push only after the user explicitly authorizes publication:

```powershell
git add <changed-files>
git commit -m "<short change summary>"
git push
```

For ordinary changes without explicit publication authorization, stop after local validation and run `build\\run-app-admin.ps1` for user preview. The script detects an existing `NetBootDhcpTool` process and restarts it; otherwise it opens a new elevated instance. It opens the current source `Release` build when available, so a stale packaged directory is not used accidentally.

## Version Upgrade Workflow

Use this workflow when changing the published version.

1. Update the application version in `src/NetBootDhcpTool.App/NetBootDhcpTool.App.csproj`.
2. Search for the old version and update intentional references:

```powershell
rg -n "<old-version>|v<old-version>" .
```

3. Add a complete change log entry in `docs/FEATURE_CHANGELOG.md`.
4. Generate release notes from the change log in `docs/RELEASE_NOTES.md`.
5. Run validation:

```powershell
$dotnet = .\build\resolve-dotnet.ps1
& $dotnet build .\NetBootDhcpTool.sln -c Release --no-restore
& $dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release
```

6. Publish local artifacts:

```powershell
.\build\stop-test-processes.ps1
.\build\publish.ps1 -GitHubRepository shashouaq/NetBootDhcpTool
```

7. Commit and push source:

```powershell
git add .
git commit -m "release v<version>"
git push
git tag v<version>
git push origin v<version>
```

8. Create or update the GitHub Release:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" release create v<version> `
  release\NetBootDhcpTool-v<version>.7z `
  release\NetBootDhcpTool-v<version>.7z.sha256 `
  release\latest.json `
  --repo shashouaq/NetBootDhcpTool `
  --title "NetBoot DHCP Tool v<version>" `
  --notes-file docs\RELEASE_NOTES.md
```

If the release already exists, upload assets with overwrite:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" release upload v<version> `
  release\NetBootDhcpTool-v<version>.7z `
  release\NetBootDhcpTool-v<version>.7z.sha256 `
  release\latest.json `
  --repo shashouaq/NetBootDhcpTool `
  --clobber
```

9. Verify GitHub assets and upgrade manifest:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" release view v<version> --repo shashouaq/NetBootDhcpTool
$r = Invoke-WebRequest -Uri "https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json" -UseBasicParsing
[System.Text.Encoding]::UTF8.GetString($r.Content)
```

## Local Cleanup Policy

- Keep the two newest versioned releases in `release/` for rollback and active troubleshooting; remove older versions.
- Keep:
  - `release\NetBootDhcpTool`
  - `release\NetBootDhcpTool-tools`
  - `release\NetBootDhcpTool-v<newest-version>`
  - `release\NetBootDhcpTool-v<newest-version>.7z`
  - `release\NetBootDhcpTool-v<newest-version>.7z.sha256`
  - `release\NetBootDhcpTool-v<previous-version>`
  - `release\NetBootDhcpTool-v<previous-version>.7z`
  - `release\NetBootDhcpTool-v<previous-version>.7z.sha256`
  - `release\latest.json`
  - `release\README_RUN.txt`
- `build\clean.ps1` removes stale build caches and release assets older than the two newest versions while preserving the canonical current outputs; run `build\publish.ps1` afterward when the current output itself needs rebuilding.
- Remove stale `src/**/bin` and `src/**/obj` caches when preparing a clean workspace.
- Do not commit `release/`, `.dotnet/`, `logs/`, `bin/`, or `obj/`.

## Upgrade Detection Contract

Future in-app upgrade detection must consume:

```text
https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json
```

The app now:

- compare `version` with the running app version
- display the running version immediately at startup
- show a clickable `有新版本！` / `New version available!` link beside the version when a newer validated release exists
- show the manifest `releaseNotes`/`changes` in a confirmation dialog, then download the validated archive in the background to the user's Downloads folder
- accept only HTTPS GitHub release URLs and a valid SHA256 field; it does not install files automatically

The archive is written to a temporary `.download` file and SHA-256 verified before it is moved into place. If speed remains below 3 KB/s for 10 seconds, the UI only displays `1406829360@qq.com`; it never sends email automatically. Future automatic installation requires signed-update and rollback controls.

## Troubleshooting

- If `gh` is not found, use the installed path:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" auth status
```

- If the token is invalid, re-authenticate:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" auth logout -h github.com -u shashouaq
& "C:\Program Files\GitHub CLI\gh.exe" auth login -h github.com -w
```

- If publish fails because files are locked, stop app processes:

```powershell
.\build\stop-test-processes.ps1
```
