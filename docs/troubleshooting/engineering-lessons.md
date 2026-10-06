# Engineering Lessons / 排障经验

This is an index of reusable methods, not a task-status list. Follow the [recording workflow](../MAINTENANCE_GUIDE.md#problem-resolution-records--问题解决记录); task status lives in [TODO](../TODO.md). 本页保存可复用方法，任务状态只在 TODO 维护。下面的历史结果以对应任务的最终证据为准，不代表当前环境每次都能通过。

## Quick lookup / 按症状查找

本轮维护与 v1.1.1 发布的复盘以 2026-10-01～02 的 T30 原始证据为准；T31 后续观察单独标识。先定位下表条目，再执行已有入口。不要从头重做已验收的 T19，也不要仅凭最后一次成功覆盖首次失败。

| 症状或检索词 | 首先检查与复用 | 条目 |
|---|---|---|
| SDK incompatible、PATH 找不到指定 SDK | `global.json` 与项目 SDK 解析器 | SDK-001 |
| CS0122、跨项目 internal 不可访问、WPF 构建失败 | 实际 `AssemblyName` 与 Core 的 `InternalsVisibleTo` | BUILD-001 |
| Updater 1.0.0、Setup 无 VersionInfo、版本带 SHA | 真实 PE 资源与共享版本源 | VERSION-001 |
| NU1900、官方源 TLS 失败、restore 退出 0 | 新鲜审计日志与硬失败门禁 | AUDIT-002 |
| 无漏洞却报 frameworks 缺失 | 未过滤依赖图与过滤漏洞报告的职责 | AUDIT-001 |
| 307/308、上传回复丢失、未知写入结果 | 禁止写重定向并按远端身份对账 | HTTP-001 |
| GET EOF、DNS/超时、Git ref 查询失败 | 共享有限读取重试与精确远端查询 | HTTP-002 |
| Gitee Release 没有 assets、附件列表为空 | 独立 `attach_files` 接口 | MIRROR-001 |
| 请求被拒绝、exit 23、health 文件未生成 | typed request、实际旧 App、健康握手 | REHEARSAL-001 |
| UI 场景重复、剪贴板失败、失败后仍打印 OK | 测试调度与局部剪贴板重试 | UISMOKE-001 |
| 网络失败后要求重配 token | 错误类别与既有 DPAPI 凭据 | CREDENTIAL-001 |
| 扫描误报掩码、读取自己日志失败 | 精确秘密匹配与输出文件生命周期 | SCAN-001 |
| 检查成功但只有一个源、冷启动复查成功 | 每源结果和分批失败证据 | DISCOVERY-001 |
| 当前版本/任务状态矛盾、标签与 HEAD 不同 | 当前事实、历史快照与不可变发布身份 | RECORD-001 |
| Codex 管理员启动、deny-only、High、sandbox 命令拒绝访问 | 宿主和实际子进程 Token、UAC 独立 CLI、单次完整访问 | ADMIN-001 |
| 租约一直已分配、固定 IP 首次在线后不显示断线 | 协议状态与新鲜连通观测分离、独立有界监测 | CONNECTIVITY-001 |
| 启动等待路由、关闭后台任务串行等待、迟到显示快照 | 默认页按需读取、提前取消、读取代数及同机配对测量 | LIFECYCLE-001 |

验证数量是该次证据的事实，不是未来固定门槛；后续使用当前清单、版本和任务规定。通用处理顺序见[维护指南](../MAINTENANCE_GUIDE.md#problem-resolution-records--问题解决记录)。

## SDK-001 — Pinned SDK missing from PATH / PATH 中缺少指定 SDK

- Symptom / 现象：`dotnet build` reports no compatible SDK although other .NET 10 SDKs are installed.
- Trigger/cause / 条件与原因：仓库 `global.json` 指定 10.0.401，系统 PATH 的 SDK 只有 10.0.301/302；不能用“存在 .NET 10”推导版本满足。2026-10-02 外部诊断项目从仓库目录执行时复现。
- Effective steps / 有效步骤：`$taskDotnet = & .\build\resolve-dotnet.ps1`，后续用 `& $taskDotnet ...`；保留 global.json，不更改版本来绕过问题。
- Verification / 验证：本次外部 Observer 经解析器使用指定 SDK 后构建 0 warning/error；无依赖审计绕过。证据根 `D:\Release\_post_v111_discovery_20261002_01`。
- Scope / 边界：解决 SDK 解析，不证明依赖审计、程序行为或发布已通过。解析器首次获取 SDK 可能需要网络。入口：[resolve-dotnet.ps1](../../build/resolve-dotnet.ps1)。

## VERSION-001 — Filenames do not establish PE versions / 文件名不能证明 PE 版本一致

- Symptom / 现象：旧正式包的 App 已是 1.1.0，Updater/SetupHelper 仍显示 SDK 默认 1.0.0 或附加提交 SHA；Setup 没有 PE 版本资源。文件名、界面和签名清单看起来一致仍可能漏掉这些问题。
- Confirmed cause / 已确认原因：版本原先只配置在 App 项目；辅助程序未继承统一属性，NSIS 未声明 VIProductVersion/VIFileVersion/VIAddVersionKey。
- Effective steps / 有效步骤：只在 [Version.props](../../build/Version.props) 修改产品版本/身份；共享 MSBuild 属性覆盖各组件，NSIS 由同一来源显式传参。读取实际 App、Updater、嵌入 SetupHelper 和最终 Setup 的 PE，再核对 portable/Full 清单、latest-v2 和文件名。使用 [PE 门禁](../../build/pe-version.ps1) 与原包装入口，不给旧正式文件重新盖版本。
- Verification / 验证：T30 本地真实 NSIS stable/RC 及错配拒绝回归通过；正式 `production-pe-versions.json` 的四组件 FileVersion 为 1.1.1.0、ProductVersion 为 1.1.1，身份/组件说明一致。证据根 `D:\Release\_v111_formal_20261002`。
- Scope / 边界：测试 N+1 的版本/公钥注入仅在隔离快照，测试签名资产不得上传。Chocolatey 包版本为 3.12.0，winget/编译器版本为 3.12，分别核对安装参数和 `makensis /VERSION`，不能假设不同渠道版本字符串相同。完整规则见[发布流程](../RELEASE_PROCESS.md#version-source)。

## AUDIT-001 — Healthy filtered report omits frameworks / 健康漏洞报告缺少 frameworks

- Symptom / 现象：新鲜官方审计查询成功、未报告漏洞，但 CI 错误判定项目/framework 覆盖缺失。
- Confirmed cause / 已确认原因：NuGet `--vulnerable` 的健康过滤结果不保留无漏洞的 frameworks；不能从过滤漏洞报告证明全部依赖覆盖。
- Effective steps / 有效步骤：从未过滤依赖图核对全部源码项目和 frameworks，再独立检查漏洞报告。官方源、新鲜缓存、NU1900～NU1905、查询失败和漏洞硬失败规则继续保留。
- Verification / 验证：T30 记录首次 CI `37016856420` 的失败与修复后 11 项审计决策回归；最终 CI 与独立签名前审计各覆盖 11 项目/framework。方法与证据：[T30](../tasks/T30.md#2026-10-02-正式发布完成--accepted)。
- Scope / 边界：此方法解决覆盖解析误判。真实 NU1900/TLS/数据源失败仍须阻止发布；不能把查询失败解释为无漏洞。

## AUDIT-002 — Restore exit zero can hide audit failure / restore 成功不等于审计成功

- Symptom / 现象：本机 Release 构建和测试包通过，restore 退出 0，但日志包含 NU1900，未得到当日官方漏洞数据。
- Confirmed/unknown cause / 原因与不确定性：官方 NuGet v3 TLS 读取失败导致审计不可用；导致该网络失败的底层原因未确定。普通 restore 返回成功不能证明漏洞查询成功。
- Effective steps / 有效步骤：使用 [Invoke-FreshNuGetAudit.ps1](../../build/release-pipeline/Invoke-FreshNuGetAudit.ps1)，为每次执行指定新的证据目录；保留 restore、完整依赖图、漏洞 JSON 与 summary。独立 HTTP cache、官方源、全部传递依赖和 NU1900～NU1905/查询/漏洞硬失败继续生效。不要手工拼一套较弱的审计命令或读取旧缓存代替。
- Verification / 验证：`D:\Release\_v111_readiness_20261002` 保留本机审计失败；发布授权后的精确提交 CI 与正式签名前独立审计各覆盖 11 项目/framework、未报告漏洞，见 [T30](../tasks/T30.md)。这两份通过证据允许继续正式流程，不把本机失败改写为通过。
- Scope / 边界：离线审计决策回归证明门禁分支，测试签名包证明本地准备，只有新鲜官方查询证明该次审计。修复解析器时使用真实健康 JSON 形态和错误/缺少覆盖夹具；PowerShell 传入 MSBuild `-p:` 参数时保持完整字符串，避免夹具参数绑定偏离实际入口。

## HTTP-001 — Redirects can repeat writes / 重定向可能重复写请求

- Symptom/cause / 现象与原因：307/308 自动重定向可能在另一个地址再次执行 POST/PATCH/DELETE；只限制显式重试次数不足以保证一次写请求。
- Effective steps / 有效步骤：写请求使用 `MaximumRedirection=0`；未知上传结果先按远端名称、大小、SHA/附件身份对账，不盲目重发。Python 只读 fallback 在第二次请求前拒绝 HTTPS userinfo 重定向。
- Verification / 验证：正确期望在修复前失败，修复后 HTTP 28、未知上传对账 1、旧读契约 3、Python 5 项通过。入口与证据：[T30](../tasks/T30.md)。
- Scope / 边界：复用共享发布传输，不把发布脚本的 HTTP fallback 自动套入已签名客户端；保持 TLS、证书和 CRL 校验。

## HTTP-002 — Recover reads without obscuring failures / 有限读取恢复与准确归因

2026-10-06 发布环境复用：`python-http.tests.py` 首次在 Python 3.14 下报 `ModuleNotFoundError: httpx`，是隔离依赖路径未加载，不是 TLS／管理员问题。验证既有 `D:\Release\_t19_python_deps` 中 HTTPX 0.28.1 后，仅当前测试进程设置 PYTHONPATH，5 项离线回归通过；finally 恢复原变量。发布工具使用既有 NETBOOT_HTTPX_PYTHON/PYTHONPATH 入口，不改全局 Python，不降低证书校验。原失败和复验分别保存。

2026-10-06 v1.2.0 正式读取补充（`D:\Release\_v120_formal_20261006`）：初次匿名 GitHub 资产读取为 generic permanent-transport；独立 curl 明确出现重定向后的 DNS 超时，分类探针另保留两次 timeout 与随后 Permanent curl transport failure，后者具体底层原因仍未知。后续直接只读诊断返回 200，九资产完整独立复读及双站逐字节校验通过；不能合并失败成同一根因或把恢复称为 DNS/TLS 已修复。源码同步的冗余 GitHub fetch 发生 Schannel handshake failure，日志确认未推送；已有本地 tag/完整对象与 GitHub 精确 tag API commit 一致，Gitee main 的已存在对象为祖先，因此操作适配器核验这些身份后做一次 atomic 快进／新 tag 推送。push exit 0 后错误的单 tag REST 查询未通过；按既有 tags 列表与 branch API 独立读回相同 commit 后继续镜像，未再次 push，未改产品／共享传输或证书规则。匿名 Gitee API 403 与附件匿名 200 分别记录；既有 DPAPI 仅用于元数据读回，不能据此声称默认客户端 Gitee discovery 已通过。外部验收项目 restore 元数据中无关 MyGet audit source 的 NU1900 通过显式仓库 NuGet.config、新缓存／官方源／warn-as-error restore 消除，构建 0 warnings/errors；另一次 MSB1006 来自 `WarningsAsErrors` 多值传参，并不是管理员或网络问题，原失败保留。

- Symptom / 现象：GitHub API/资产读取出现 EOF、TLS、generic permanent-transport 或超时；Git ref 读取失败，使人无法判断推送/上传是否完成。
- Confirmed/unknown cause / 原因与不确定性：这些是不同请求的失败类别。本轮首次公共 GET 的 generic 错误与另一次 curl 的 DNS 超时不能合并成同一个已确认根因；随后成功只证明当次读取恢复。
- Effective steps / 有效步骤：复用 [ReleaseTransport.psm1](../../build/release-pipeline/ReleaseTransport.psm1) 的 GET 分类、有限重试、超时与已校验临时文件；只有允许的 GitHub GET 故障才使用固定 HTTPX 运行时只读 fallback。调用端保持 filename/size/SHA/签名核对，凭据经子进程 stdin 传递。保留请求阶段、类别、次数、耗时，URL 去掉查询参数后才写诊断记录。
- Remote reconciliation / 远端对账：Git 推送或写请求结果不确定时，先用精确 tag/ref 或 Release/attachment ID 查询实际状态；annotated tag 继续核对最终 commit。宽泛 matching-refs 的空列表或一次 `ls-remote` 传输失败不能证明目标不存在。资产复用仍按 HTTP-001 和镜像门禁决定，不能仅凭 ref 存在判定字节验收。
- Verification / 验证：T30 故障注入和真实公网双端读回均通过；正式证据根保留首次失败及后续完整 readback。复用[发布韧性规则](../RELEASE_PROCESS.md#release-http-resilience--post-v110-maintenance)，避免临时增加第二套无界下载脚本。
- Scope / 边界：认证/权限、证书/撤销、永久错误和完整性冲突仍硬失败；发布工具恢复方法不证明客户端网络问题已修复。客户端预算与跨网络证据留在 DISCOVERY-001/T31。

## MIRROR-001 — Gitee attachments use a separate API / Gitee 附件不能套用 GitHub 响应结构

- Symptom / 现象：仓库外补充验收脚本读取 `.assets.links` 或 `.assets`，在真实 Gitee Release metadata 中找不到预期资产。
- Confirmed cause / 已确认原因：补充脚本假设错误，不是已发布包缺失。Gitee Release 附件由 `/releases/{releaseId}/attach_files` 独立接口提供，不能直接套用 GitHub assets 对象。
- Effective steps / 有效步骤：先读取当前响应与仓库已实现契约，复用 [Publish-GiteeMirror.ps1](../../scripts/Publish-GiteeMirror.ps1) 的 `Get-GiteeAttachmentsForMirror`、`Get-GiteeMirrorDownloadUrl`；按当前正式资产清单精确文件名/附件 ID 核对。排除 Gitee 自动生成的源码 zip/tar.gz，同名重复或不同字节硬失败，不为“修验收”删除附件或重新上传。
- Verification / 验证：外部脚本两次格式失败保留；修正接口后的 Gitee public-readback-03 对九项正式原件 size/SHA/签名核对通过，并与 GitHub 九项逐字节一致，证据在 T30 正式证据根。
- Scope / 边界：API metadata 成功不等于匿名下载成功；只读检查分别验签/校验。九项是 v1.1.1 的清单，不把所有后续版本数量写死；重跑镜像只补缺项，既有匹配项零上传。

## REHEARSAL-001 — Invalid fixtures can look like updater defects / 手工夹具错误会被误认为更新器缺陷

2026-10-06 v1.2.0 复用补充：四项隔离正式 Setup/App→Updater 全部 completed／HEALTHY／exit 0，各 20 个受管文件（载荷另含清单本身，共 21 文件）、稳定配置／测试用户数据与整机网络快照／注册安装路径保持，归属产品进程为零。额外“默认 discovery 结果持久化后下载”外部夹具首次 `Sequence contains no matching element`：`UpdatePackageMetadata.Format` 是 JsonIgnore，JSON 往返会回到 Zip，不等于正式客户端在内存中丢格式。用原签名 JSON 与生产信任重新 Evaluate，先核对 Full filename/size/SHA，恢复这个运行时字段，保留原单一合格 URL，再在新证据根执行实际 DownloadPackageAsync；verified Full／ReadyToInstall 通过，首次空根与失败保留。PowerShell 清理入口自身正常返回、日志为空，外部 wrapper 将未设置的 LASTEXITCODE 与 0 比较而误报；PowerShell 的终止异常／成功返回与 native exit code 分开核验，随后只读进程扫描确认零残留，不重跑已完成动作。证据同正式发布根，不修改产品协议或按失败结果重放安装。

- Symptom / 现象：Test N+1 请求被拒绝，或新 App 启动后没有健康文件而 exit 23 回滚；早期手工演练连续失败。
- Confirmed cause / 已确认原因：夹具的路径、请求标识、签名清单编码和健康文件后缀不满足既有协议。被拒绝或回滚证明对应保护路径，不能据此认定正式更新事务有缺陷。
- Effective steps / 有效步骤：优先复用 [UpdateController](../../src/NetBootDhcpTool.Core/UpdateController.cs) 和 typed request；外部夹具逐项对照 [UpdatePackages](../../src/NetBootDhcpTool.Core/UpdatePackages.cs)、[Updater ValidateRequest](../../src/NetBootDhcpTool.Updater/Program.cs) 与 [App 健康检查](../../src/NetBootDhcpTool.App/App.xaml.cs)。包放在批准数据根的 `updates/staging`，请求/accepted/health 路径按协议归属；`ManifestJson` 是原签名 JSON 字节的 Base64，不能放原 JSON 或重新序列化后再配旧签名。
- Checklist / 必查契约：请求 ID 仅字母/数字/连字符且不超过 80 字符；健康 token 为 64 位十六进制；App 健康文件后缀 `.health`；原安装 manifest 的实际 SHA 作为基线；自动更新的 parent 必须是该安装目录下真实旧 App，并等待其正常退出。独立标记测试根、TEMP/TMP、用户数据及测试签名隔离继续保留。
- Verification / 验证：`D:\Release\_post_v110_maintenance_20261001\runtime-rehearsal-08-result.json` 是正确协议演练，前 01～07 失败记录仍保留。随后正式生产原件四项 Setup/App→Updater 事务均 HEALTHY/exit 0，每项 20 个受管文件一致，用户数据保留、网络快照不变、归属进程无残留，见 T30。
- Scope / 边界：启动成功或收到 accepted 不能代替 terminal HEALTHY、Updater 退出码和库存核对；回滚退出码不可计入安装成功。先复用 runtime-08 方法并核对当前契约，避免继续手工猜协议；本地测试公钥/资产与正式生产信任分开验收。

## UISMOKE-001 — Stateful UI smoke must execute once / 有状态 UI 烟测不能被定时器重复执行

2026-10-06 T34/T35 扩展复用：测试已持有假工作流，持续监测应暂停；模拟网卡加入 Adapters 还需要将 ComboBox.ItemsSource 绑定到该集合，否则 SelectedItem 为 null，监测会按设计停止。先断言工作流暂停，再在仍保留底层假所有者的夹具中暂时清窗口指针，并 finally 恢复；绑定/恢复 ItemsSource 与选择，按真实上下文测试新鲜结果，不删掉生产侧工作流/选中身份保护。三份失败 UI 日志保留，修正后 `PEER_CONNECTIVITY_OK` 和全量 UI_SMOKE_OK 通过，证据在 `artifacts/maintenance/peer-lifecycle-20261006-111048/`；该结论只说明夹具上下文，不是实机掉线验收。

- Symptom / 现象：周期定时器重入整组有状态场景，剪贴板争用导致偶发失败，异常之后仍打印 UI_SMOKE_OK。
- Confirmed cause / 已确认原因：测试调度/成功标志有误，并存在系统剪贴板的短时争用；不能用重复执行整个场景来掩盖失败。
- Effective steps / 有效步骤：整组测试只调度一次，成功标志只在全部断言通过后输出；只对局部剪贴板操作做最多三次有界重试。保留工作流状态断言与原始失败，不放宽断言或重跑已改变状态的完整场景来制造成功。
- Verification / 验证：T30 修正后标准用户 `ui-smoke-04.log` 为 UI_SMOKE_OK；1.1.1 发布前本地标准用户烟测亦通过。远端 elevated UI 按既有规则 skipped，MSTest 的配置跳过也单独记录，不合并成通过数。
- Scope / 边界：复用 [ui-automation-smoke.ps1](../../build/ui-automation-smoke.ps1) 与已有测试实现；无非管理员环境时记录跳过。测试只用隔离数据和假所有者，不操作真实网卡制造验收证据。

## CREDENTIAL-001 — Connectivity failure is not token revocation / 连通性失败不等于令牌失效

- Symptom/cause / 现象与原因：TLS、EOF、DNS 或超时导致读取失败，不能据此判断保存的凭据过期或权限被撤销。
- Effective steps / 有效步骤：复用当前用户 DPAPI 入口；保留密文，区分传输错误与明确认证/权限错误；进程环境变量在 finally 清理。不得把 token 放进聊天、argv、日志或仓库。
- Verification / 验证：正式发布的复用/清理与泄漏检查证据在 [T30](../tasks/T30.md)；唯一操作入口见[凭据管理](../RELEASE_PROCESS.md#local-gitee-credential-management)。
- Scope / 边界：只读检查不证明写权限；明确过期、撤销、认证/权限拒绝或疑似泄漏时才按既有授权边界重新配置。

## SCAN-001 — Masked logs and open output need separate handling / 凭据掩码误报与扫描日志占用

2026-10-06 生命周期测量复用：活动 FileLogger 使用 `FileShare.Read` 写入，测量脚本默认 `ReadAllText` 的共享方式与现有写句柄不兼容，首样本失败并保留 PID 31276。按日志支持包的既有方式，用只读 FileStream 搭配 `FileShare.ReadWrite | FileShare.Delete` 和 StreamReader 读取，不修改写日志方式、不以管理员权限或跳过日志掩盖失败。先核对 retained PID/start/path，正常关闭后用新数据/结果路径重测；v3 五样本及后续交替样本读取、清理与快照检查通过。原 failed handoff `535cfd5f-ce5f-44ce-a7cd-46eaea8359a1`、脚本 v2、日志保留；边界仅活动日志可读，不支持覆盖原失败或忽略任意不可读文件。

- Symptom / 现象：泄漏检查把 GitHub checkout 日志中的字面 `basic ***` 当明文，修正后又因读取自身尚未关闭的重定向日志失败。
- Confirmed cause / 已确认原因：通用模式把掩码与真实秘密混淆；扫描范围同时包含仍被当前输出占用的文件。这是两种检查问题，不能通过跳过所有 Authorization 行或所有不可读文件来解决。
- Effective steps / 有效步骤：优先匹配实际秘密及编码形式、私钥标记，再精确认可已知字面掩码；保留其它敏感模式检查。完成日志写入/关闭句柄后扫描，或将本次扫描输出放在被扫描证据根之外。读取失败仍应明确失败；不打印匹配内容，不更换有效 token 来避开误报。
- Verification / 验证：正式证据根保留两次失败；最终 `credential-exposure.json` 为 Passed=true、CheckedEvidenceTextFiles=218、GiteeTokenCleared=true。早期检查的 206 是阶段数量，不是最终文件总数。
- Scope / 边界：扫描通过只覆盖报告列出的范围，不证明所有外部位置均无秘密。日常复用 [Get-GiteeCredential.ps1](../../scripts/Get-GiteeCredential.ps1) 的受控检查接口；finally 清理与凭据是否写入 argv/日志仍分别检查。

## DISCOVERY-001 — Successful check can use only one mirror / 检查成功可能只有一个源

- Symptom / 现象：更新检查 `Succeeded=true`，但只有一个合格镜像；另一个源可能发生 TLS/超时错误。首次失败、后续成功不能单独证明“冷启动已修复”。
- Confirmed behavior / 已确认行为：每源 8 秒，Gitee 多步 metadata/manifest/signature 共用该预算；两源依次读取，成功候选才参与镜像匹配与测速。2026-10-02 本机 10/10 检查成功，其中仅 5/10 两源成功，另 5 次 GitHub TLS 建连失败而 Gitee 成功。
- Effective diagnostic method / 有效诊断方法：分别统计检查成功、签名、镜像数、测速和逐请求失败；新进程首次检查与同客户端复查分别记录。失败链与响应头耗时单独保存，保留失败原件；响应头耗时不能分解 DNS/TLS，也不等于正文读取耗时。
- Verification/limits / 验证与边界：本机采样未复现旧的双源 8 秒超时；前 3 组失败没有 inner exception，补充内层异常记录的后 2 组未再失败。TLS 和历史超时根因仍未确定，跨网络未执行。本条是已验证的排查方法，不是已解决的客户端缺陷。
- Details / 详情：[更新检查诊断](update-discovery-timeouts.md)，[T31](../tasks/T31.md)。

2026-10-06 补充：同一正式 Core 的 15 进程/30 次检查全部验签成功，但仅 18 次双源。三次 8 秒取消发生在最终响应头前；后段九次 Gitee 403 与超时另列。运行时事件将一例 GitHub 超时缩小到两次重定向后的 DNS 等待窗口，但该失败样本 ActivityId 为零且没有目标主机字段，不能推广为旧 TLS 失败根因。新 observer 同时记录正文与测速流读取，不收集原始事件 payload/查询字符串；补主机字段后的样本没有复现超时。当前只有一个上联网，T31 的第二网络条件仍未满足。全部失败与阶段边界详见诊断页及 `artifacts/maintenance/peer-lifecycle-20261006-111048/t31-*.json`；捕获 403 后停止继续请求，不能通过重配有效凭据或放宽证书消除它。

## RECORD-001 — Historical gates must not overwrite current state / 历史失败与文档收尾不能覆盖正式状态

- Symptom / 现象：正式 v1.1.1 已验收，部分发布说明仍写“当前正式版 v1.1.0、1.1.1 未发布”；任务旧段落的待办 0 项被误读为后续仍无待办。文档 HEAD 与正式 tag 不同也容易被误判成版本错配。
- Confirmed cause / 已确认原因：阶段记录使用当前时态，后续成功未明确标识其已取代旧阶段；源码发布身份与文档收尾提交混在一起。
- Effective steps / 有效步骤：发布页顶部维护当前已验收事实，旧失败段落加日期/历史标识和最终证据链接。任务状态只查 TODO；T30 发布关闭后新增的 T31 不得回写成发布时已完成。正式 tag 固定在通过精确 CI 的发布源码 commit，文档收尾使用另一个 commit，不能移动 tag 或重建签名资产。
- Verification / 验证：本轮以 T30 最终 summary 校正文档：v1.1.1 tag 固定 `63ff41962737ff83674751776a28af5937fa312a`，精确 CI/签名前新鲜审计/两端公网原件/实际事务证据分别成立。当前 T31 仍待验收；T19/T30 的验收无需重开。
- Scope / 边界：分开记录本地回归、测试签名 N+1、精确提交 CI、正式签名、各镜像公开读回、客户端发现/failover、真实隔离事务和清理。源码/工具改变才运行相应回归；纯文档做维护、链接、UTF-8 与 diff 检查。恢复观察不升级成根因修复或实机网络验收，未来发布继续按授权与门禁执行。

## CONNECTIVITY-001 — Address assignment is not current connectivity / 已分配与当前连通状态不同

- Symptom / 现象：DHCP 对端掉线后仍显示“已分配”；固定 IP 目标第一次联通之后不再反映断线。
- Trigger/cause / 条件与原因：DHCP 状态列绑定租约协议 StatusText，而 Ping 仅更新延迟；单目标扫描成功后返回，失败又被 Assigned override 遮盖。两条链路均已在当前源码确认。
- Effective steps / 有效步骤：独立记录新鲜 Ping 状态及检查/变化时间，保留租约协议；固定 IP 在已验证配置的已知结果上做有界轮转，沿用协调器的队列/并发限制。工作流暂停，重扫/切换/恢复/关闭失效会话；应用结果前校验会话/代数/IP/网卡，旧网页详情不作为当前可达证据。
- Verification / 验证：3 项纯状态回归及 `PEER_CONNECTIVITY_OK` 覆盖联通、离线、恢复、终态/历史、暂停、切换后迟到结果、状态颜色和时间列；完整检查见 [T34](../tasks/T34.md)，原始证据在 `artifacts/maintenance/peer-lifecycle-20261006-111048/`。
- Scope / 边界：ICMP 无应答表示离线或禁止应答，不代表断电。大结果集仅每两秒一批最多 32 条，最近探测时间表示样本年龄。模拟回归不代替真实对端拔线验证，不改变 DHCP ACK/租约持久化或恢复顺序。

2026-10-06 实机续验发现：固定 IP 主列已经离线，但首次成功时写入 `Remark=Reachable / 已连通` 的文字仍保留；新时间列使星号备注列剩余宽度很小，`CellText` 换行造成单行逐字排版。将自动备注改为中英文资源 `scan.initial.success`（首次探测成功，表示历史事件），并给租约/扫描备注列设置 120 最小宽度，保留横向滚动。最终实机证据与检查见 [T34](../tasks/T34.md)，首轮错误截图 `artifacts/maintenance/live-peer-20261006-134403/current-fixed-1/offline.png` 保留。

## LIFECYCLE-001 — First-use reads and early background cancellation / 按需读取与提前取消后台任务

- Symptom / 现象：默认 DHCP 页启动忙状态等待全量 PowerShell 路由查询；关闭依次等待后台读取/更新后才开始网络清理。
- Trigger/cause / 条件与原因：RefreshAdaptersAsync 虽并行启动读取，仍 await 路由后才清忙状态。旧/新交替进程的发现中位数 1267/286 ms 证实默认页按需读取可以去掉等待；无会话退出 248/259 ms 未证实稳定提速，真实网络恢复耗时尚未测量。
- Effective steps / 有效步骤：默认页先就绪网卡；路由页首次读取，显式刷新仍全量。读显示快照校验代数和关闭令牌。关窗确认后立刻取消独立后台任务，并在必要业务补偿期间观察它们结束；恢复、归属验证和持久化仍按原顺序完成。记录首帧、功能就绪与无会话/真实恢复退出分别计时，不能用单次快样本承诺提升。
- Verification / 验证：交替旧/新各 5 样本均正常 close/exit 0，地址/路由前后相同；默认页就绪中位数 2746/1719 ms。UI `ROUTE_DISPLAY_LIFETIME_OK` 拒绝旧结果并取消慢读取；显式刷新读回 6 网卡/65 路由，关窗后台取消及原恢复等待回归通过，见 [T35](../tasks/T35.md)。
- Scope / 边界：显示读缓存不授权路由写入；首帧未变快，不承诺活动 DHCP/恢复场景同比例提升。原始并行负载样本与最终交替样本分别保留，证据在 `artifacts/maintenance/peer-lifecycle-20261006-111048/`。

2026-10-06 实机补充：此前“真实网络恢复尚未测量”为本地阶段记录。复用隔离物理 Linux 客户端完成真实 DORA，旧/新各 3 个交替独立进程起服中位数为 31295/31015 ms、活动会话正常关闭为 20180/20047 ms，未证实稳定提速。当前状态轮的关闭 20199 ms 中，所属防火墙两次删除、恢复前读取、DHCP 配置恢复与恢复后读取五次串行 PowerShell 合计 19936 ms，确认等待主要落在系统调用链；尚未拆分进程启动、模块加载与实际命令耗时。8 轮正常退出、产品恢复等价且独立 Windows/Linux 读回一致，证据在 `artifacts/maintenance/live-peer-20261006-134403/`。后续应先测量各阶段再优化，保留每步归属核验、补偿、持久化与恢复读回；不能把提前取消已实现当作真实关闭速度已改善。

2026-10-06 DHCP 实机提速续验：同一 Windows PowerShell 进程内，网卡首次/后续查询为 1632/44/51 ms、IPv4 为 1370/89/81 ms、防火墙为 2655/925/876 ms；确认模块/查询环境初始化可复用。首版仅流程内复用，关闭仍重新冷初始化；首轮起服 15675 ms、关闭 11569 ms 尚未达 50%，且该轮 SSH 客户端保护失败，因此只保留为诊断样本。最终方案在 DHCP 运行期间保留一个只接受父进程私有继承管道的临时子进程，初始化与已有只读启动检查重叠；各请求仍独立作用域、带响应标识、逐步校验并立即提交恢复日志。取消/超时/错误不重放写操作，回收原环境；正常/意外停止及退出释放，失败启动也释放。没有常驻服务或外部监听。源码、8 项执行器/保留生命周期回归及最终真实配对测量见 `artifacts/maintenance/dhcp-efficiency-20261006-143042/` 和 [T35](../tasks/T35.md)。

执行器初次专项回归中，预取消令牌在 `SemaphoreSlim.WaitAsync` 返回 TaskCanceledException，发生在原捕获块之前；不是 Windows 权限问题。进入队列前显式检查调用令牌，并分别映射队列等待取消/超时，尚未取得队列所有权时不能终止其他正在执行的请求。原 `session-tests-first.log` 1 失败/5 通过保留，最终完整单测 249 通过/1 配置跳过及真实子进程退出检查分别记录，不用修改断言来掩盖取消契约。

SSH 夹具收尾修正：最终测量前发现同一旧测试租约路径遗留 8 个 dhclient 进程，当前夹具启动客户端前已经收到同一物理 MAC 的真实 Discover/Request/ACK，触发“有线接口已有地址”保护。旧清理仅依赖 PID 文件且未读回进程退出，这是已确认的夹具检查缺口；未确认旧各轮 PID 文件为何没有有效清理依据，也未将单条报文定位到其中某一 PID。按精确 `-lf` 租约路径、dhclient 名称、指定接口 argv 与 `/proc` 启动 ticks 确认所有权后发送 TERM，8 个均退出，未用 KILL；Windows 原失败计划 POSTCHECK 配置完全相同，原 FAILED 保留。新夹具准备前检查三个明确测试路径无存活客户端，结束时枚举本轮精确拥有的进程、TERM 后有界等待并确认零剩余，之后才移除测试地址/临时文件并恢复 NetworkManager 管理。旧验收的地址/路由等价仍成立，但当时没有证明测试客户端进程已全部退出；本轮补证见 `cleanup-prior-owned-clients.json` 和 `final/remote-owned-cleanup.jsonl`。不得按进程名批量终止其他 DHCP 客户端，也不改变 Wi-Fi/办公管理连接。

## ADMIN-001 — Codex host and command tokens / Codex 宿主与命令权限分别核验

2026-10-06 v1.2.0 发布补充：首个正常关闭预览计划 `d589a8eb` 在 PRECHECK 因 `PLAN_SCRIPT_OUTSIDE_AUTHORIZED_ROOTS` 失败，未执行动作；脚本位于 `D:\Release` 而共享执行器仅允许项目根或 admin-tools 下脚本。检查原 PID/start/hash 仍一致后，将同范围脚本放入仓库忽略的 `artifacts/release-v120`，重新封存 hash，计划 `e5423d67` 全阶段 COMPLETED／exit 0。预览 PID 36192 正常关闭、产品清理 failures=0、网络前后相同；脱离启动器的进程观察退出码 unavailable，不将其写成零。证据 `D:\Release\_v120_formal_20261006`；不能为解决路径限制修改共享管理员策略或扩大授权。

同版隔离安装验收补充：计划 `0c3259bb` 在脚本启动前被 `HIGH_RISK_SCRIPT_AUTOMATION_REFUSED` 拒绝，未产生测试根。检查当前共享分类器确认它对整份文本合并匹配 `production` 与 `change/remove`，隔离脚本的报告字段／文件名／错误描述触发了该组合；不是实际生产写入或安装失败。逐项核对完整动作仅在已有 marker/test-env 约束下运行正式原件、只读网络／HKCU 登记项、精确正常关闭；将夹具路径、辅助脚本和报告字段明确命名为 isolated／official，再对调用脚本及完整 helper 同时复用原分类器检查、重封 58 项输入和阶段 SHA。共享规则保持原样。新计划 `98c0c5e3` 通过原执行器全部阶段／High／exit 0／cleanupVerified，四事务、20 托管文件及网络／登记项／零进程证据分别通过。原拒绝脚本和结果保留；存在真实禁止操作时不得以改名、隐藏命令或换入口执行来消除风险检查。

- Symptom / 现象：用户属于 Administrators，当前 Codex 和自动化 PowerShell 仍为 Medium、Administrators deny-only，管理员隔离路由烟测无法从该进程执行。
- Confirmed cause / 已确认原因：2026-10-03 本机 Codex CLI 0.159.2 的当前桌面后端和直接命令子进程均为未提升的 UAC filtered token，`IsTokenRestricted=false`；当前会话虽为 `danger-full-access`，它不会自动赋予 Windows 管理员权限。PowerShell 5.1/7 并存不是原因。Windows 原生 sandbox 的权限限制应另行核验。
- Effective steps / 有效步骤：通过 `Start-Process -Verb RunAs` 启动管理员 PowerShell 7，再启动独立 Codex CLI；交互入口使用 `--no-daemon --sandbox danger-full-access --ask-for-approval on-request`，仅作用于本次启动。先检查实际 codex.exe Token、WindowsPrincipal、whoami High 标签，再使用官方 app-server `command/exec` 的显式 `dangerFullAccess` 运行只读 `fltmc filters`；权限成立后复用 [route-smoke.ps1](../../build/route-smoke.ps1)。不得修改 UAC、安全策略、全局权限配置或创建永久管理员桥。
- Verification / 验证：2026-10-03 新 codex.exe 及其 PowerShell 5.1/7 子进程为 High/Elevated，`fltmc filters` 退出 0。已恢复隔离烟测，`ROUTE_SMOKE_OK interfaces=56,62`、退出 0，结束后没有残留测试交换机/虚拟网卡。原始证据和可重用入口保存在本次 Codex 工作目录的 `outputs/admin-tools/`、`outputs/administrator-verification.json`、`outputs/route-smoke-rpc.json`；仓库摘要见 `artifacts/acceptance/codex-admin-20261003/summary.json`。
- Scope / 边界：管理员宿主下的 sandbox Token 未取得，首次请求被 0.159.2 以 `custom outputBytesCap is not supported with windows sandbox` 拒绝；验证脚本已修正参数，但本轮未再触发 UAC。当前 Medium 宿主的独立 `unelevated` sandbox 补测确认子进程 `IsTokenRestricted=true`、管理员检查和 `fltmc` 失败。当前聊天禁止 `require_escalated`，实际成功的是官方显式完整访问通道，不能称为该审批工具已测试。路由日志仍出现 NU1900，烟测通过不替代新鲜依赖审计或 T31 跨网络/TLS 根因验收。

### 2026-10-06 Preview verification addendum / 预览核验补充

- Symptom/trigger / 现象与触发：共享管理员 handoff 的 PRECHECK/ACTION 成功打开 PID 43092，VERIFY/POSTCHECK 报 `Preview PID identity mismatch`。不是 UAC 或程序启动失败；当时窗口仍正常响应。
- Confirmed cause / 已确认原因：PowerShell 7.6.5 的 `ConvertFrom-Json` 将 `StartedAt` ISO 时间自动变为 `System.DateTime`；核验脚本将 `ToString('o')` 与该日期对象作字符串比较。原比较为 false，转换双方为 UTC ticks 后完全相等。原文件 `preview.ps1` 和 FAILED handoff `b10941ac-91a4-48ee-8a46-a86dd2261c79` 保留，不更改框架状态。
- Effective steps / 有效步骤：先读取 Get-CodexAdminResult，检查失败阶段与存活 PID，禁止重新启动预览。用已完成的 High ACTION 中精确路径匹配记录，加上当前同 PID/UTC ticks、响应窗口、新 session log 完成连续进程身份对账；再在普通会话只读比较地址/路由快照。日期按显式类型/UTC ticks 比较，避免文化格式字符串。普通会话无法读取提权进程 Path 时保留这个权限边界；缺少原 High 路径证据时不得假定路径匹配。
- Verification / 验证：`artifacts/maintenance/p3-20261006-105248/preview-reconciliation.json` 为 Passed=true；PID/start ticks 相同、first-content-rendered、窗口响应、地址/路由快照相同。原管理员计划仍 FAILED，单独对账通过，没有第二次 UAC、重复启动或重跑路由测试。
- Scope / 边界：这是本次预览核验脚本的日期比较缺陷，不归因于全局管理员框架或产品。对账只允许利用可核对的已完成 ACTION；UTC ticks、PID 或路径证据不同须保留失败并重新核实，不能以放宽匹配条件掩盖进程复用。

### 2026-10-06 disabled preview owner / 预览 owner 被弹窗禁用

- Symptom / 现象：关闭已核对 PID/start/path 的旧源码预览时，CloseMainWindow 返回 false；首帧日志存在但网络初始化未开始。
- Trigger/cause / 条件与原因：首次安全引导是 modal，主窗口被禁用；不是普通会话缺管理员。用户关闭 UAC 通知（ConsentPromptBehaviorAdmin=0）也不使宿主变 High，实际旧预览为 High、宿主仍 Medium。首个测量 handoff `f5117934-7376-4b51-8519-9d2b46b5a29f` FAILED 原件保留。
- Effective steps / 有效步骤：通过共享管理员入口验证原 PID/start/path，再枚举该 PID 可见且启用的窗口，仅识别“首次运行安全引导”/“First-run Safety Guide”并发送普通 WM_CLOSE（只读模式，不确认解锁），随后 CloseMainWindow 正常关闭主窗口；未知弹窗不自动确认，超时保留进程，禁止强杀。新测量用合成独立配置；不改用户 UAC/生产设置。
- Verification / 验证：旧 PID 43092 已正常关闭，随后五旧版、五优化版和交替十进程均正常 exit 0；高权限证据与地址/路由快照保留在 `artifacts/maintenance/peer-lifecycle-20261006-111048/`。前两个 failed handoff 分别为 owner 禁用与活动日志共享方式，不能写成管理员框架失败或产品清理失败。
- Scope / 边界：仅受本项目证据约束的旧预览/测量进程和已知可取消安全引导；不能自动同意未保存设置、业务网络变更、升级或任意未知对话框。UAC 设置由用户管理，仍通过共享入口取得实际 High。

2026-10-06 实机预检查与收尾补充（证据 `artifacts/maintenance/live-peer-20261006-134403/`）：

- 历史隔离物理网卡编号 13 已被办公口使用；稳定 GUID `{863A3E86-475B-4BA1-AE14-8D7C654DF9C1}` 本次对应 15。预检查使用带花括号的 `InterfaceGuid` 字符串直接比较 `[guid]` 时误判缺失，ACTION 未执行；显式转换两边为 GUID 后唯一匹配。Linux `ip -j -4 addr` 会省略无 IPv4 的有线口，必须用 `ip -j link` 独立确认身份，再将缺少地址条目视为空地址集合。
- 真实 DHCP 正常关闭后，原始整机 UDP 快照因 `0.0.0.0:68` 消失而失败。已核对原拥有者 PID 2244 为仍 Running 的 Windows DHCP Client；SharedAccess PID 3676 的虚拟口 UDP 67/68、目标配置、其他地址/路由和工具防火墙一致。只排除已核实 DHCP Client 的瞬时通配 68；保留其服务名/状态/PID，并继续逐项核对其他监听，不能忽略所有 UDP 差异。具体通配监听消失触发机制未确认，不能宣称服务重启或故障根因。
- 通过 `Get-Process` 获取此前已脱离启动器的 GUI 后，`WaitForExit` 结束但 `ExitCode` 未得到有效值，严格 exit-zero 检查失败；不可改成“进程消失即成功”。原 PID 33852 的关闭请求、真实双语日志 `Window closing cleanup completed ... failures=0`、接受关闭和 Application exit 及 PID 缺席共同证明正常清理，退出码仍标 unavailable。日志匹配允许同一事件名中插入中文翻译。保留 failed `dbfca15c` 及独立 `prior-preview-close-reconciliation.json`，不重放已完成关闭。
- 管理员执行器的 `No credentials` 边界曾拒绝包含历史 SSH 凭据读取的依赖，未执行 ACTION。修订后管理员仅运行无凭据 WPF 夹具及本地读回；普通会话以任务有界、固定主机/网卡和阶段白名单的协调器执行用户已授权 SSH 操作。凭据不进入交接、argv、日志或文件，不更改共享管理员策略，不创建常驻权限桥。原拒绝 `f6ae1d5b` 保留。

最终实机快照补充：8 个夹具 ACTION/VERIFY 已通过，原 POSTCHECK 仍因非 ordered 的 `DhcpClientService` 哈希表导致 JSON 成员次序不同而失败。Python 解析后的整个前后对象（所有值及数组元素）相等；补做管理员只读实时读取，仅规范同一服务对象的成员排列，再比较完整原始与新读取，不丢弃字段值或数组。原 `bd8f38c6` failed plan、前后快照保留，结构化读回为独立补证，不将原计划改写成 COMPLETED，也不重跑已成功网络操作。后续快照用 ordered 对象或结构化相等，不用文本字节顺序断言 JSON 语义。

最终只读续验的第一次计划 `13745b24` 在 PRECHECK 报 `Cannot bind argument to parameter Path because it is an empty string`，实际原因是序列化计划时遗漏了脚本 arguments，既未打开预览也未执行网络操作；这不是权限不足。逐项补齐 EvidenceDirectory、Phase 和 App hash 参数，保持脚本 hash/操作范围不变后，新计划 `5245a733` 全阶段 COMPLETED/exit 0；原失败保留。交接生成后应检查实际 JSON 参数和脚本 param 契约，再启动管理员执行器。

## BUILD-001 — Internal component inaccessible / 内部组件跨程序集不可访问

相关普通构建边界：2026-10-06 T35 首次源码构建发生 MSB3026/3027，因旧预览 PID 43092 仍映射 App 输出 DLL。应先按 ADMIN-001 的实际路径/PID/start 与普通关闭边界完成退出，再构建；不要启动尚未确认退出的预览并发覆盖输出，也不将文件锁误当 SDK/管理员不足。原 `build-first.txt` 保留；独立 baseline-app 已在构建前复制，受失败覆盖影响的源码输出没有被用于旧基线。旧预览正常退出后 Release 构建 0 警告/错误，组件 hash 和后续样本分别留存。此条补充构建前进程核验，不改变 internal friend 根因。

- Symptom / 现象：2026-10-06 提取 LogDisplayBuffer 后，Release 构建在 App 的 WPF 临时项目中报 `CS0122`；Core 和单元测试可构建。原失败保留在 `artifacts/maintenance/p3-20261006-105248/build.txt`。
- Trigger/cause / 条件与原因：Core 既有 friend 声明使用项目/命名空间名称 `NetBootDhcpTool.App`，App 的实际 `<AssemblyName>` 为 `NetBootDhcpTool`。新组件是 internal，名称不匹配使 App 无访问权；不是管理员、依赖审计或 TLS 问题。
- Effective steps / 有效步骤：先查相关项目的 AssemblyName 与生成的 friend 声明，按真实程序集名称补充 `InternalsVisibleTo`；保留旧声明，避免推测其他消费者。不为解决构建将业务状态全部 public，也不根据 WPF 临时 csproj 文件名增加随机友元。
- Verification / 验证：补齐 `NetBootDhcpTool` 后，SDK 10.0.401 完整 Release 构建 0 警告/错误；MSTest 238 通过/1 配置跳过、控制台 OK、非管理员 UI_SMOKE_OK（日志 500 条/4 批/最多 128 条）通过。修复后日志为同目录 `build-recheck.txt`、`unit-tests.txt`、`ui-smoke.txt`，范围见 [T33](../tasks/T33.md)。
- Scope / 边界：适用于内部类型跨项目抽取时的编译身份问题；不能根据 csproj 名称或 namespace 假设 assembly 名称。应用安全权限与友元可见性是不同边界，管理员权限不修复编译错误。本条不改变正式发布状态。

## ADDRESS-001 — 多 IPv4 追加、源地址和恢复归属

- Symptom / 现象：单 IP 替换流程不能直接满足业务／带外地址共存；默认探测可能使用其他本机出口；仅记录整份原配置会覆盖会话中外部新增地址。T36 首轮实机还出现对端地址恢复后立即 HTTP 断言失败，证据 `artifacts/maintenance/multi-ip-20261006/live-summary.json` 与原 FAILED 管理员计划保留。
- Trigger/cause / 条件与原因：Windows `New-NetIPAddress` 的 DHCP 行为与追加要求不一致；新 connected route 可以改变其他接口的有效路径。恢复探测的具体网络根因仍未知，不能将再次成功归因于 ARP、缓存或已修复。次轮观察为 Ping 先恢复、下一次 HTTP 恢复。
- Effective steps / 有效步骤：稳定网卡 ID → 当前全量地址／DHCP／默认路由／DNS／metric 读回 → 重叠路径检查 → 原共存开关读回 → 意图持久化 → netsh ActiveStore 追加 → 属性／DAD／业务配置读回。删除只限准确匹配的 manual ActiveStore 自有地址；原有地址、外部属性修改和未知结果不自动删除。最后清理原开关前检查外部固定地址。探测必须指定源 IP 和接口，并重新验证目标路由；异步 ICMP 缓冲区在取消后仍持有至原生完成，不能提前释放。上一会话恢复与新会话退出清理分别维护。
- Verification / 验证：2026-10-06 Release、源地址 ICMP/TCP、32 路真实 loopback 并发、无重定向、地址模型故障／取消／路由保护，以及 WPF 只读关闭保留 prior recovery 回归。隔离实机证据 `artifacts/maintenance/multi-ip-20261006-retry1/`：双网段 Ping/HTTP、对端离线／再联通、保留其他固定 IP 的真实单作用域 DORA、Windows DHCP 客户端租约与两个静态地址共存及真实 ciaddr 续租，Windows/Linux 独立前后读回一致，journal 为空。最终构建追加复验独立记录，不覆盖原失败。
- Scope / 边界：仅物理有线网卡、IPv4、已可靠识别的共存状态；本机 netsh 为英文字段，未验证其他语言／Windows 构建时阻止共存写入。多 IP 不负责 VLAN，不在共享业务 DHCP 广播域启动服务器。有限恢复采样是观察证据，不是已定位网络根因，也不替代 T31 第二实际网络诊断。


## Adding a lesson / 新增条目

Use a stable error/module ID and the same fields above; include the exact evidence date/source and distinguish confirmed cause, workaround and unknowns. Extend a matching entry rather than duplicating it. 采用稳定错误/模块编号；记录现象、条件、原因、步骤、验证和边界，注明证据日期／来源。只有重试恢复时写“恢复观察”，不写“已修复”；凭据及带签名的 URL 查询参数不得进入本页。

可直接复用以下模板；有同类条目时补充原条目的条件/证据，不新增同义编号。

```markdown
## <模块>-<编号> — <错误关键词与中文现象>

- Symptom / 现象：错误文本、触发操作及原失败证据。
- Trigger/cause / 条件与原因：commit、工具版本、环境；已确认原因或仍缺失的证据。
- Effective steps / 有效步骤：按执行顺序写步骤，链接已有工具/契约入口。
- Verification / 验证：日期、输出/报告、各批次通过/失败/跳过数量及证据位置。
- Scope / 边界：适用条件、未验证部分；明确已修复、恢复观察或待诊断。
```
