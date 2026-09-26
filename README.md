# NetBoot DHCP Tool

Version: 1.0.16

Authors: Joel & Codex

Windows portable IPv4 DHCP, adapter IP configuration, IPv4/IPv6 static route rules, adapter restart/MAC tools, ping scan, web open, favorites, network profiles, recovery center, operation history, bilingual UI, contextual `?` help buttons, and logs.

## Maintenance Entry / 维护入口

Start with the [maintenance guide](docs/MAINTENANCE_GUIDE.md), then read only the selected [approved work item](docs/TODO.md) and its source references. [PROJECT_MEMORY.md](PROJECT_MEMORY.md) holds stable product/network boundaries; the [release process](docs/RELEASE_PROCESS.md) owns publication steps. The [change log](docs/FEATURE_CHANGELOG.md) records completed changes, not future work. T01-T18 are complete; T19's release pipeline is implemented and its dedicated runner is registered. The [work-item index](docs/TODO.md) is the source for current status and remaining remote acceptance.

维护顺序：维护指南 → 待办索引 → 单个任务及相关代码。T01-T18 已完成；T19 发布链路已实现并注册专用 Runner，双端远端发布和幂等重跑验收仍待完成。当前状态和阻塞原因以[待办索引](docs/TODO.md)为准。无需每次读取全部任务或历史日志。仅明确授权“发布/release”后才能提交、推送或发布。

At startup, the application claims a system-wide mutex keyed by the normalized full data-directory path before it migrates legacy files, creates defaults, or reads recovery journals. The owner keeps the mutex through asynchronous exit cleanup. A second process shows a bilingual notice and exits without changing shared files; an abnormal exit releases the mutex so a later run can inspect the preserved recovery data.

启动时，程序先按规范化后的完整数据目录取得系统级互斥锁，再迁移旧文件、创建默认数据或读取恢复记录；窗口退出清理完成前持续持锁。第二个进程会提示已有实例并退出，不修改共享文件；异常退出后系统释放互斥锁，后续启动可检查保留的恢复数据。

## Static Routes

The Static Routes tab accepts multiple temporary IPv4/IPv6 rules. Enter a single address or CIDR network; a single address is normalized to `/32` or `/128`. Select any local adapter, enter an on-link gateway for a next-hop route, or leave Gateway blank for a direct route. The same normalized prefix may be assigned to different adapters, while duplicate use of one prefix on one adapter is blocked. Route metrics are calculated from the current interface metrics and the preview shows the effective same-prefix winner. Different prefixes may overlap; Windows longest-prefix matching is shown in the preview. More-specific existing or planned routes block a broader rule when they use a different path, while existing routes are never overwritten or removed. IPv4 and IPv6 default routes (`0.0.0.0/0` and `::/0`) are not allowed.

Only routes created by this session are removed when the application closes. Existing system routes are not overwritten or removed. If the original adapter identity is no longer available, the route is retained in a recovery journal under `%LOCALAPPDATA%\NetBootDhcpTool` and the next startup offers cleanup after the adapter is available again. Recovery Center lists each saved adapter configuration, pending MAC restoration, and session route separately; it checks adapter identity, confirms each restore, verifies adapter settings, and records the result in operation history.

Temporary DHCP firewall rules are recorded in a separate durable journal before creation. Each rule has a unique identity and owner marker and is scoped to the selected interface alias and application path. Stop and startup-failure cleanup verify ownership and the full recorded scope before removal; absent rules are treated as already cleaned, while changed rules stay visible in Recovery Center. Resolve a prior DHCP firewall recovery entry before starting another DHCP session.

## Notes

Favorite records can store device name, device number, serial number, remark, account/password text, and free-form memory text. Personal favorite passwords remain protected with Windows DPAPI; manufacturer-published BMC presets are explicitly marked public and show their documented plaintext defaults or model/label guidance. Credential-free JSON export remains available. A favorite can also mark HTTPS as its preferred web scheme.

Every application action button has a small, visible-by-default round `?` help indicator beside it. Hover the action or indicator for a contextual Chinese/English explanation, or click `?` to open the same explanation in a dialog. The help control does not execute the neighboring action. Hovering the main adapter and result fields also explains what their values represent.

