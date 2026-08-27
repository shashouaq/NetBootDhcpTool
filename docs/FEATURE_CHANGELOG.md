# Feature Change Log

## 2026-08-27

- Type: Feature / Security / Release preparation
- Affected files/modules: `src/NetBootDhcpTool.Core/CredentialProtector.cs`, `src/NetBootDhcpTool.Core/FavoriteStore.cs`, `src/NetBootDhcpTool.Core/Models.cs`, `src/NetBootDhcpTool.Core/VersionUpdateService.cs`, `src/NetBootDhcpTool.Network/NetworkAdapterService.cs`, `src/NetBootDhcpTool.Dhcp/DhcpServer.cs`, `src/NetBootDhcpTool.App/MainWindow.xaml`, `src/NetBootDhcpTool.App/MainWindow.xaml.cs`, `src/NetBootDhcpTool.App/MainWindowViewModel.cs`, `src/NetBootDhcpTool.App/FavoriteWindow.xaml`, `src/NetBootDhcpTool.App/FavoriteWindow.xaml.cs`, `src/NetBootDhcpTool.Tests/Program.cs`, `config/favorites.json`, `i18n/`, `README.md`, `PROJECT_MEMORY.md`, `docs/RELEASE_NOTES.md`, `docs/RELEASE_PROCESS.md`, `docs/MAINTENANCE_GUIDE.md`
- Concrete change: Bumped the application to v1.0.8. Replaced plaintext favorite-password persistence with current-user Windows DPAPI and legacy migration, redacted password UI and exports, and added per-favorite HTTPS opening. Captured/restored complete IPv4 address and default-route sets, made DHCP binding transactional, and added scoped firewall-rule lifecycle cleanup. Added the background validated GitHub update check and current-version display, moved binding collections into a view model, and expanded automated smoke coverage.
- Verification: Release build passed with 0 warnings and 0 errors; the expanded smoke suite passed local DPAPI migration/export, updater-manifest validation, DHCP UDP Discover/Offer/Request/ACK, DHCP bind-failure recovery, lease exhaustion, route normalization, and legacy snapshot checks. The v1.0.8 self-contained package is 63,449,475 bytes; 7-Zip testing passed, file version is 1.0.8.0, and SHA256 `adbc955aa2367853321c12f9cbe6c7801879ef3bde9d15df0ec838934b9040a5` matches both the sidecar and `latest.json`. Local cleanup leaves only v1.0.7 and v1.0.8 versioned archives/directories and no source bin/obj caches. Remote GitHub release verification follows after publication. The elevated Hyper-V route smoke was not run because this terminal is not administrator; no real adapter was modified.
- User impact: Existing local favorite passwords are upgraded on first load; exported JSON no longer carries credentials. The toolbar always shows the running version and exposes a direct website download link only after a newer HTTPS GitHub release manifest is validated. DHCP and adapter cleanup now leave fewer persistent side effects.

- Type: Repository housekeeping
- Affected files/modules: `build/publish-framework-dependent.ps1`, `build/clean.ps1`, `build/stop-test-processes.ps1`, `release/`, `docs/MAINTENANCE_GUIDE.md`
- Concrete change: Removed the unreferenced framework-dependent publishing helper, which only produced the retired `release\NetBootDhcpTool-fd` layout. Updated the clean workflow and maintenance policy to retain the two newest semantic-versioned release directories, archives, and checksum sidecars, preserve the canonical current outputs, and treat optional non-administrator pktmon cleanup as non-blocking. Audited the current release directory; `v1.0.6` and `v1.0.7` were already the only versioned archives, so no retained release asset was removed.
- Verification: Release build passed with 0 warnings and 0 errors; the existing smoke test returned `OK`; both retained 7z archives passed 7-Zip testing and their SHA-256 values matched the sidecar files; PowerShell syntax and `git diff --check` were verified.
- User impact: The documented cleanup path no longer leaves an obsolete packaging route, and routine cleaning will preserve the latest two rollback candidates.

- Type: Feature / Release
- Affected files/modules: `src/NetBootDhcpTool.Core/Models.cs`, `src/NetBootDhcpTool.Core/IpNetwork.cs`, `src/NetBootDhcpTool.Core/StaticRouteValidator.cs`, `src/NetBootDhcpTool.Network/StaticRouteService.cs`, `src/NetBootDhcpTool.App/MainWindow.xaml`, `src/NetBootDhcpTool.App/MainWindow.xaml.cs`, `i18n/`, `build/route-smoke.ps1`, `build/resolve-dotnet.ps1`, `README.md`, `docs/NetBootDhcpTool_OnePageGuide.html`, `PROJECT_MEMORY.md`
- Concrete change: Added a Static Routes tab for multiple IPv4 CIDR rules. Each rule selects a wired adapter and accepts either a blank gateway for a directly connected route or an IPv4 next hop for a gateway route. Prefixes are canonicalized, metrics are validated, duplicate/conflicting routes are rejected, default routes are allowed with an additional confirmation, and only routes created by the current session are journaled and removed on exit. Cleanup failures or adapter identity changes remain recoverable on the next startup. Added a controlled Hyper-V vNIC smoke-test script and made the build resolver prefer a project-local .NET 8 SDK.
- Verification: Project-local .NET SDK `8.0.424` restore and Release build passed with 0 warnings and 0 errors; existing test smoke returned `OK`; elevated isolated Hyper-V route smoke returned `ROUTE_SMOKE_OK` and `LEFTOVER_SWITCHES=0`; `git diff --check` and content/encoding review passed; the v1.0.7 self-contained package passed 7-Zip testing, SHA256 sidecar verification, manifest verification, and assembly file-version verification. Remote Release verification is completed below.
- User impact: Users can explicitly direct selected IPv4 networks through different wired adapters while keeping DHCP/manual adapter safety boundaries. Session-created routes do not persist as permanent application settings and are cleaned automatically on exit.

