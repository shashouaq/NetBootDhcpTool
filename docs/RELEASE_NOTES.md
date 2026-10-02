# Release Notes

## v1.1.1

### zh-CN

本版本统一 Windows 文件版本属性，并提高发布工具处理临时网络故障的可靠性。

- App、Updater、SetupHelper 和 Setup 的 Windows 文件属性统一显示产品版本与组件说明。
- 发布工具为 GitHub/Gitee 读取增加有限重试和受校验的只读 fallback；未知上传结果先核对已存资产，写请求不自动跟随重定向。
- v1.1.0 已完成首次安装的用户沿用应用内 Full 7z 更新；更早版本仍需通过匹配的 Setup 与 Full 7z 完成一次完整安装。
- 安装和更新继续使用既有签名、文件校验、启动健康确认及回滚流程，保留用户数据。

### en-US

This maintenance release aligns Windows file version properties and improves release tooling reliability during transient network failures.

- Windows file properties show consistent product versions and component descriptions for App, Updater, SetupHelper and Setup.
- Release tooling adds bounded retries and verified read-only fallback for GitHub/Gitee reads. Uncertain uploads are reconciled against stored assets; write requests do not follow redirects automatically.
- Users already migrated through v1.1.0 continue using in-app Full 7z updates. Older versions still require one full installation using the matching Setup and Full 7z.
- Installation and updates retain the existing signature, inventory, startup-health and rollback checks and preserve user data.

## v1.1.0

### zh-CN

本版本是更新基础设施升级。v1.0.20 及更早版本的用户需要从本次 Release 下载并运行 `NetBootDhcpTool-Setup-v1.1.0.exe`，完成一次完整安装；安装过程中保留用户配置、网络配置、收藏、历史及其他用户数据。安装完成后，后续版本恢复应用内自动更新。

- 新更新清单使用独立的 `latest-v2.json` 通道，避免旧版更新器误选 7z 包。
- 完整安装程序与 Full 7z 共用同一份签名 payload；安装会校验签名、包大小、SHA-256 和文件清单后再替换程序文件。
- 新更新器通过 Full 7z 执行应用内更新；失败时恢复之前受管文件。
- GitHub 与 Gitee 的签名 Full 7z 使用相同文件名、大小和 SHA-256；两个源均通过签名清单选择，下载源不可用时自动故障切换，安装失败时执行事务回滚。

### en-US

This release upgrades the update infrastructure. Users of v1.0.20 and earlier must download and run `NetBootDhcpTool-Setup-v1.1.0.exe` from this Release to perform one full installation. User settings, network configuration, favorites, histories, and other user data are preserved. After installation, in-app automatic updates resume for subsequent releases.

- The new updater uses a separate `latest-v2.json` channel so legacy updaters cannot select a 7z package.
- The full installer and Full 7z share the same signed payload. The installer verifies the signature, package size, SHA-256, and file inventory before replacing application files.
- The new updater applies in-app updates from Full 7z packages and restores the previous managed files if an update fails.
- GitHub and Gitee serve the same signed Full 7z filename, size, and SHA-256. Signed metadata selects between the two sources, downloads automatically fail over when a source is unavailable, and failed installations use transactional rollback.

## v1.0.20

### zh-CN

NetBoot DHCP Tool v1.0.20 将正式发布与 Gitee 镜像同步拆开，提升发布可靠性。

- 正式包在 GitHub-hosted Windows Runner 上只构建一次，GitHub 发布和完整性校验独立完成。
- Gitee 镜像可在 GitHub 正式发布成功后单独重试和续传，不会让镜像故障回滚 GitHub 发布状态。
- 更新检查只测速和下载签名信息与当前正式版本一致的镜像；过期或文件名、SHA-256 不匹配的镜像会自动排除。

### en-US

NetBoot DHCP Tool v1.0.20 separates formal publication from Gitee mirror synchronization for more reliable releases.

- The formal package is built once on a GitHub-hosted Windows runner, followed by independent GitHub publication and full integrity verification.
- Gitee mirroring can be retried or resumed after GitHub publication succeeds; mirror failures do not change the GitHub release state.
- Update checks probe and download only mirrors whose signed metadata matches the current formal version; stale sources or filename/SHA-256 mismatches are excluded.

## v1.0.19

### zh-CN / 简体中文

NetBoot DHCP Tool v1.0.19 增加固定名称的便携目录，并支持带签名校验的 Full/OTA 自动升级。

