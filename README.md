# NetBoot DHCP Tool

Version: 1.2.0

Authors: Joel & Codex

Windows portable IPv4 DHCP, adapter IP configuration, IPv4/IPv6 static route rules, adapter restart/MAC tools, ping scan, web open, favorites, network profiles, recovery center, operation history, bilingual UI, contextual `?` help buttons, and logs.

## Maintenance Entry / 维护入口

Codex sessions follow [AGENTS.md](AGENTS.md). Search [engineering lessons](docs/troubleshooting/engineering-lessons.md) before investigating failures and record reusable resolutions before handoff. Codex 修改遵循仓库 AGENTS.md；遇错先查已有经验，解决后在交付前记录可复用的方法。

Start with the [maintenance guide](docs/MAINTENANCE_GUIDE.md), then read only the selected [approved work item](docs/TODO.md) and its source references. [PROJECT_MEMORY.md](PROJECT_MEMORY.md) holds stable product/network boundaries; the [release process](docs/RELEASE_PROCESS.md) owns publication steps. The [change log](docs/FEATURE_CHANGELOG.md) records completed changes, not future work. T01-T36 implementation and acceptance status is tracked in the [work-item index](docs/TODO.md); the most recently published version is recorded in the [release process](docs/RELEASE_PROCESS.md).

维护顺序：维护指南 → 待办索引 → 单个任务及相关代码。T01-T36 的实施与验收状态以[待办索引](docs/TODO.md)为准；发布步骤及远端发布证据以[发布流程](docs/RELEASE_PROCESS.md)为准。无需每次读取全部任务或历史日志。仅明确授权“发布/release”后才能提交、推送或发布。

At startup, the application claims a system-wide mutex keyed by the normalized full data-directory path before it migrates legacy files, creates defaults, or reads recovery journals. The owner keeps the mutex through asynchronous exit cleanup. A second process shows a bilingual notice and exits without changing shared files; an abnormal exit releases the mutex so a later run can inspect the preserved recovery data.

启动时，程序先按规范化后的完整数据目录取得系统级互斥锁，再迁移旧文件、创建默认数据或读取恢复记录；窗口退出清理完成前持续持锁。第二个进程会提示已有实例并退出，不修改共享文件；异常退出后系统释放互斥锁，后续启动可检查保留的恢复数据。

## Multiple IPv4 addresses / 同网卡多 IP

The Multiple IPs page appends temporary addresses on one physical Ethernet adapter while preserving existing business addresses, gateways, DNS and metrics. Add role/IP/mask/optional-target drafts, preview/apply, or double-click an existing address to reuse it for source-specific scanning. Only newly created owned ActiveStore addresses are removed on exit; existing addresses remain. Saved profiles fill forms without applying network settings. Pending prior-run/uncertain/external-change records are available individually in Recovery Center.

“多 IP 调试”支持业务和带外网段同时配置与检测，显示本机源地址、对端连通状态及变化时间。默认追加地址，不清空业务配置；只有本工具新建的临时地址会按归属清理。DHCP 客户端共存必须可靠读回系统开关，不能识别的 Windows 语言/版本会阻止修改。网卡已有地址可直接加入扫描草稿，不取得删除归属。不同 VLAN 仍需要交换机/网卡或第二网卡支持。

Choose the adapter, add one draft per role/subnet, preview and apply, then scan with each assigned source address. Leave the optional target blank for a subnet scan (4096 combined targets maximum). The monitor revisits up to 32 discovered peers per round with eight concurrent probes. A fresh Ping or HTTP/HTTPS response establishes connectivity; no response is shown as offline and can also mean filtering. Existing gateways/DNS remain; overlapping another interface's route blocks an append. Prior-run records require explicit recovery and survive closing a new read-only session.

操作顺序：选择网卡 → 分别添加“业务”“带外”的 IP/掩码和可选对端 → 预览并应用 → 按源地址扫描／监测。空对端扫描本网段，合计最多 4096 个目标；持续监测每轮最多 32 个已发现对端、并发 8 个。已联通、已离线、已停止监测和变化／探测时间分别显示；无应答也可能是对端过滤探测。追加前检查其他网卡的重叠路由；已有网关和 DNS 保留。之前会话的恢复项须逐项处理，关闭新开的只读窗口不会清除它们。

