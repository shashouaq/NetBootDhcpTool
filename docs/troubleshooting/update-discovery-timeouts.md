# Update Discovery Diagnostics / 更新检查诊断

Task status: [T31 in TODO](../TODO.md). 本页只记录现象、证据和排查方法；不修改正式发布状态，不将未知根因写成修复完成。

## Known behavior / 已确认链路

`VersionUpdateService.CheckAsync` reads Gitee then GitHub. Each source gets a separate 8-second cancellation budget. Gitee latest-release metadata, attachments, manifest and signature share that source budget; GitHub manifest and signature share its own budget. Qualified signed candidates determine allowed mirrors; probes have separate 6-second budgets and read up to 64 KiB. See [VersionUpdateService.cs](../../src/NetBootDhcpTool.Core/VersionUpdateService.cs).

每源 8 秒并非“每个 HTTP 请求 8 秒”。单源可用时检查仍可成功；只有两个源都提供匹配清单，才可宣称双源均合格。测速、整包下载、安装/升级和真实网络写入验收分别记录。

## Historical failure / 历史失败

The initial formal v1.1.1 ordinary check reported `gitee.com: A task was canceled.; github.com: A task was canceled.` The request list ended at the Gitee manifest and GitHub manifest, without request timing or inner exceptions. Later ordinary discovery and both download failovers passed separately. This does not identify DNS, TLS, response-header wait or body-read time as the cause.

原件：`D:\Release\_v111_formal_20261002\public-failover\public-client-result.json`（2 passed/1 failed）；单独复核：`public-dual-source-recheck\public-client-result.json`（1 passed/0 failed）。保留原批失败，不改成 3/3。最终正式验收见 [T30](../tasks/T30.md)。

## 2026-10-02 local observation / 本机观察

- Evidence root / 证据根：`D:\Release\_post_v111_discovery_20261002_01`。外部 Observer 使用正式验收 harness 留存的 `NetBootDhcpTool.Core.dll`，SHA-256 `64fb0a47596c619c985bcdbf8395c90cfb1c9219eb46a4dc429f4cf0fc02cf7c`；不是另一次生产安装/发布验收。
- Scope / 范围：5 个独立 .NET 进程，每个先首次检查、再用同一 HttpClient/服务复查，共 10 次。首次进程不代表 OS DNS、TLS、代理或 CDN 缓存被清空。
- Checks / 检查：10/10 `Succeeded=true`、版本 1.1.1、签名通过；耗时 1,777.3～3,745.3 ms。首次检查 1,843.4～2,763.2 ms，同客户端复查 1,777.3～3,745.3 ms。样本不能证明复查一定更快。
- Mirrors / 镜像：5/10 双源合格，5/10 仅 Gitee 合格。GitHub 出现 5 次 `HttpRequestException: The SSL connection could not be established, see inner exception.`，记录耗时 71.5～218.9 ms，token 未取消。此次快速 TLS 错误与旧的 8 秒取消现象不同，不能归为同一根因。
- Instrumentation / 记录边界：`sample-1.json`～`sample-3.json` 保留初版采样；初版无 inner exception，且自动重定向后的 RequestUri 可能是最终地址。Observer 后续同时记录 RequestedUrl/FinalUrl（均去掉查询参数）和内层异常；`sample-4.json`、`sample-5.json` 中未再发生错误，故 TLS 根因仍未知。计时覆盖至响应头，不分解 DNS/TLS，也未单独测正文耗时。
- Safety boundary / 操作边界：仅匿名读取元数据、清单、签名及小范围测速；未执行整包下载、安装、网络配置修改、凭据读取或 TLS/证书/CRL 放宽。未修改产品 HTTP stack 或任何正式资产。

## 2026-10-06 stage observation / 阶段诊断补充

