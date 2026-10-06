# Maintenance Guide

This guide is the operating standard for maintaining NetBoot DHCP Tool. Follow it for every code, configuration, documentation, packaging, and release change.

## Approved Work and Reading Order

Read [README](../README.md), this guide's relevant workflow, and the selected item from [TODO.md](TODO.md); then locate the named source methods. Read [PROJECT_MEMORY](../PROJECT_MEMORY.md) only for the affected stable business/network boundaries. Do not load all task files or release history for an unrelated change.

`TODO.md` is the sole task-status index; `tasks/Txx.md` defines each approved scope, evidence, dependencies, non-goals, acceptance and regression requirements. Approval to record tasks does not mean their code is implemented. Before execution, recheck the working tree and semantically overlapping work; preserve existing changes and extend the matching task instead of creating duplicates. Update task status only after its required checks and document synchronization, linking the result in [FEATURE_CHANGELOG](FEATURE_CHANGELOG.md).

The 2026-09-23 audit baseline and unperformed real-network checks are recorded in the index. Existing build/smoke success does not close those audit findings. This guide owns development workflow; publication commands and release checks belong to [RELEASE_PROCESS](RELEASE_PROCESS.md).

## Repository

- GitHub repository: `https://github.com/shashouaq/NetBootDhcpTool`
- Gitee distribution repository: `https://gitee.com/joel20230302/NetBootDhcpTool`
- Default branch: `main`
- Release tag format: `v<version>`
- Current application version: `1.2.0`
- Publication state: v1.2.0 formally published on 2026-10-06 on GitHub and Gitee; exact CI, independent fresh official audits, nine public assets/byte comparison, canonical client checks/download failover and four isolated Setup/Updater transactions passed. Default discovery recheck qualified one GitHub mirror; initial cold failures and Gitee API 403 remain separate T31 observations. v1.1.1 and earlier releases remain immutable. See RELEASE_PROCESS for evidence and boundaries.
- Target framework: .NET 10; the repository pins SDK `10.0.401` in `global.json` with `latestFeature` roll-forward.
- Resolve the SDK through `build/resolve-dotnet.ps1`; it honors the repository pin and bootstraps that SDK when needed. The first run may need network access.
- Legacy GitHub manifest URL (frozen for ZIP-only clients):

```text
https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json
```

New V2 clients use `latest-v2.json` and its detached signature. Keep the legacy manifest frozen; old clients migrate once through the paired full Setup and Full 7z assets before using V2 automatic updates.

## Non-Negotiable Maintenance Rules