The DHCP server's preserve-address option serves one selected scope on a gateway-free isolated static interface only. Do not use it on a shared business DHCP network. Multiple addresses cannot distinguish untagged DHCP broadcasts into different pools. The original fixed-IP replacement workflow remains available separately.

## Static Routes

The Static Routes tab accepts multiple temporary IPv4/IPv6 rules. Enter a single address or CIDR network; a single address is normalized to `/32` or `/128`. Select any local adapter, enter an on-link gateway for a next-hop route, or leave Gateway blank for a direct route. The same normalized prefix may be assigned to different adapters, while duplicate use of one prefix on one adapter is blocked. Route metrics are calculated from the current interface metrics and the preview shows the effective same-prefix winner. Different prefixes may overlap; Windows longest-prefix matching is shown in the preview. More-specific existing or planned routes block a broader rule when they use a different path, while existing routes are never overwritten or removed. IPv4 and IPv6 default routes (`0.0.0.0/0` and `::/0`) are not allowed.

Only routes created by this session are removed when the application closes. Existing system routes are not overwritten or removed. If the original adapter identity is no longer available, the route is retained in a recovery journal under `%LOCALAPPDATA%\NetBootDhcpTool` and the next startup offers cleanup after the adapter is available again. Recovery Center lists each saved adapter configuration, pending MAC restoration, and session route separately; it checks adapter identity, confirms each restore, verifies adapter settings, and records the result in operation history.

Temporary DHCP firewall rules are recorded in a separate durable journal before creation. Each rule has a unique identity and owner marker and is scoped to the selected interface alias and application path. Stop and startup-failure cleanup verify ownership and the full recorded scope before removal; absent rules are treated as already cleaned, while changed rules stay visible in Recovery Center. Resolve a prior DHCP firewall recovery entry before starting another DHCP session.

## Notes

Favorite records can store device name, device number, serial number, remark, account/password text, and free-form memory text. Personal favorite passwords remain protected with Windows DPAPI; manufacturer-published BMC presets are explicitly marked public and show their documented plaintext defaults or model/label guidance. Credential-free JSON export remains available. A favorite can also mark HTTPS as its preferred web scheme.

Every application action button has a small, visible-by-default round `?` help indicator beside it. Hover the action or indicator for a contextual Chinese/English explanation, or click `?` to open the same explanation in a dialog. The help control does not execute the neighboring action. Hovering the main adapter and result fields also explains what their values represent.

## Version Check

The application displays its running version when it starts. The accepted v1.1.0 release introduced a separate signed `latest-v2.json` channel and Full 7z packages; the legacy `latest.json` channel remains frozen for ZIP-only clients. Users upgrading from older versions must run the full Setup once. It preserves user configuration and data, then subsequent releases use in-app 7z updates. The app probes only mirrors whose signed version, package filename, size, and SHA-256 match, prioritizes the faster eligible source, and falls back to the other approved source when a mirror fails. The updater independently verifies the manifest signature and package bytes, applies one transaction through safe staging and rollback, then verifies startup health. The v1.2.0 release is published on GitHub and Gitee. It adds multiple IPv4 debugging on one Ethernet adapter, source-specific checks, visible peer online/offline transitions and improved DHCP startup/cleanup. Windows PE versions and signed package inventories agree; publication evidence and the separate pending discovery observations are in [the release process](docs/RELEASE_PROCESS.md). Accepted v1.1.1 and earlier releases remain immutable.

## Build

Run:

```powershell
.\build\build.ps1
```

For development and change-specific validation, follow the [standard maintenance workflow](docs/MAINTENANCE_GUIDE.md#standard-change-workflow).

## Publish

For local packaging and GitHub/Gitee distribution, follow the single [release process](docs/RELEASE_PROCESS.md).

The portable archive contains one top-level `NetBootDhcpTool` folder with no version number in its name. Extract it, then move that folder to any writable path on a Windows x64 computer and run `NetBootDhcpTool.exe` inside it. Local packaging writes to a new `release\local-build-*` directory so existing release files are preserved.

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