- 解压便携包后，可直接将 `NetBootDhcpTool` 文件夹移动到目标路径。
- 更新包下载并校验完成后，可点击“重启升级”。独立更新器会等待程序正常退出，再替换受管文件、校验文件并启动新版；应用失败时可恢复旧文件。
- 用户数据继续保存在 `%LOCALAPPDATA%\NetBootDhcpTool`；安装目录中不归更新器管理的文件会保留。
- 更新弹窗根据保存的 `Language` 设置选择中文或英文说明；`auto` 跟随系统 UI 语言。
- 旧版客户端仍可手动下载 `.7z` 便携包。

### en-US / English

NetBoot DHCP Tool v1.0.19 adds a fixed-name portable folder and signed Full/OTA automatic upgrades.

- Extract the portable archive and move the `NetBootDhcpTool` folder directly to the desired location.
- After an update package is downloaded and verified, select **Restart to upgrade**. A separate updater waits for normal application shutdown, replaces and verifies managed files, then starts the new version. It can restore the previous files if applying the update fails.
- User data remains under `%LOCALAPPDATA%\NetBootDhcpTool`; files in the installation directory that are not managed by the updater are preserved.
- The update dialog selects the matching language from the saved `Language` setting; `auto` follows the system UI language.
- Legacy clients can continue to download the `.7z` portable archive manually.

## v1.0.18

NetBoot DHCP Tool v1.0.18 fixes Gitee-only update discovery for clients that receive canonical Release download URLs from Gitee's metadata API.

### Changes

- Accept the exact project's canonical Gitee `latest.json` and versioned `.7z` Release URLs alongside the existing attachment-ID aliases.
- Require a canonical Gitee archive URL's release tag and archive version to match the manifest version; reject unrelated hosts, malformed paths, mismatched versions, credentials, query strings, and fragments.
- Keep Gitee-first metadata discovery, GitHub fallback, measured dual-source selection, and archive SHA-256 verification.

### Validation

- Exact-commit Windows CI `36302211641` and formal release `36302353139` passed; all seven publish and verification stages succeeded.
- GitHub and Gitee each serve the same 56,975,532-byte `.7z` archive with SHA-256 `e23dccb0bbc30253a13296fa259e60f2a3f8354db0d333cbca7f1b01e1a18a4f`. The identical 2,782-byte `latest.json` files have SHA-256 `ce446161c885163f2d731eb0ca4eadde4affd86ab8deb319f1ce6517d34a2093`.
- The published-tag client passed its live dual-source check, and an independent Gitee-only live check discovered v1.0.18 from Gitee's canonical manifest URL.

## v1.0.16

NetBoot DHCP Tool v1.0.16 validates the new formal release path while preserving the existing portable archive format and update behavior.

### Changes

- Build the `.7z` package once on a dedicated Windows x64 runner and publish the identical archive and SHA-256 sidecar to GitHub and Gitee.
- Verify remote archive bytes, checksum files, and update manifests before making either Release stable.
- Resume a same-tag retry from its persistent package and Gitee attachment checkpoints without rebuilding or duplicating verified assets.
- No DHCP, adapter, route, or update-selection behavior changes in this version.

## v1.0.15

NetBoot DHCP Tool v1.0.15 hardens DHCP lease recovery and route adapter identity checks, and makes update selection and downloads easier to recover.

### Changes

- Stop DHCP before acknowledging a lease when its journal cannot be saved; unexpected-stop recovery waits for active network operations.
- Verify a static-route target by stable adapter GUID immediately before writing the route.
- Select the newest valid update manifest across Gitee and GitHub, even when one mirror is stale.
- Fall back when a download source is idle for 30 seconds; explain and provide cancellation in the bilingual update flow.

### Validation

- Pinned .NET 10.0.401 Release solution build: 0 warnings/errors; MSTest: 150/150; console smoke: `OK`; non-admin WPF UI smoke: `UI_SMOKE_OK`; maintenance checks: 295 localization keys and 63/63 static help buttons.
- Physical adapter and independent DHCP client acceptance are not included in this release's automated evidence.

## v1.0.14

NetBoot DHCP Tool v1.0.14 adds Gitee release distribution for mainland users and selects the faster available update mirror after a bounded download-speed probe.

### Changes

- Publishes the same SHA-256-verified archive to Gitee and GitHub and keeps both download URLs in each update manifest.
- Checks Gitee release metadata first, then falls back to GitHub when Gitee metadata is unavailable.
- When an update is found, concurrently samples up to 64 KB from each source and prioritizes the faster mirror; failed sources remain available as automatic fallbacks.
- Shows source-speed results and the selected mirror beside the update link and in the confirmation; the download status refreshes its live rate and identifies source changes.
- Preserves SHA-256 verification, safe replacement, bilingual UI, and manual installation.

### Validation

- Release solution build passed with 0 warnings and 0 errors; all 144 MSTest tests passed; console smoke returned `OK`; non-admin WPF UI smoke verified the speed display, selected mirror, live fallback status, and confirmation details. Maintenance checks reported 294 localization keys and 62/62 static help buttons.
- The self-contained x64 archive is about 55 MiB and passed 7-Zip integrity testing; its SHA-256 sidecar and `latest.json` agree. The packaged executable file version is `1.0.14.0`.