- Inspect the existing code and documents before editing.
- Keep changes scoped to the requested work.
- Preserve unrelated user changes.
- Record every code, config, document, test, build, packaging, or release change in `docs/FEATURE_CHANGELOG.md`.
- Do not mark work complete until the change log entry exists.
- Run relevant validation when feasible.
- For documentation-only changes, run lightweight checks instead of a full build unless the docs affect packaging or release behavior.
- For application changes that are not being published, finish validation by opening the current source `Release` build for preview; documentation-only changes do not need an application preview. Do not push or publish unless the user explicitly asks.
- Before an application preview, check whether `NetBootDhcpTool` is already running. `build/run-app-admin.ps1` manages only a process whose resolved `.exe`/`dotnet` entry point is one of this repository's source Release or packaged app outputs. It requests normal close and waits up to 240 seconds for the app's serialized cleanup; refusal, unreadable ownership, or timeout leaves the process running and does not start another GUI. It prefers the current source Release output and falls back to `release/NetBootDhcpTool` only when the source executable is absent. It never uses `Stop-Process -Force` for the app.
- Only when the user explicitly says to publish/release may source, packages, tags, or releases be pushed to either host. After publishing, verify the remote commit/release/assets and report the exact URLs or commit; never claim publication from a local command alone.
- Follow [RELEASE_PROCESS](RELEASE_PROCESS.md) for every formal release. GitHub-hosted Windows runners gate CI, build and publish the signed assets once, then verify every public asset. Gitee mirroring runs independently through `scripts/Publish-GiteeMirror.ps1`; retry it by tag without rebuilding or changing the successful GitHub formal-release state. Keep the former self-hosted workflow, scripts, runner configuration and release cache as backups while the new formal release chain is under review; they are not a publication prerequisite.
- Every application run must create a new UTF-8 session log whose filename contains the start timestamp; user actions and slow-operation start/completion, elapsed time, and errors must be traceable in that run's log.
- Prioritize Chinese/English bilingual support for every future user-visible change, including UI labels, buttons, dialogs, status messages, logs, help text, and maintenance documentation. Reuse the language resources where practical; do not add Chinese-only or English-only text without documenting the reason.
- Every user-facing application button must have a separate compact round `?` help button beside it. Keep the indicator small and visible by default; hovering the action or indicator shows a contextual Chinese/English explanation, and clicking the indicator opens the same explanation in a dialog without executing the neighboring action. Run-time-created buttons must use `HelpButtonService.Attach`.
- Before handoff, compare the keys in `src\NetBootDhcpTool.Core\Defaults.cs` with both `i18n\zh-CN.json` and `i18n\en-US.json`; missing or extra language keys are a localization defect.
- Adapter restart and MAC changes are explicit administrator-level operations. They must log the selected adapter identity, requested action, result, and error; settings switches control whether wireless, virtual, disconnected, or other adapter types may be targeted.
- MAC restoration is session-scoped: after a verified change, normal application exit restores adapters whose per-change checkbox is selected; forced termination and system crashes cannot guarantee restoration. Before each write, persist a short-lived in-flight recovery record even if the user unchecked exit restore. Clear or reduce it only after verified success/compensation; if compensation is not verified, leave the record available in Recovery Center.
- `docs/FEATURE_CHANGELOG.md` is the source of truth for every code, configuration, documentation, test, build, packaging, and release change. `build/publish.ps1` reads the current version section and falls back to `Unreleased` only when that version section is absent; keep release notes aligned with the section the script will use.
- DHCP startup and manual IPv4 configuration reject WLAN. Legacy `AllowDhcpOnWifi` fields in settings JSON are ignored and do not authorize WLAN writes; there is no Wi-Fi DHCP override. Adapter restart and MAC changes retain their separate allow-any-adapter settings.
- Manufacturer-published BMC preset credentials may be stored in plaintext only for entries explicitly marked as public defaults. Model-dependent values must say to use the chassis label/manual and must never be guessed.

## Change-Specific Review Checklist

- Multiple-address drafts use `AdapterAddressPlan`; `AdapterAddressManager` owns immediate address/coexistence intents and exact removal; `AdapterAddressBackend` owns stable physical-interface resolution and fresh route/property checks; `MainWindow.Addresses` owns the form, workflow lease and bounded monitor. Preserve default routes, DNS source/values, metric, DHCP and unrelated addresses by before/after readback. `ProbeSource` binds both ICMP and TCP to the assigned source and rejects a target route on another interface. Test cancellation, unknown writes, externally changed properties, pre-existing addresses, corrupt journals, prior-run read-only close, client coexistence/renewal and single-scope isolated DORA separately. 多地址模式不能复用替换模式的整份快照恢复；未识别的系统共存状态禁止写入，异常原件与独立清理证据必须保留。

- Network profiles are drafts until the DHCP, manual-scan, and route sections pass their shared validators. Save, load, and import must state draft/effective status; load fills forms only. Loading empty optional DHCP gateway/DNS fields also clears their text and restores the add-field buttons. Keep DHCP preview/start on the shared scope validator, manual scan on ScanRangePlan, and route preview/apply on shared route validation. Compare server/mask/pool/gateway/DNS/lease, manual IP/mask/target, and normalized route targets as an order-independent multiset with duplicate counts. Normalize IPv4 masks and route CIDRs; do not compare the runtime-computed interface metric as a fixed user input. Adapter ID is preferred, with the existing MAC/name relocation fallback.