本次证据根为 `artifacts/maintenance/peer-lifecycle-20261006-111048/`。使用与旧观察相同的正式 v1.1.1 Core SHA-256 `64fb0a47596c619c985bcdbf8395c90cfb1c9219eb46a4dc429f4cf0fc02cf7c`，原 `t31-current-network-1..5.json`、正文/事件 observer 的 `t31-stages-1..5.json` 及允许公开主机字段的 `t31-stages-v2-1..5.json` 分组保留。共 15 独立进程、30 次检查全部成功且验签，18 双源、12 单源；后段请求不能被总成功数掩盖。所有实验保持原信任/预算，只做匿名元数据与 64 KiB 测速；不对正式客户端或资产做变更。

三例用满源预算：旧 observer 首次 GitHub 清单 8013.7 ms、Gitee latest API 8006.4 ms；阶段 observer 首次 GitHub 清单 8005.7 ms，均 TaskCanceledException、源 token 已取消，inner exception 为空。阶段样本标明 `BeforeHeaders`，没有该次最终正文读取，避免将它归为慢正文。运行时事件显示初始 DNS/TCP/TLS 完成，经过两次 Redirect 后出现新 ResolutionStart，约 7.3 秒后仍无完成事件并达到总 8 秒预算。该失败样本只采集事件名、ActivityId 为零，未采集这次 DNS 主机；只能认为序列指向重定向后 DNS 等待，不能确认历史 TLS 根因或完整归属链。后续允许公开主机/去 query 的重定向字段未再复现此超时，不补写旧样本没有的字段。

后段 observer 十检查中九次 Gitee API 返回 HTTP 403（首次组首次为双源，其余单源），GitHub 检查/签名成功。403 的服务端原因未确认，不视为 TLS、凭据失效或已证实限流，也不重新配置 token 或继续加压采样。事件只留公开允许主机，其他主机 redacted；不存原始网络 payload、响应正文或带签名的 query。原始首次失败、后续恢复和 HTTP 拒绝均保留。

机器当前仅一条可用外网上联，另一个物理接口为 APIPA/无网关；Hyper-V 虚拟网卡共用该上联，不算第二网络。没有修改办公网络来制造条件。第二实际网络未提供，T31 仍按 TODO 保持待验收。本次已补齐本机阶段证据和可复用 harness，历史 TLS 根因仍未知。

## Repeat the observation / 复查方法（原观察入口）

First check the source/environment and use a new output filename. The saved external Observer source/project and original results remain in the evidence root. Resolve the pinned SDK through the repository entry; do not use PATH `dotnet` as proof that SDK 10.0.401 is installed.

```powershell
$taskDotnet = & .\build\resolve-dotnet.ps1
& $taskDotnet 'D:\Release\_post_v111_discovery_20261002_01\observer\bin\Release\net10.0\Observer.dll' '<new-absolute-output-json-path>' '<network-and-process-label>'
```

仅在现成且允许使用的网络运行，不为采样修改办公/生产网络。记录网络标签、时间、runtime、Core DLL hash、首次/复查、签名、镜像数、逐请求耗时和内层异常。外部证据目录是本机路径；换机器须先核对 Observer 引用的 Core 和运行时，不把路径存在当成同一版本证据。两次采样只有一次成功时保留各自结果。

## Pending evidence / 待补证据

1. A second independently available network with the same assembly, trust and budgets. / 第二个实际可用网络上的相同版本采样；本次未执行，不把两个发布源视为两种网络。
2. Comparable failure evidence to establish the historical TLS/root cause. / 当前已捕获源预算取消、响应头/正文时间与网络事件，仍须可比较的真实失败进一步确认历史 TLS 根因；不能给 inner exception 为空的样本补写原因。
3. Only after evidence supports a defect, review any proposed instrumentation/budget/retry change and its cancellation, signature, mirror and TLS regressions. / 只有证据支持后才决定是否调整诊断信息、预算或重试；不因重试恢复直接延长超时，不复用发布脚本 fallback 代替产品链路。

These pending observations do not reopen accepted v1.1.1 release gates. T31 remains awaiting acceptance; the current local snapshot is complete. 本机诊断快照已完成，跨网络和根因证据未完成；T31 状态只在 TODO 维护。
