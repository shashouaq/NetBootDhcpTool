# Maintenance Guide

This guide is the operating standard for maintaining NetBoot DHCP Tool. Follow it for every code, configuration, documentation, packaging, and release change.

## Repository

- GitHub repository: `https://github.com/shashouaq/NetBootDhcpTool`
- Default branch: `main`
- Release tag format: `v<version>`
- Current application version: `1.0.8`
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
- Every finished change must be committed and pushed to GitHub before handoff.
- Every version upgrade must update local release artifacts and GitHub Release assets.

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

5. Commit and push:

```powershell
git add <changed-files>
git commit -m "<short change summary>"
git push
```

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
- open the manifest's validated `downloadUrl` in the system browser for direct website download
- accept only HTTPS GitHub release URLs and a valid SHA256 field; it does not install files automatically

Automatic download, checksum verification of the local file, replacement, and rollback remain out of scope until a signed-update installer is designed.

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