## Version Check

The application displays its running version when it starts. It checks the public [Gitee releases](https://gitee.com/joel20230302/NetBootDhcpTool/releases) and the GitHub `latest.json` manifest, selecting the newest validated release. When an update is available, it probes up to 64 KB from each available mirror, prioritizes the faster source, and shows the results beside the update link and in its tooltip. Confirming the update downloads the archive in the background to Downloads, displaying the live speed and source. The toolbar can cancel an active download; a failed or idle source automatically falls back to the other mirror. SHA-256 is checked before replacing a destination. The app never installs automatically. If speed remains below 3 KB/s for 10 seconds, the app only displays `1406829360@qq.com`.

## Build

Run:

```powershell
.\build\build.ps1
```

For development and change-specific validation, follow the [standard maintenance workflow](docs/MAINTENANCE_GUIDE.md#standard-change-workflow).

## Publish

For local packaging and GitHub/Gitee distribution, follow the single [release process](docs/RELEASE_PROCESS.md).

The portable output is `release\NetBootDhcpTool`. Copy this folder to another Windows x64 computer and run `NetBootDhcpTool.exe`.

For maintenance, packaging, and release standards, see `docs\MAINTENANCE_GUIDE.md` and `docs\RELEASE_PROCESS.md`.

The repository pins the .NET 10 SDK in `global.json`. Build scripts use a matching local or system SDK and bootstrap .NET 10 into `.dotnet` when needed.

Run as administrator:

```powershell
.\build\run-app-admin.ps1
```

Check what adapters the app can enumerate:

```powershell
.\build\list-adapters.ps1
```

## DHCP Protocol Check

Without a real device, run the app as administrator, start DHCP on an isolated adapter, then run:

```powershell
.\release\NetBootDhcpTool-tools\NetBootDhcpTool.DhcpVerifier.exe 255.255.255.255
```

This sends a DHCP Discover from UDP 68 and expects a DHCP Offer from UDP 67. Use Wireshark, tshark, or Windows pktmon to capture UDP 67/68 if packet evidence is needed. The verifier is published separately so the main portable app stays smaller.

Windows built-in packet capture:

```powershell
.\build\verify-dhcp-pktmon.ps1
```

Run PowerShell as administrator. Start DHCP in the app first, then run the script. The decoded capture is written to `logs\dhcp-verify\dhcp.txt`.

This diagnostic replaces the host's active PktMon capture and global filters before starting, then stops and removes its filters when it exits. Preserve any other machine-wide PktMon session/filter configuration before running it. Packaging and test-process cleanup do not change PktMon state.

## Runtime

Administrator permission is required for adapter IP changes and DHCP UDP 67. The app checks permission and restarts elevated.

DHCP must only be used on isolated test networks. Do not run it on office, production, or existing DHCP networks.

The DHCP service verifies the selected adapter's stable ID, interface index, operational state, and server IPv4 address before binding. Windows socket filtering restricts packet reception and reply egress to that adapter; its firewall rules use the selected interface alias. A loopback smoke does not replace dual-isolated-interface packet acceptance.

The DHCP service commits bindings and declined-address conflicts to its recovery journal before acknowledging a lease. If a lease-journal write fails during packet handling, the service stops before it can issue another response; review the recovery entry before restarting DHCP.

## Files

Application settings, favorites, profiles, and recovery journals are stored under `%LOCALAPPDATA%\NetBootDhcpTool\config`.
`i18n\zh-CN.json` and `i18n\en-US.json` store UI text.
Run logs are stored under `%LOCALAPPDATA%\NetBootDhcpTool\logs` with a unique timestamped filename per session.

Network history and operation history are stored in `%LOCALAPPDATA%\NetBootDhcpTool`. Legacy settings, favorites, and logs are copied from the application folder on first start when the corresponding per-user file is absent; originals are retained.

## Notes

The publish script uses self-contained .NET 10 so the target machine does not need .NET installed. This is larger than native tools such as Tftpd64 because WPF and .NET runtime files are included.