## v1.0.13

NetBoot DHCP Tool v1.0.13 bundles the approved maintenance work through T17. Startup and refresh now read network adapters and current IPv4/IPv6 routes concurrently, reducing the measured network-discovery wait while preserving the full current route list and fresh checks before route changes.

### Changes

- Added visible operation phases and elapsed timing for startup discovery, network actions, cancellation waits, and normal exit recovery.
- Strengthened DHCP session/interface ownership, lease persistence and client identity handling, firewall cleanup, adapter restoration, and recovery records.
- Improved input/range validation, configuration/favorites persistence and comparison, and final download verification.
- Kept current route reads uncached; both parallel startup reads must finish before the existing UI snapshot is populated.

### Validation

- Release solution build: 0 warnings/errors; MSTest 140/140; console smoke `OK`; non-admin WPF UI smoke `UI_SMOKE_OK`; maintenance checks passed.
- On the connected Linux client, real DORA, same-address renewal after application process restart, DHCPRELEASE, address reuse under a temporary second MAC, lease UI isolation, and normal adapter/firewall restoration passed. Startup discovery samples were 2.75 and 3.29 seconds versus the T16 single-run serial baseline of 4.44 seconds; timings vary with system load.

## v1.0.12

NetBoot DHCP Tool v1.0.12 moves the project to .NET 10, adds repeatable Windows CI and focused regression tests, and makes recovery safer to inspect and apply item by item.

### Changes

- Migrated all projects to .NET 10 and pinned the SDK and Microsoft Testing Platform runner used by local and CI tests.
- Added unit coverage for CIDR and route validation, DHCP lease behavior, JSON backups, legacy-data migration failures, and adapter identity matching; added a Windows build/test workflow using the Node 24-compatible `setup-dotnet@v5` action and a maintenance consistency gate.
- Split Recovery Center into its own UI and `MainWindow` partial. Each adapter snapshot, pending MAC restore, and session route is listed independently with adapter identity, capture time, confirmation, and operation history; restore checks the resulting state.
- Made legacy configuration/log migration report individual copy failures, continue with later files, and retain the original data.
- Reduced the main-window minimum to 900x560 DIP and enabled layout rounding for compact/high-DPI screens.
- Tightened release cleanup so it does not stop machine-wide PktMon capture or filters and refuses to package while the application is open.

### Validation

- .NET 10.0.401 Release solution build passed with 0 warnings and 0 errors; all 17 MSTest tests passed; the existing protocol/persistence smoke returned `OK`; non-admin WPF UI automation passed at compact bounds. Maintenance checks reported 265 synchronized localization keys and 60/60 static buttons with help keys; the dependency audit found no vulnerable packages. The approximately 56.7 MB self-contained archive passed 7-Zip testing; its SHA-256 sidecar and manifest match, and its file version is `1.0.12.0`. The administrator-only Hyper-V route smoke was not run because this terminal is not elevated.

## v1.0.10

NetBoot DHCP Tool v1.0.10 adds temporary multi-adapter IPv4/IPv6 static routes with automatic route metrics and safer route cleanup, while hardening HTTPS reachability probes to use normal certificate validation.

### Changes

- Added multiple temporary IPv4/IPv6 route rules, including single-address normalization, per-rule adapter selection, on-link gateway validation, overlap preview, automatic effective metrics, and exact session-owned cleanup.
- Kept existing system routes read-only and protected the local default route from being overwritten or removed by the static-route feature.
- Changed HTTPS reachability probes to use the Windows/.NET certificate trust policy; untrusted self-signed certificates no longer count as successful HTTPS without explicit system trust.

### Validation

- Release build, unit smoke, dependency vulnerability audit, secret-pattern audit, route safety audit, and packaged archive verification are recorded in `docs/FEATURE_CHANGELOG.md`. The self-contained archive is `63,497,821` bytes, passed 7-Zip testing, has file version `1.0.10.0`, and uses SHA256 `d84b55b9f3cc44f9f4b55c2644514a89bc93fdfa13d44fc26f4b60378e4a1798`; the local package matches the remote manifest and a freshly downloaded remote archive.

## v1.0.9

NetBoot DHCP Tool v1.0.9 adds adapter controls, complete local static-route visibility, responsive route rendering, bilingual contextual help, and safer update/release workflows.

### Changes