## 2026-07-02

- Type: Documentation
- Affected files/modules: `docs/MAINTENANCE_GUIDE.md`, `docs/RELEASE_PROCESS.md`, `README.md`, `docs/FEATURE_CHANGELOG.md`
- Concrete change: Added a maintenance guide covering standard change flow, required change-log entries, validation expectations, GitHub commit/push requirements, version upgrade steps, GitHub Release asset handling, cleanup policy, and future upgrade detection contract. Linked the guide from README and release process documentation.
- Verification: Lightweight documentation checks passed with conflict-marker scan, `git diff --check`, and local secret scan outside generated/release/log folders. `git status -sb` confirmed only intended documentation files were changed.
- User impact: Future maintenance and upgrade work has a single operating manual, and completed changes must be synchronized to GitHub before handoff.

- Type: Repository housekeeping
- Affected files/modules: `release/`, `src/**/bin`, `src/**/obj`, `docs/FEATURE_CHANGELOG.md`
- Concrete change: Removed stale local release directories and archives for versions older than `v1.0.6`, removed the obsolete `NetBootDhcpTool-fd` release folder, and cleared generated `bin`/`obj` build caches from all source projects. Kept the current portable release, tools folder, versioned `v1.0.6` folder, archive, checksum, `latest.json`, and run instructions.
- Verification: Confirmed `release/` only contains current `v1.0.6` release assets and confirmed no `src/**/bin` or `src/**/obj` directories remain. Full build was skipped to keep generated caches removed; prior `v1.0.6` release build and GitHub asset verification remain current.
- User impact: The workspace is smaller and easier to maintain while preserving the current release and future upgrade-check assets.

- Type: Release process standardization
- Affected files/modules: `build/publish.ps1`, `docs/RELEASE_PROCESS.md`, `docs/RELEASE_NOTES.md`, `README.md`
- Concrete change: Standardized packaging around the app project version, added SHA256 and `latest.json` release manifest generation, documented the GitHub release asset standard, added release notes extracted from the change log, and defined the future upgrade-check manifest URL and verification expectations.
- Verification: `dotnet build .\NetBootDhcpTool.sln -c Release --no-restore` passed with 0 warnings and 0 errors; `dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release` returned `OK`; local secret scan found no matches outside generated/release/log folders; `.\build\publish.ps1` generated `release\latest.json`, `release\NetBootDhcpTool-v1.0.6.7z.sha256`, rebuilt `release\NetBootDhcpTool-v1.0.6.7z`, and 7-Zip archive testing passed.
- User impact: Future releases can follow one repeatable packaging process, and later upgrade detection can consume a stable GitHub-hosted manifest.

- Type: Bug fix
- Affected files/modules: `src/NetBootDhcpTool.App/MainWindow.xaml.cs`
- Concrete change: Changed the Open Logs action to launch Windows Explorer explicitly instead of relying on the system shell association for the logs path.
- Verification: `dotnet build .\NetBootDhcpTool.sln -c Release --no-restore` passed with 0 warnings and 0 errors; `dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release` returned `OK`; local secret scan found no matches outside generated/release/log folders; `.\build\publish.ps1` refreshed `release\NetBootDhcpTool`, rebuilt `release\NetBootDhcpTool-v1.0.6.7z`, and 7-Zip archive testing passed.
- User impact: Opening logs no longer routes through editor/debugger file associations that can produce PowerShell errors such as unsupported `log` language mode.

- Type: UI layout optimization
- Affected files/modules: `src/NetBootDhcpTool.App/MainWindow.xaml`, `src/NetBootDhcpTool.App/MainWindow.xaml.cs`
- Concrete change: Optimized the top adapter summary layout by removing the duplicate current/last IP label column, tightening the adapter label spacing, and applying consistent spacing between IP, MAC, gateway, and status fields.
- Verification: `dotnet build .\NetBootDhcpTool.sln -c Release --no-restore` passed with 0 warnings and 0 errors; `dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release --no-build` returned `OK`; local secret scan found no matches outside generated/release/log folders; `.\build\publish.ps1` refreshed `release\NetBootDhcpTool`, rebuilt `release\NetBootDhcpTool-v1.0.6.7z`, and 7-Zip archive testing passed.
- User impact: The adapter information area uses less horizontal space, removes the redundant label block, and presents network details with cleaner spacing.