- For user-visible changes, cover Chinese and English strings, the initial/loading/empty/error/success states, progress and cancellation for long work, keyboard/accessibility names, and behavior at the 900x560 DIP minimum window size. Keep confirmations and previews on operations that can change network state.
- Give every static main-window action its contextual `?` help button. Runtime-created buttons must use `HelpButtonService.Attach`; `build/verify-maintenance.ps1` checks localization-key parity and static main-window help coverage.
- For persistence or recovery changes, preserve existing user data on migration failure, identify adapters by stable identity rather than interface index alone, confirm each restore independently, and verify the resulting state. Keep regression tests for legacy data and failure paths.
- Adapter configuration recovery snapshots include DHCP state, every affected IPv4 address and SkipAsSource value, default routes with protocol and policy store, DNS source/value, automatic/manual metric state, and administrative enabled state. Persist before the first write; mark the write attempted before invoking PowerShell. Failure compensation uses an independent bounded token and compares a full recapture before clearing or completing recovery state. Unknown legacy DNS source is preserved as unknown and blocks restoration. For administrative enable/disable, read `Get-NetAdapter.AdminStatus` (link `Status` may remain `Disconnected` while the adapter is enabled), resolve the selected adapter by interface index plus stable GUID, pass that verified Cim object through `-InputObject`, and verify the expected `AdminStatus` after each command. The NetAdapter Enable/Disable cmdlets do not accept `-InterfaceIndex` on this Windows build.
- MAC mutation tests must inject a PowerShell result failure after a simulated write, cancellation after a simulated write, and compensation/readback failure. Assert the independent compensation token and the durable recovery record. Automated tests use a fake executor only; require the specifically designated isolated adapter for the real restore matrix.
- For JSON persistence, use the load status to distinguish missing files, valid empty values, valid primary data, backup recovery, and read/parse failure. Initialize defaults only for a confirmed missing file. Validate primary and backup before saving; when both are unreadable, retain both and stop writes. Keep the last verified backup, and ensure favorite backups store personal credentials only as current-user DPAPI values while exports contain no credentials. If a recovery journal cannot be read, preserve it and disable the related cleanup/write actions until it is repaired.
- Favorite preset reconciliation must honor the persisted deletion tombstones. Add only missing shipped presets; never overwrite a user's edited notes, credentials, custom fields, or usage history. Restoring built-in presets is a separate confirmed action. Edit a clone and commit only after confirmation; keep unedited/hidden fields and IDs/history. External `IsPublicDefault` markers are untrusted: only a shipped preset with its exact published credential may remain plaintext. Credential-free exports clear all credential payloads, including unavailable DPAPI ciphertext and public defaults.
- Application startup must hold the normalized data-directory mutex before default creation, legacy migration, recovery reads, or network recovery; retain it through asynchronous exit cleanup. Cross-process ownership tests use the dedicated `NetBootDhcpTool.InstanceProbe` executable and a unique temporary `NETBOOT_DATA_DIRECTORY`, assert the losing process leaves the data-tree hash unchanged, and clean up every child process. Never point process probes at a user or production data directory. UI smoke likewise uses a unique temporary directory and acquires its lock before creating test defaults.
- For DHCP, adapter, MAC, or route changes, make test scope explicit. The normal smoke and unit tests do not prove behavior on a real physical adapter. The elevated route smoke uses uniquely named Hyper-V Internal switches and is appropriate only for route changes when Hyper-V is available.
- Static route creation first persists an unverified intent. Recovery can delete only a record with a verified route instance identity and matching interface, path, metric, and policy store. Keep uncertain intents visible but disable their cleanup action; never treat an intended path as proof of ownership. During batch rollback, remove each successfully deleted route from the recovery journal before proceeding; stop further deletion if that journal commit fails. Run `build/route-smoke.ps1` as administrator and count it only with `ROUTE_SMOKE_OK` plus no leftover test switches.
- DHCP firewall changes use a separate durable journal: persist each unique rule identity and owner/scope intent before mutation, read back the exact firewall instance after creation, and remove only by exact rule name after checking the owner marker, instance when known, executable, direction, UDP ports, interface alias, and remaining rule attributes. An absent rule is idempotent; a mismatch preserves the record. An unreadable journal locks DHCP start/cleanup, and a stale session must be resolved in Recovery Center before another DHCP start. Do not test creation or cleanup against office/production firewall rules; require a designated isolated interface and verify no owned test rules remain.
- Windows DHCP socket scoping uses `IP_IFLIST`, available starting with Windows 10 version 1803; the service must fail closed if the selected interface filter is unavailable. Local socket tests read back both the sole selected `IP_IFLIST` entry and matching `IP_UNICAST_IF` egress index.
- DHCP socket smoke on loopback proves packet handling only. Acceptance of interface isolation requires two distinct isolated interfaces, packet capture/counters on both, complete flow on the selected interface, zero response on the other, and proof that replies leave only the selected interface. Recheck that DHCP stop releases the port and that occupied-port startup fails; never use office/production networks for this matrix.
- DHCP protocol decisions follow RFC 2131 §4.3: a pending Offer is not a lease, SELECTING must honor Server Identifier and Requested IP, INIT-REBOOT and renewal/rebinding must match an owned valid binding, and RELEASE/DECLINE must verify ownership. The app persists a binding before ACK and keeps unexpired binding/conflict state across service restarts. If lease-table persistence fails while serving a packet, stop DHCP before processing another request; do not continue issuing Offers or ACKs from uncertain state. The UI's lease state, session history, and Ping result are separate fields; Released/Declined/Expired rows clear current Ping/HTTP/HTTPS probes but retain last-online time. Test reuse of the same IP without inheriting the former row's online probes. The detector has one total timeout and must continue past malformed or unrelated responses.
- The DHCP verifier and automated loopback smoke use the strict packet parser and transaction/client matching. Automated DHCP tests must bind a selected loopback interface and ephemeral server/client ports; they must not bind production UDP 67/68 or send DHCP broadcasts. Real DORA, renewal/rebinding, restart, and address reuse require the designated T07 isolated client/interface setup.
- Manual scans must create one `ScanRangePlan` before saving a recovery snapshot or writing adapter settings. It validates IPv4, contiguous masks, host/network/broadcast semantics and a maximum of 4096 actual probe targets; an invalid nonempty target must never fall back to a subnet scan. Subnet target counts exclude the local address and, through `/30`, network/broadcast addresses; `/31` includes both endpoints and `/32` has no implicit peer. Keep enumeration lazy and concurrency at most `PingScanner.MaxConcurrency`; DNS must be asynchronous, bounded, and cancellable. Verify the exact 4096 boundary and over-limit rejection with a fake probe; never test large ranges against office/production networks.
- Keep one timestamped UTF-8 session log per app run. Do not treat a successful build, UI smoke, or isolated route smoke as evidence that a real user network was changed or accepted.

