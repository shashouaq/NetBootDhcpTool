# Feature Change Log

## 2026-07-02

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