- Added selected-adapter restart and MAC address changes with manual or locally administered random values, configurable adapter safety switches, and normal-exit restoration.
- Added manufacturer-published BMC default presets as explicit public plaintext records while keeping personal favorite credentials protected.
- Displayed all current IPv4 static routes across local interfaces by default, kept existing routes display-only, and optimized route rendering to avoid UI freezes.
- Added bilingual labels/help coverage, compact contextual `?` controls, hover explanations, rounded UI surfaces, run-scoped logs, and update manifest/download behavior.
- Recorded the complete change set in `docs/FEATURE_CHANGELOG.md`; the release archive is not auto-installed by the updater.

### Validation

- Release build and smoke tests passed before packaging. The self-contained archive is `63,486,110` bytes, passed 7-Zip testing, has file version `1.0.9.0`, and uses SHA256 `2ad3144c5686042017426800657215b597b723ae897cb88e5d6775e40676b3c9`; local and remote archive/manifest verification is recorded in `docs/FEATURE_CHANGELOG.md`.

## v1.0.8

NetBoot DHCP Tool v1.0.8 closes the maintenance backlog around local credential safety, network rollback completeness, DHCP lifecycle cleanup, and release update visibility.

### Changes

- Protected favorite passwords with current-user Windows DPAPI; automatically migrated legacy plaintext favorites and made JSON exports credential-free.
- Added per-favorite HTTPS preference and an explicit Open Web action; password values are masked everywhere in the main UI and details dialog.
- Captured and restored all non-APIPA IPv4 addresses and all default routes, including route metrics and policy stores, with backward-compatible legacy snapshots.
- Made DHCP startup transactional and scoped firewall rules to the application; rules created by the current session are removed on stop, failed startup, and window cleanup.
- Added a background GitHub `latest.json` check. The current version is shown at startup, and a validated newer release appears as a clickable direct download link.
- Moved main-window binding collections into `MainWindowViewModel` and expanded the smoke suite with DPAPI, updater, DHCP UDP, lease, and snapshot checks.

### Validation

- Release build passed with 0 warnings and 0 errors; the expanded smoke suite passed DPAPI migration/export, updater-manifest validation, DHCP UDP Offer/ACK and bind-failure recovery, lease exhaustion, route normalization, and legacy snapshot checks. The self-contained archive is 63,449,475 bytes, passed 7-Zip testing, has file version 1.0.8.0, and uses SHA256 `adbc955aa2367853321c12f9cbe6c7801879ef3bde9d15df0ec838934b9040a5` in the sidecar and manifest. Remote GitHub release verification is recorded in the feature change log after publication; elevated Hyper-V route smoke was not run because this terminal is not administrator.

## v1.0.7

NetBoot DHCP Tool v1.0.7 adds session-scoped static route management for isolated Windows network diagnostics.

### Changes

- Added a Static Routes tab supporting multiple IPv4 destination prefixes and per-rule wired adapter selection.
- Blank Gateway creates a directly connected route; a nonblank IPv4 Gateway creates a next-hop route.
- Canonicalized CIDR input, validated route metrics, rejected duplicate/conflicting routes, and allowed `0.0.0.0/0` with an explicit extra warning.
- Journaled only routes created by the current session and removed them on exit; retained failed cleanup records for next-start recovery.
- Added UTF-8 PowerShell route handling, typed route verification, rollback on partial batch failure, and a controlled Hyper-V vNIC smoke-test script.
- Updated the project-local SDK resolver to bootstrap and prefer .NET 8, refreshed the README and one-page guide, and bumped the application to v1.0.7.

### Validation

- Project-local .NET SDK `8.0.424` restore and Release build passed with 0 warnings and 0 errors.
- Existing test smoke returned `OK`.
- Elevated isolated Hyper-V vNIC route smoke returned `ROUTE_SMOKE_OK`; the script confirmed `LEFTOVER_SWITCHES=0`.
- The self-contained v1.0.7 package passed 7-Zip testing, SHA256 sidecar verification, `latest.json` verification, and assembly file-version verification.
- Remote GitHub Release verification is completed below as part of the v1.0.7 release.

## v1.0.6

NetBoot DHCP Tool v1.0.6 focuses on release consistency, update-readiness, and UI polish.

### Changes

- Standardized the release process around the application project version.
- Added `latest.json` manifest generation for future GitHub-based update checks.
- Added SHA256 checksum generation for the `.7z` release archive.
- Documented the GitHub Release asset standard and future updater download URL pattern.
- Optimized the top adapter summary area by tightening spacing and removing a duplicate current/last IP label.
- Fixed Open Logs so it opens Windows Explorer directly instead of routing through editor/debugger file associations.

### Validation

- Release build passed with 0 warnings and 0 errors.
- Smoke test returned `OK`.
- Local secret scan found no matches outside generated/release/log folders.
- Publish script rebuilt the portable app, versioned archive, checksum, and update manifest.
- 7-Zip archive testing passed.