## Standard Change Workflow

1. Check local state:

```powershell
git status -sb
git pull --ff-only
```

2. Make the scoped change. When a failure occurs, search existing lessons first and follow the [problem-resolution recording workflow](#problem-resolution-records--问题解决记录) before handoff.
3. Update `docs/FEATURE_CHANGELOG.md` with:

- date
- type
- affected files/modules
- concrete change
- verification
- user impact

4. Run the commands in [RELEASE_PROCESS Required Checks](RELEASE_PROCESS.md#required-checks) for application changes, then select any additional task-specific checks from the [verification evidence map](#verification-evidence). That section is the single command list for full local validation and pre-publication checks. Run the commands separately and stop at the first failure. For documentation-only changes, use the lightweight checks in the verification map.

The test projects have different entry points: `NetBootDhcpTool.UnitTests` is the MSTest suite and must be run with `dotnet test`; `NetBootDhcpTool.Tests` is the network/persistence console smoke and must be run with `dotnet run`. The WPF UI smoke script runs on a standard-user Windows session; CI skips it if its runner is elevated. Run the UI smoke for user-visible changes when a non-admin Windows session is available. Its T03 workflow checks acquire a fake in-process owner and invoke real UI handlers for F5/Ctrl+R, Favorite/Profile, Recovery Center, and close-wait behavior without issuing adapter writes. For edits to Preview/packaging process guards, also run `build/tests/process-safety.tests.ps1` from both Windows PowerShell 5.1 and PowerShell 7 when installed; the fixture uses a test-only sleeper, path-collision records, and injected process callbacks, never a real app termination.

For static-route changes, also run the elevated isolated route smoke when Hyper-V is available:

```powershell
.\build\route-smoke.ps1
```

Count the route smoke as passed only when it exits successfully and prints `ROUTE_SMOKE_OK interfaces=<index>,<index>`. It creates and removes only its uniquely named Hyper-V Internal switches. If elevation or Hyper-V is unavailable, record the check as skipped; do not describe it as passed. Do not substitute tests against a physical adapter.

For the combined T21/T22 administrator acceptance, run `build/admin-acceptance.ps1` from an elevated PowerShell after the Release solution build. It runs the Hyper-V route smoke first and only proceeds to cancellation and independent compensation on the specifically designated isolated Intel X722 adapter after the route gate succeeds. The machine-readable report is `artifacts\acceptance\admin-acceptance.json`; require `passed=true`, `routeSmoke=true`, `routeResourcesClean=true`, and `physicalAdapterCancelCompensation=true`. The wrapper checks for newly retained route-smoke switches, vEthernet adapters, and test-prefix routes even when the route smoke fails; a clean failure report proves cleanup only, not acceptance.

## Problem Resolution Records / 问题解决记录

For every nontrivial failure resolved during code, configuration, test, build, packaging, release or diagnostic work, recording the reusable method is part of completion. Codex follows this repository rule through [AGENTS.md](../AGENTS.md); it is an agent workflow requirement, not a background service or a guarantee that every external Codex session writes documents automatically.

每次修改中解决了有复用价值的问题，必须在交付前记录方法；仅修好了代码或只写“测试通过”不满足本规则。此规则由仓库 AGENTS.md 引导后续 Codex 会话执行，不依赖聊天历史，也不等于全局永久记忆的自动写入。

1. Search [engineering lessons](troubleshooting/engineering-lessons.md), the relevant task and `PROJECT_MEMORY.md` using the error text/module before trying a new workaround. / 先按错误文本与模块检索已有经验，核对是否仍适用。
2. Extend the matching lesson with symptom, trigger, confirmed cause (or explicit uncertainty), effective steps, verification, applicability/limits and a source/evidence pointer. Keep an unresolved investigation in its task; do not label an unverified hypothesis as a solution. / 补充原条目，注明现象、触发条件、原因或不确定性、有效步骤、验证、适用边界和证据；未解决问题保留在任务中。
3. Keep durable product/network boundaries in `PROJECT_MEMORY.md`, reusable troubleshooting in the lesson or focused troubleshooting page, chronological changes in `FEATURE_CHANGELOG.md`, and task status only in `TODO.md`. Link these instead of copying logs or creating competing status indexes. / 分别保存稳定边界、排障方法、变更事实和任务状态，通过链接衔接，不复制长日志。
4. Retain original failures and separate later successful checks. Record regression coverage when it is needed to prevent recurrence; documentation-only work uses lightweight checks. Never document credentials, private keys or signed URL query strings. / 保留原失败，后续成功单独记录；按风险补充必要回归，纯文档使用轻量检查，敏感数据不得入文档。
5. At handoff, link the updated lesson; if there is no new reusable lesson, state which existing method was reused or that no new conclusion was established. / 交付时链接经验记录；没有新增经验时说明复用来源或本次未得出新结论。

### Reusing a resolution efficiently / 高效复用步骤

1. Start with the [symptom lookup](troubleshooting/engineering-lessons.md#quick-lookup--按症状查找). Record the current commit, SDK/tool version, exact error, operation type (read/write/local fixture) and evidence directory before retrying. / 按症状定位，先固定版本、错误与操作类别，避免把不同失败混为同一原因。
2. Verify the current contract and reuse the existing resolver/audit/transport/mirror/typed-request entry. If the fixture is invalid, correct the fixture and preserve its failed result; if a remote write is uncertain, query the exact remote identity before deciding whether to resume. / 先核对契约并复用入口；夹具错误先修夹具，未知写入先对账。
3. Capture only the evidence needed for the unresolved step. Once applicable checks pass, repeat them only for new code/asset changes, failures or remaining uncertainty; inspect a retained exact-commit artifact instead of rebuilding an immutable release. / 先补缺失证据，已通过门禁按变更风险决定是否重跑；正式原件不可为收尾而重建。
4. Update the matching lesson with the verified result and remaining limits. Keep counters per batch/source and distinguish confirmed repair, recovery observation and pending investigation. / 更新原条目，按批次/源记录数量，明确已修复、恢复观察或待诊断。

The v1.1.1 retrospective is indexed in VERSION-001, AUDIT-001/002, HTTP-001/002, MIRROR-001, REHEARSAL-001, UISMOKE-001, CREDENTIAL-001, SCAN-001 and RECORD-001. DISCOVERY-001 links the separate pending T31 diagnostic; release acceptance does not close that investigation.

## Runtime Responsibility Map

Keep background work with the narrow owner that can cancel it, observe it, and enforce its state boundary:

| Responsibility | Owner | Boundary |
|---|---|---|
| PowerShell process start, timeout, cancellation, output drain, and child cleanup | `PowerShellProcessRunner`, DHCP-scoped `PowerShellProcessSession` | Services keep their own scripts, ordering, ownership checks, and compensation. Successful DHCP start retains one warm child on private inherited pipes; failed/canceled start, normal/unexpected stop and exit release it. Each request remains separate from its immediate journal commit. Never replay writes after an uncertain response; stop/reap the child on failure. |
| Fresh route-planning reads | `StaticRouteService` snapshot reader | Refresh for every preview/apply; preserve per-write identity and ownership checks. |
| HTTP/HTTPS reachability | `HttpProbeService` | Reuse its owned transport, bound and cancel requests, preserve system TLS validation. |
| Lease connectivity work | `LeaseProbeCoordinator` | Cap active probes at 8 and pending bindings at 64; validate session/client/IP/generation before applying results. |
| Visible peer connectivity and known static-IP peers | `PeerConnectivity`, independent `LeaseProbeCoordinator` instance and MainWindow session guard | Keep lease protocol separate; record check/transition time. At most 32 known peers per two-second round-robin batch; cancel on adapter/session changes. Cached web details are not fresh Ping proof. |
| First-use current route display and shutdown | MainWindow read generation, lifetime cancellation and background-stop task | Startup common tabs require adapters only; explicit refresh still reads all routes. Reject old/canceled display reads; cancel independent background tasks early while keeping real network restoration order and verification. |
| Scan progress and ordinary operation history | `ScanProgressAccumulator`, `CoalescingSnapshotWriter` | Batch display and history only; critical recovery and DHCP persistence stay immediate. |
| Session log file and display | `FileLogger`, `LogDisplayBuffer`, MainWindow renderer | Keep every file row; the buffer owns locking, 500-row backlog, 128-row batches, omission count, clear and close. MainWindow owns visibility/rendering/scrolling and records rendered-row omissions. Support-package reads share an active log. |
| Selected-adapter status refresh | `CoalescedRefreshCoordinator` and MainWindow request identity | Coalesce refresh work and reject stale adapter/workflow generations; never use display data to authorize a write. |
| Update check/download lifecycle | `UpdateController` | Own tasks and cancellation; MainWindow handles confirmation and presentation. |

## Verification Evidence

Use the [standard change workflow](#standard-change-workflow) commands where applicable, then select the task-specific checks. These checks prove different boundaries:

Run `build/measure-performance.ps1` for the T20 fixtures. It writes five raw samples per fixture plus environment and per-sample CPU/allocation data to ignored `artifacts/performance/`. Current coverage includes controlled 254/4096-target scans, 64 HTTP targets, 10,000 session-log rows, 4,096 history hits persisted in 128-row batches, 128 lease bindings with the 8/64 scheduler caps, a 100-event refresh burst, a no-change adapter-field comparison, read-only full-list/selected-identity adapter reads, and old four-query/new one-snapshot route-planning reads. Scanner elapsed time excludes real ICMP/DNS/HTTP and UI. Host adapter and route timings are observations, not stable timing gates; route measurements execute only read-only Windows queries. T21/T22 designated isolated-interface and administrator Hyper-V acceptance remains separate from these results.

| Check | Establishes | Does not establish |
|---|---|---|
| `verify-maintenance.ps1`, Markdown/link review, `git diff --check` | Document, localization, help-key, and static repository consistency | Runtime behavior |
| Release solution build | Source compiles for the configured targets | Correct runtime behavior or adapter changes |
| MSTest | The covered isolated logic and injected failure paths pass | Real adapter, firewall, route, DHCP client, or WLAN behavior |
| Console smoke | The selected loopback, parser, and persistence smoke scenarios pass | Physical-interface isolation or real DHCP client acceptance |
| Non-admin WPF UI smoke | The covered UI handlers and views work with isolated temporary app data | Real system network changes |
| Admin Hyper-V route smoke | The reported route scenario passed on its uniquely named isolated test switches | DHCP, firewall, adapter recovery, or other route scenarios |
| Task-specific real isolated-interface acceptance | Only the named adapter/client scenario evidenced by that task | Other tasks or general production acceptance |

`build/verify-dhcp-pktmon.ps1` is a host-level diagnostic: it stops any active PktMon capture and removes global PktMon filters before capture, then stops the capture and removes filters in `finally`. Run it only when replacing the machine's current PktMon state is authorized. Packaging and test-process cleanup leave machine-wide PktMon captures and filters untouched.

For documentation-only changes, run `build/verify-maintenance.ps1`, review the rendered Markdown and local links, then run `git diff --check` and `git status -sb`. Do not run the app or full build for a docs-only change unless the document changes packaging or release behavior.

5. Commit and push only after the user explicitly authorizes publication:

```powershell
git add <changed-files>
git commit -m "<short change summary>"
git push
```

For ordinary application changes without explicit publication authorization, stop after local validation and run `build/run-app-admin.ps1` for user preview. The script only closes an entry point proven to belong to this repository, waits for normal app cleanup to finish, then opens the current source `Release` build or its packaged fallback as an elevated instance. If process ownership or exit cannot be proved, resolve that state manually and rerun; the script leaves the process untouched. Documentation-only changes do not require an app preview.

## Version Upgrade Workflow

Update the version only in `build/Version.props`, review intentional old-version references, and synchronize the user summary in `docs/RELEASE_NOTES.md` with the applicable `docs/FEATURE_CHANGELOG.md` section. Use [RELEASE_PROCESS](RELEASE_PROCESS.md) for the sole set of release validation, packaging, commit/tag, GitHub upload, and remote verification steps.

PE metadata and release transport checks are part of Windows CI. Run `build/tests/pe-version.tests.ps1` with NSIS 3.12, `build/tests/http-resilience.tests.ps1`, `build/tests/release-resilience.tests.ps1` and `python build/tests/python-http.tests.py` with the pinned fallback requirements, plus existing release/mirror/credential regressions. Complete bundle preparation must pass `Assert-NetBootReleaseVersions` before publication. Follow [release resilience rules](RELEASE_PROCESS.md) for retries, verified partial files and uncertain-write reconciliation.

The current formal version 1.2.0 was published from immutable commit/tag `a92898c9981a8e09d6222a9eb53be826dbfe1d84` / `v1.2.0`; see [the release process](RELEASE_PROCESS.md) for exact CI, fresh audits, public/client/runtime acceptance and distinct T31 discovery observations. The accepted 2026-10-02 v1.1.1 commit/tag `63ff41962737ff83674751776a28af5937fa312a` / `v1.1.1` remains historical and immutable; see [T30](tasks/T30.md#2026-10-02-正式发布完成--accepted). Earlier draft, Test N+1 and local NU1900 records do not describe the current publication state. Test-key assets are never formal assets. Older releases and frozen legacy manifests remain immutable, and T19 remains DONE / ACCEPTED. Future publication still requires explicit authorization.

2026-10-06 的 1.2.0 已正式发布并完成双站原件、客户端直达清单／下载切换及四项隔离安装升级验收；默认发现复查只有一个合格镜像，不关闭 T31。1.1.1 及此前草案、Test N+1、本机 NU1900 保留为历史记录；正式状态与证据见发布流程，后续发布仍需明确授权。

## Local Cleanup Policy

- Keep the two newest versioned releases in `release/` for rollback and active troubleshooting; remove older versions.
- Keep the two newest `release\local-build-*` package directories so recent local build output remains available without replacing earlier artifacts.
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

The legacy v1.0.20 update path uses the Gitee latest Release API and frozen `latest.json` attachment, then its GitHub fallback URL. New V2 clients use `latest-v2.json` on both sources:

```text
https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json
```

The V2 signed manifest keeps legacy fields unchanged and adds a separate `sevenZipPackages` list. Full 7z is the automatic-update payload; any optional OTA package must use the same 7z extractor, locked stream, canonical path checks and transaction/rollback path. The detached RSA-PSS/SHA-256 signature authenticates the exact manifest bytes. ZIP support remains for historical compatibility and test/tools; it does not mean the v1.0.20 updater can safely apply V2 packages.

For a signed package, the app:

- compare `version` with the running app version
- display the running version immediately at startup
- show a clickable `有新版本！` / `New version available!` link beside the version when a newer validated release exists
- show the manifest `releaseNotes`/`changes` in a confirmation dialog, then download the selected package into `%LOCALAPPDATA%\NetBootDhcpTool\updates\staging`
- accept only a correctly signed manifest and HTTPS assets from the exact project repositories on GitHub or Gitee; verify package SHA-256, size, manifest inventory, paths, and current installation baseline before enabling **Restart to upgrade**
- start the standalone updater from `%LOCALAPPDATA%\NetBootDhcpTool\Updater`, where it repeats verification, waits for normal app exit and the single-instance lease to release, stages and atomically replaces only managed files, then verifies the new inventory
- wait for the new app to report a nonce-bound healthy startup; if it exits before that report, restore the prior managed files and try to start the previous version. A process that stays alive without health confirmation is left untouched with its backup and transaction evidence preserved
- when an update exists, request at most 64 KB from each approved Gitee/GitHub mirror concurrently; show each measurement as it completes and put the fastest successful URL first
- show measured source speeds beside the update link and in its tooltip/confirmation, then show the active source and refreshed transfer speed during download; report a source switch if a mirror fails
- try approved mirrors in measured-speed order, validating the same SHA256 before replacing the destination
- use the same injectable `HttpClient` transport for manifest and archive requests; the service disposes only a client it created itself, and rejects a second overlapping download while one is active

The package is streamed into a uniquely named temporary file created with `CreateNew`. The output is flushed and closed; the hash-read handle is also closed before the temporary file replaces the destination. A failed/canceled transfer or a failed replacement keeps an existing destination intact and removes only this operation's temporary file. If cleanup itself fails, the result must report both the download and cleanup failures. Keep URL, signature, hash, inventory, and baseline validation ahead of installation changes. If speed remains below 3 KB/s for 10 seconds, the UI only displays `1406829360@qq.com`; it never sends email automatically.

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
