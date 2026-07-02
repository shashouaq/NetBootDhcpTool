# NetBoot DHCP Tool - Codex 开发指令说明书

> 用途：本文件用于直接交给 VS Code / Codex 插件阅读，让 Codex 根据本文档自动创建并开发一个 Windows 绿色免安装网络调试工具。  
> 项目建议名称：**NetBoot DHCP Tool**  
> 中文名称：**网口调试 DHCP 绿色工具**  
> 项目路径：`D:\project\NetBootDhcpTool`

---

## 1. 项目定位

开发一款 Windows 平台绿色免安装网络调试工具，主要用于现场调试设备。

核心目标：

1. 选择本地网卡。
2. 自动或手动配置本机网卡 IPv4 地址。
3. 开启 DHCP Server，通过指定网卡向外部设备分发 IP。
4. 显示已经分发的 IP。
5. 支持点击 IP 直接通过 HTTP 或 HTTPS 打开设备管理页面。
6. 支持手动设置本机网卡 IP 后扫描同网段在线设备。
7. 支持把常用手动 IP 配置加入收藏夹，便于下次一键调用。
8. 支持中文 / 英文界面。
9. 参考 Tftpd64 / Tftpd32 中的 DHCP 功能，但不要直接复制其源码。

一句话定位：

> 一款用于设备现场调试的绿色 DHCP / IP 扫描 / Web 跳转工具，适合工程师在隔离网络中快速给设备分配 IP、发现设备并打开管理页面。

---

## 2. 项目命名与图标建议

### 2.1 推荐项目名

英文名：

```text
NetBoot DHCP Tool
```

中文名：

```text
网口调试 DHCP 绿色工具
```

项目文件夹：

```text
D:\project\NetBootDhcpTool
```

命名理由：

- NetBoot 表示网络启动 / 网络调试场景。
- DHCP Tool 直接体现核心功能。
- 后续如果扩展 TFTP、Ping、Syslog、ONVIF、ARP 扫描等功能，名称仍然适用。

### 2.2 备选名称

```text
EasyDHCP Debugger
DHCP Lab Assistant
NetPort DHCP Assistant
QuickDHCP Tool
Device DHCP Helper
```

### 2.3 图标建议

图标主题：

```text
网卡 + IP + 分发节点 + 闪电
```

图标风格：

- 扁平化。
- 蓝色 / 青色主色。
- 图标元素包含：网口、网络节点、IP 标签、DHCP 分发符号。
- 文件名：`assets\app.ico`
- 同时生成：`assets\logo.png`

---

## 3. 参考工具说明

本项目功能交互参考开源工具 Tftpd64 / Tftpd32 中的 DHCP 工具。

参考点：

1. Tftpd64 是轻量级 Windows 网络工具，包含 TFTP、DHCP、DNS、SNTP、Syslog 等功能。
2. Tftpd64 的 DHCP 页面是一个简单 BOOTP/DHCP Server。
3. 用户填写 DHCP 参数后保存并启动 DHCP 分发。
4. 本项目只参考功能逻辑和交互方式，不直接复制源码。

注意：

- Tftpd64 GitHub 仓库许可证为 GPL-2.0。
- 如果直接复制或修改其源码，项目许可可能也要兼容 GPL。
- 本项目要求：**不要直接复制 Tftpd64 源码，只参考功能体验和业务逻辑。**

参考链接：

```text
https://github.com/PJO2/tftpd64
https://github.com/PJO2/tftpd64/wiki/The-BOOTP-DHCP-Server
https://pjo2.github.io/tftpd64/
```

---

## 4. 推荐技术路线

优先选择：

```text
C# + .NET 8 + WPF
```

原因：

1. Windows 桌面开发稳定。
2. 适合绿色免安装发布。
3. 修改网卡 IP、调用 PowerShell、管理进程、日志、JSON 配置都方便。
4. UI 开发比纯 WinForms 更适合后续美化。
5. 与 VS / Codex 开发体验匹配。

可选方案：

```text
C# + .NET 8 + WinForms
```

但默认不推荐，除非 WPF 实现遇到严重问题。

不建议第一版使用：

```text
Electron
Python GUI
Java Swing
Web 本地服务
```

原因：绿色发布体积、权限提升、系统网络配置调用和现场稳定性不如 C# WPF 直接。

---

## 5. 总体开发要求

请 Codex 按以下规则执行：

1. 自动创建项目路径：

```text
D:\project\NetBootDhcpTool
```

2. 自动生成完整项目结构。
3. 使用 C# + .NET 8 + WPF。
4. 最终生成绿色免安装版 exe。
5. 所有配置、日志、收藏夹、语言包保存在程序目录下。
6. 不依赖外部数据库。
7. 不依赖安装服务。
8. 不要求联网。
9. 第一版只支持 IPv4。
10. 支持中文 / 英文界面。
11. 默认根据系统语言自动选择界面语言。
12. 代码必须模块化。
13. 不要把业务逻辑全部写在 MainWindow.xaml.cs。
14. 必须包含日志。
15. 必须包含异常捕获。
16. 必须包含管理员权限检测。
17. 必须包含 DHCP 启动前安全确认。
18. 所有关键操作必须有中文 / 英文友好提示。

---

## 6. 权限要求

本工具需要管理员权限运行，因为需要：

1. 修改本地网卡 IP。
2. 设置 DNS。
3. 启动 DHCP Server 监听 UDP 67。
4. 可能需要开放或检查防火墙。
5. 扫描 ARP / 获取 MAC 信息。

程序启动时需要：

1. 检查当前进程是否管理员权限。
2. 如果不是管理员权限：
   - 弹窗提示。
   - 尝试以管理员身份重新启动。
   - 当前普通权限进程退出。
3. 如果管理员提权失败，显示明确错误信息。

---

## 7. 安全警告要求

DHCP Server 可能影响正式网络，因此必须加入防误操作设计。

### 7.1 启动 DHCP 前必须提示

提示内容：

```text
请确认当前网卡连接的是隔离调试网络，不要连接到办公网络、生产网络或已有 DHCP 的网络，否则可能造成 IP 冲突或网络异常。
```

英文：

```text
Please make sure the selected adapter is connected to an isolated test network. Do not use this DHCP server on an office network, production network, or a network that already has DHCP service. Otherwise, IP conflicts or network outages may occur.
```

### 7.2 必须勾选确认项

启动 DHCP 按钮前要求用户勾选：

```text
我确认当前网络为隔离调试网络
```

英文：

```text
I confirm this is an isolated test network
```

### 7.3 高风险场景提示

以下情况需要警告或默认禁止：

1. 当前网卡有默认网关。
2. 当前网卡是 Wi-Fi。
3. 当前网段检测到已有 DHCP 响应。
4. 当前 UDP 67 端口被占用。
5. 当前网卡连接状态异常。
6. 当前网卡疑似虚拟网卡。
7. 当前 IP 配置可能与已有网络冲突。

默认策略：

1. 默认不允许在 Wi-Fi 网卡启动 DHCP。
2. 默认不允许在有默认网关的网卡启动 DHCP。
3. 用户可在高级设置中打开强制允许，但必须二次确认。

---

## 8. 核心功能一：自动 DHCP 分发模式

### 8.1 页面名称

中文：

```text
自动 DHCP
```

英文：

```text
Auto DHCP
```

### 8.2 功能流程

1. 枚举本机所有可用网卡。
2. 用户选择一个本地网卡。
3. 用户配置 DHCP 参数。
4. 点击“开始 DHCP”。
5. 程序自动把选择的网卡设置为静态 IP。
6. 程序启动 DHCP Server。
7. 设备接入后自动分发 IP。
8. 界面实时显示分发记录。
9. 用户可以点击 IP 打开 HTTP / HTTPS。
10. 用户可以停止 DHCP。

### 8.3 网卡枚举字段

界面展示：

```text
网卡名称
网卡描述
接口索引 InterfaceIndex
MAC 地址
当前 IPv4 地址
子网掩码
默认网关
DNS
连接状态
是否 Wi-Fi
是否虚拟网卡
```

### 8.4 网卡过滤规则

默认排除：

1. Loopback。
2. Disabled 网卡。
3. Bluetooth 网卡。
4. 明显虚拟网卡，例如：
   - VMware
   - VirtualBox
   - Hyper-V Virtual
   - VPN
   - TAP
   - WSL
5. Wi-Fi 默认不允许启动 DHCP，但可以在高级设置里允许。

### 8.5 默认 DHCP 参数

```text
本机网卡 IP：192.168.100.1
子网掩码：255.255.255.0
DHCP 起始地址：192.168.100.100
DHCP 结束地址：192.168.100.200
网关：192.168.100.1
DNS：192.168.100.1
租约时间：3600 秒
```

### 8.6 DHCP 租约列表字段

```text
分发时间
设备 MAC
分发 IP
主机名 Hostname
租约开始时间
租约结束时间
租约状态
HTTP 状态
HTTPS 状态
备注
```

### 8.7 租约列表操作

每个 IP 支持：

```text
打开 HTTP
打开 HTTPS
复制 IP
Ping
重新探测 HTTP/HTTPS
加入备注
```

打开 HTTP：

```text
http://对应IP
```

打开 HTTPS：

```text
https://对应IP
```

使用系统默认浏览器打开，不需要内置浏览器。

### 8.8 停止 DHCP 逻辑

点击停止 DHCP 后：

1. 停止 UDP 监听。
2. 停止租约分发。
3. 保留当前租约列表。
4. 不强制恢复原网卡 IP，除非用户勾选：

```text
停止后恢复原始 IP 配置
```

---

## 9. 核心功能二：手动设置 IP + 同网段扫描模式

### 9.1 页面名称

中文：

```text
手动 IP / 扫描
```

英文：

```text
Manual IP / Scan
```

### 9.2 功能流程

1. 用户选择本地网卡。
2. 用户手动输入本机 IP、子网掩码、网关、DNS。
3. 点击“应用并扫描”。
4. 程序把配置应用到选中的本地网卡。
5. 程序自动计算同网段地址范围。
6. 自动 Ping 同网段 IP。
7. 能 Ping 通的 IP 自动展示到列表。
8. 同时探测 HTTP / HTTPS 是否可访问。
9. 支持点击 IP 打开网页。

### 9.3 手动配置字段

```text
本机 IP
子网掩码
网关，可选
DNS，可选
备注名称，可选
序列号，可选
```

### 9.4 扫描结果字段

```text
IP 地址
Ping 状态
延迟 ms
MAC 地址，如果能通过 ARP 获取
Hostname，如果能解析
HTTP 是否可访问
HTTPS 是否可访问
最后发现时间
备注
```

### 9.5 扫描操作

每个扫描结果支持：

```text
打开 HTTP
打开 HTTPS
复制 IP
重新 Ping
重新探测 HTTP/HTTPS
加入备注
```

### 9.6 扫描并发要求

默认并发：

```text
64
```

可配置：

```text
PingConcurrency
```

默认 Ping 超时：

```text
800ms
```

HTTP / HTTPS 探测超时：

```text
1000ms
```

### 9.7 扫描限制

1. 默认只扫描当前网段内可用主机地址。
2. 不扫描网络地址。
3. 不扫描广播地址。
4. /24 网段需要快速完成。
5. 对特别大的网段，例如 /16，要提示用户可能耗时较长，并要求确认。
6. 支持停止扫描。

---

## 10. 核心功能三：收藏夹功能

### 10.1 收藏夹用途

针对“手动 IP / 扫描模式”的常用配置，保存为收藏记录，便于下次一键加载。

### 10.2 收藏字段

收藏夹记录需要包含：

```text
收藏名称
序列号
备注名称
说明
网卡名称，可选
网卡 MAC，可选
本机 IP
子网掩码
网关
DNS
创建时间
更新时间
最后使用时间
```

### 10.3 收藏文件

保存路径：

```text
config\favorites.json
```

### 10.4 收藏夹功能

需要支持：

1. 新增收藏。
2. 编辑收藏。
3. 删除收藏。
4. 搜索收藏。
5. 加载收藏。
6. 一键应用并扫描。
7. 导入 favorites.json。
8. 导出 favorites.json。

### 10.5 收藏夹列表字段

```text
收藏名称
序列号
备注名称
本机 IP
子网掩码
网关
DNS
更新时间
最后使用时间
```

### 10.6 搜索字段

支持按以下字段搜索：

```text
收藏名称
序列号
备注名称
本机 IP
网关
说明
```

---

## 11. 核心功能四：中文 / 英文双语

### 11.1 语言文件路径

```text
i18n\zh-CN.json
i18n\en-US.json
```

### 11.2 语言策略

1. 程序首次启动时读取系统语言。
2. 中文系统默认中文。
3. 非中文系统默认英文。
4. 设置页面可手动切换语言。
5. 所有按钮、菜单、提示、日志、错误都走语言资源。
6. 切换语言后尽量立即生效；如果实现复杂，可提示重启生效。

### 11.3 语言文件要求

不能把界面文字硬编码在 XAML 或 C# 业务逻辑中。

---

## 12. 核心功能五：日志功能

### 12.1 日志路径

```text
logs\yyyy-MM-dd.log
```

### 12.2 日志内容

必须记录：

1. 程序启动。
2. 程序退出。
3. 当前管理员权限状态。
4. 枚举到的网卡信息。
5. 应用网卡 IP 的命令。
6. 应用网卡 IP 的执行结果。
7. DHCP 启动。
8. DHCP 停止。
9. DHCP 分发记录。
10. Ping 扫描开始 / 停止。
11. Ping 扫描结果。
12. HTTP / HTTPS 探测结果。
13. 收藏夹新增 / 修改 / 删除 / 加载。
14. 异常堆栈。
15. 防火墙 / 端口检测结果。

### 12.3 界面日志

界面底部需要显示实时日志。

支持：

```text
清空界面日志
打开日志目录
复制日志
```

注意：

清空界面日志不删除日志文件。

---

## 13. 核心功能六：Windows 网卡配置实现

### 13.1 服务类

创建：

```text
NetworkAdapterService
```

职责：

1. 枚举网卡。
2. 获取当前 IPv4 配置。
3. 保存原始配置。
4. 设置静态 IPv4。
5. 设置 DNS。
6. 恢复原始配置。
7. 检测是否管理员权限。
8. 检查是否 Wi-Fi。
9. 检查是否虚拟网卡。
10. 检查默认网关。
11. 刷新网卡状态。

### 13.2 Windows IP 配置方式

优先使用 PowerShell NetTCPIP 模块：

```powershell
Get-NetAdapter
Get-NetIPAddress
New-NetIPAddress
Set-NetIPAddress
Remove-NetIPAddress
Set-DnsClientServerAddress
Set-NetIPInterface
```

Microsoft 文档说明：

- `New-NetIPAddress` 用于创建并配置 IP 地址。
- `Set-NetIPAddress` 用于修改已有 IP 地址配置。
- DHCP 相关网络通信需要关注 UDP 67 / 68。

参考链接：

```text
https://learn.microsoft.com/en-us/powershell/module/nettcpip/new-netipaddress
https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-netipaddress
https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/troubleshoot-dhcp-guidance
```

### 13.3 网卡配置注意事项

1. 修改 IP 前保存原始配置。
2. 设置新 IP 前处理旧 IPv4 地址。
3. 不要误删其它网卡配置。
4. 不要修改 IPv6。
5. 不要修改非选中网卡。
6. 命令失败必须记录详细日志。
7. 命令失败必须给出友好提示。
8. 修改 IP 后等待网卡状态刷新。
9. 修改 IP 后重新读取确认实际配置。

---

## 14. 核心功能七：DHCP Server 实现

### 14.1 DHCP 服务类

创建：

```text
DhcpServer
DhcpPacketParser
DhcpLeaseManager
DhcpOptions
```

### 14.2 第一版最低支持 DHCPv4

必须支持：

1. DHCP Discover。
2. DHCP Offer。
3. DHCP Request。
4. DHCP ACK。
5. DHCP NAK，可选。
6. Lease 管理。
7. MAC 到 IP 的租约绑定。
8. 地址池耗尽提示。
9. Option 1：Subnet Mask。
10. Option 3：Router。
11. Option 6：DNS。
12. Option 51：Lease Time。
13. Option 53：DHCP Message Type。
14. Option 54：Server Identifier。

### 14.3 监听要求

1. DHCP Server 监听 UDP 67。
2. DHCP Client 通常使用 UDP 68。
3. 绑定到用户选择的网卡 IP。
4. 检测 UDP 67 是否被占用。
5. 如果端口被占用，提示具体原因。
6. 如果防火墙可能阻塞，提示用户检查。
7. DHCP 启动失败不能导致程序崩溃。

### 14.4 DHCP 地址池逻辑

1. 地址池范围必须与本机网卡 IP 在同一网段。
2. 起始 IP 不得大于结束 IP。
3. 不得把本机网卡 IP 分发给客户端。
4. 不得分发网络地址。
5. 不得分发广播地址。
6. 同一个 MAC 再次请求时优先返回原租约 IP。
7. 地址池耗尽时提示。
8. 租约过期后可回收。

---

## 15. 核心功能八：HTTP / HTTPS 探测

### 15.1 探测目标

对 DHCP 分发出的 IP 或 Ping 扫描发现的 IP，探测：

```text
http://IP
https://IP
```

### 15.2 探测要求

1. 默认超时时间 1000ms。
2. HTTPS 证书错误不要导致程序崩溃。
3. 只判断是否有响应，不要求校验证书。
4. 结果显示在列表中。
5. 点击打开时调用系统默认浏览器。
6. 不需要内置浏览器。

### 15.3 操作按钮

```text
打开 HTTP
打开 HTTPS
复制 IP
重新探测
```

---

## 16. 核心功能九：配置文件

### 16.1 appsettings.json 路径

```text
config\appsettings.json
```

### 16.2 默认配置示例

```json
{
  "Language": "auto",
  "PingConcurrency": 64,
  "PingTimeoutMs": 800,
  "HttpTimeoutMs": 1000,
  "AllowDhcpOnWifi": false,
  "AllowDhcpOnAdapterWithGateway": false,
  "RestoreIpOnDhcpStop": false,
  "DetectExistingDhcpBeforeStart": true,
  "DefaultDhcp": {
    "ServerIp": "192.168.100.1",
    "SubnetMask": "255.255.255.0",
    "PoolStart": "192.168.100.100",
    "PoolEnd": "192.168.100.200",
    "Gateway": "192.168.100.1",
    "Dns": "192.168.100.1",
    "LeaseSeconds": 3600
  }
}
```

### 16.3 配置要求

1. 程序首次启动时，如果 config 目录不存在，自动创建。
2. 如果 appsettings.json 不存在，自动生成默认配置。
3. 如果 favorites.json 不存在，自动生成空数组。
4. 配置读取失败时，生成错误日志，并回退默认配置。
5. JSON 格式错误时，不要程序崩溃。

---

## 17. 界面设计要求

### 17.1 主窗口布局

主窗口分为 5 个区域：

1. 顶部工具栏。
2. 网卡选择区。
3. 功能 Tab 区。
4. 结果列表区。
5. 底部实时日志区。

### 17.2 顶部工具栏

包含：

```text
语言切换
设置
打开日志
关于
```

### 17.3 网卡选择区

包含：

```text
网卡下拉框
刷新按钮
当前 IP
MAC
网关
状态
```

### 17.4 功能 Tab

```text
Tab 1：自动 DHCP
Tab 2：手动 IP / 扫描
Tab 3：收藏夹
Tab 4：设置
```

### 17.5 结果列表区

根据不同 Tab 展示不同内容：

1. 自动 DHCP：显示 DHCP 租约。
2. 手动 IP / 扫描：显示扫描结果。
3. 收藏夹：显示收藏记录。
4. 设置：显示程序设置。

### 17.6 底部日志区

包含：

```text
实时日志窗口
清空显示
打开日志目录
```

---

## 18. 项目目录结构

请自动创建：

```text
D:\project\NetBootDhcpTool
├─ src
│  ├─ NetBootDhcpTool.App
│  ├─ NetBootDhcpTool.Core
│  ├─ NetBootDhcpTool.Dhcp
│  ├─ NetBootDhcpTool.Network
│  └─ NetBootDhcpTool.Tests
├─ assets
│  ├─ app.ico
│  └─ logo.png
├─ config
│  ├─ appsettings.json
│  └─ favorites.json
├─ i18n
│  ├─ zh-CN.json
│  └─ en-US.json
├─ logs
├─ build
│  ├─ build.ps1
│  ├─ publish.ps1
│  └─ clean.ps1
├─ release
├─ README.md
└─ NetBootDhcpTool.sln
```

---

## 19. 推荐源码模块划分

### 19.1 NetBootDhcpTool.App

WPF UI 项目。

负责：

```text
MainWindow
Tab 页面
ViewModel
UI 绑定
语言切换
用户交互
```

### 19.2 NetBootDhcpTool.Core

公共核心项目。

负责：

```text
配置模型
日志接口
语言服务
通用工具
IP 地址计算
结果模型
异常模型
```

### 19.3 NetBootDhcpTool.Network

网络配置项目。

负责：

```text
网卡枚举
网卡 IP 设置
DNS 设置
管理员检测
Ping 扫描
ARP 查询
HTTP/HTTPS 探测
```

### 19.4 NetBootDhcpTool.Dhcp

DHCP 服务项目。

负责：

```text
DHCP Server
DHCP Packet Parser
Lease Manager
DHCP Options
DHCP Events
```

### 19.5 NetBootDhcpTool.Tests

测试项目。

至少测试：

```text
IP 网段计算
DHCP 地址池校验
收藏夹 JSON 读写
配置文件读写
```

---

## 20. README.md 要求

生成 README.md，内容包含：

1. 项目说明。
2. 功能介绍。
3. 编译环境。
4. 编译方法。
5. 运行方法。
6. 管理员权限说明。
7. DHCP 使用风险说明。
8. 绿色发布方法。
9. 配置文件说明。
10. 收藏夹说明。
11. 常见问题。
12. 后续规划。

---

## 21. 构建脚本要求

### 21.1 build\build.ps1

功能：

```text
还原 NuGet 包
编译 solution
输出编译结果
```

### 21.2 build\publish.ps1

功能：

```text
清理 release 目录
执行 dotnet publish
复制 config
复制 i18n
复制 assets
创建 logs 目录
生成绿色版发布目录
```

### 21.3 build\clean.ps1

功能：

```text
清理 bin
清理 obj
清理 release
```

---

## 22. MVP 第一版必须完成

第一版不要过度复杂，先完成以下功能：

1. 创建项目结构。
2. WPF 主界面。
3. 管理员权限检测和自动提权。
4. 枚举本地网卡。
5. 选择网卡。
6. 手动设置本机 IPv4。
7. Ping 扫描同网段。
8. 显示扫描结果。
9. HTTP / HTTPS 打开。
10. 收藏夹新增 / 删除 / 加载。
11. 基础 DHCP 分发。
12. DHCP 租约列表。
13. 中文 / 英文语言资源。
14. 日志文件。
15. 一键发布绿色版。

---

## 23. 后续增强功能

第一版完成后，再考虑：

1. TFTP Server。
2. Syslog Server。
3. ARP 主动扫描。
4. ONVIF 设备探测。
5. MAC 厂商识别。
6. DHCP MAC 白名单。
7. DHCP 固定 IP 绑定。
8. 扫描结果导出 CSV / Excel。
9. 收藏夹导入导出增强。
10. 自动识别设备 Web 端口。
11. 多网卡并行调试。
12. 操作审计日志。
13. 一键恢复网卡原配置。
14. 自动生成便携 ZIP 包。

---

## 24. Codex 直接执行指令

下面这段是可以直接发给 Codex 的执行命令说明。

```text
你现在需要在 Windows 平台开发一个绿色免安装网络调试工具，项目名称为 NetBoot DHCP Tool，中文名为“网口调试 DHCP 绿色工具”。

请在 D:\project 路径下自动创建项目文件夹：

D:\project\NetBootDhcpTool

项目目标：
开发一个类似 Tftpd64 中 DHCP 功能的轻量级绿色工具，主要用于现场调试设备。用户选择本地网卡后，可以快速给该网卡配置 IP，并通过该网卡开启 DHCP 服务，向外部设备分发 IP。工具还需要支持手动配置本地网卡 IP 后扫描同网段设备，并支持点击设备 IP 使用 HTTP 或 HTTPS 打开设备页面。

重要参考：
功能交互参考 Tftpd64 / Tftpd32 的 DHCP 工具，但不要直接复制其源码。仅参考功能逻辑和界面布局。

开发要求：
1. Windows 桌面程序。
2. 绿色免安装，最终可以直接运行 exe。
3. 支持中文 / 英文双语界面。
4. 默认语言根据系统语言自动判断，中文系统显示中文，非中文系统显示英文。
5. 所有配置、收藏夹、日志都保存在程序目录下，不依赖数据库服务。
6. 需要管理员权限运行，因为需要修改本地网卡 IP、开启 DHCP 监听、开放防火墙端口。
7. 程序启动时自动检测是否为管理员权限，如果不是管理员，提示并尝试以管理员权限重新启动。
8. 项目需要自动创建完整目录结构、源码、配置文件、README、构建脚本和图标资源。
9. 代码必须有清晰注释，结构清晰，便于后续维护。
10. 默认使用 C# + .NET 8 + WPF。
11. 本项目第一版只需要支持 IPv4，不要求 IPv6。

核心功能一：自动 DHCP 分发模式
1. 自动枚举本机所有启用状态的物理网卡。
2. 排除 Loopback、虚拟网卡、蓝牙网卡、禁用网卡。
3. 展示网卡名称、描述、MAC 地址、当前 IP、网关、连接状态。
4. 用户选择一个本地网卡。
5. 用户可以选择 DHCP 地址池配置，例如：
   - 本机网卡 IP：192.168.100.1
   - 子网掩码：255.255.255.0
   - DHCP 起始地址：192.168.100.100
   - DHCP 结束地址：192.168.100.200
   - 网关：默认等于本机网卡 IP
   - DNS：可为空或默认等于本机网卡 IP
   - 租约时间：默认 3600 秒
6. 点击“开始 DHCP”后，程序自动把选择的网卡配置为指定静态 IP。
7. 自动开启 DHCP Server，通过该网卡向外分发 IP。
8. 实时显示 DHCP 租约列表：
   - 分发时间
   - 设备 MAC
   - 分发 IP
   - 主机名 Hostname，如果能获取
   - 租约状态
   - HTTP 状态
   - HTTPS 状态
   - 备注
9. 租约列表中每个 IP 支持：
   - 打开 HTTP
   - 打开 HTTPS
   - 复制 IP
   - Ping
10. 点击 HTTP 时调用系统默认浏览器打开 http://对应IP。
11. 点击 HTTPS 时调用系统默认浏览器打开 https://对应IP。
12. 停止 DHCP 后释放监听端口，但不强制恢复网卡原始 IP，除非用户勾选“停止后恢复原始 IP”。

核心功能二：手动设置 IP + 同网段扫描模式
1. 用户选择本地网卡。
2. 用户手动输入：
   - 本机 IP
   - 子网掩码
   - 网关，可选
   - DNS，可选
3. 点击“应用并扫描”后：
   - 自动把该 IP 配置应用到选中的本地网卡。
   - 自动计算同网段 IP 范围。
   - 自动 Ping 同网段可用 IP。
4. 扫描结果列表显示：
   - IP 地址
   - Ping 状态
   - 延迟 ms
   - MAC 地址，如果可以通过 ARP 获取
   - Hostname，如果可以解析
   - HTTP 是否可访问
   - HTTPS 是否可访问
5. 对扫描到的 IP 支持：
   - 打开 HTTP
   - 打开 HTTPS
   - 复制 IP
   - 重新 Ping
6. 扫描时需要有进度条、当前扫描数量、已发现设备数量。
7. 支持停止扫描。
8. 扫描逻辑需要并发，但不能过高，默认并发 64，可在配置文件中调整。
9. 默认只扫描当前网段内可用主机地址，不扫描网络地址和广播地址。
10. 对 /24 网段要快速完成。

核心功能三：收藏夹功能
针对“手动 IP / 扫描模式”的配置，支持收藏夹。
收藏夹字段：
1. 收藏名称
2. 序列号
3. 备注名称
4. 说明
5. 网卡名称，可选
6. 本机 IP
7. 子网掩码
8. 网关
9. DNS
10. 创建时间
11. 更新时间
12. 最后使用时间

功能要求：
1. 用户在手动配置 IP 后，可以点击“加入收藏”。
2. 弹出窗口填写备注名称、序列号、说明。
3. 收藏信息保存在程序目录下 config\favorites.json。
4. 收藏夹界面以表格展示收藏名称、序列号、备注名称、IP、子网掩码、网关、更新时间。
5. 用户选择收藏记录后，可以一键加载配置。
6. 加载后自动填充 IP、掩码、网关、DNS 等字段。
7. 支持一键应用并扫描。
8. 支持编辑收藏。
9. 支持删除收藏。
10. 支持按收藏名称、序列号、备注名称、IP 搜索过滤。
11. 支持导入 / 导出 favorites.json，便于复制到其它电脑使用。

核心功能四：多语言支持
1. 支持中文和英文。
2. 语言文件：
   - i18n\zh-CN.json
   - i18n\en-US.json
3. 所有按钮、菜单、提示、日志、错误信息都走语言资源文件。
4. 程序首次启动根据系统语言自动选择。
5. 设置页面可以手动切换中文 / English。
6. 切换语言后立即生效或提示重启生效。

核心功能五：日志功能
日志保存路径：
logs\yyyy-MM-dd.log

日志内容：
1. 程序启动 / 退出。
2. 当前管理员权限状态。
3. 枚举到的网卡信息。
4. 应用网卡 IP 的命令和结果。
5. DHCP 启动 / 停止。
6. DHCP 分发记录。
7. Ping 扫描记录。
8. HTTP / HTTPS 探测记录。
9. 收藏夹新增 / 修改 / 删除。
10. 异常错误详情。

要求：
1. 界面底部显示实时日志。
2. 支持打开日志目录。
3. 支持清空界面日志，但不删除日志文件。
4. 异常必须捕获，不能程序直接崩溃。

核心功能六：安全提醒和防误操作
1. 启动 DHCP 前提示：
   “请确认当前网卡连接的是隔离调试网络，不要连接到办公网络、生产网络或已有 DHCP 的网络，否则可能造成 IP 冲突或网络异常。”
2. 用户需要勾选：
   “我确认当前网络为隔离调试网络”
3. 勾选后才能启动 DHCP。
4. 如果检测到当前网卡已有默认网关，提示可能连接到了正式网络。
5. 如果检测到当前网段已有 DHCP 响应，提示用户谨慎启动。
6. 默认不允许在 Wi-Fi 网卡上启动 DHCP，除非用户在高级设置中打开允许。
7. 默认不允许在有默认网关的网卡上启动 DHCP，除非用户确认强制启动。

核心功能七：Windows 网卡配置实现
需要封装 NetworkAdapterService。
功能：
1. 枚举网卡。
2. 获取网卡当前 IP。
3. 保存网卡原始配置。
4. 设置静态 IPv4。
5. 可选恢复原始配置。
6. 刷新网卡状态。
7. 检测管理员权限。

Windows 设置 IP 可以优先调用 PowerShell / NetTCPIP 模块：
- Get-NetAdapter
- Get-NetIPAddress
- New-NetIPAddress
- Set-NetIPAddress
- Remove-NetIPAddress
- Set-DnsClientServerAddress
- Set-NetIPInterface

注意：
1. 修改 IP 需要管理员权限。
2. 设置新 IP 前需要处理旧 IP。
3. 避免误删其它协议或其它网卡配置。
4. 所有命令执行结果要记录日志。
5. 命令失败时要给出中文 / 英文友好提示。

核心功能八：DHCP Server 实现
实现基础 DHCPv4 Server。
最低支持：
1. DHCP Discover
2. DHCP Offer
3. DHCP Request
4. DHCP ACK
5. DHCP NAK，可选
6. Lease 管理
7. MAC 到 IP 的租约绑定
8. 地址池耗尽提示
9. Option 1 Subnet Mask
10. Option 3 Router
11. Option 6 DNS
12. Option 51 Lease Time
13. Option 53 DHCP Message Type
14. Option 54 Server Identifier

监听要求：
1. 使用 UDP 67 作为 DHCP Server 端口。
2. 正确处理来自客户端 UDP 68 的请求。
3. 绑定到用户选择的网卡 IP。
4. 防火墙可能拦截 DHCP，程序需要检测并提示。
5. 如果端口被占用，要提示具体原因。

核心功能九：HTTP / HTTPS 探测
扫描或 DHCP 分发出 IP 后，需要尝试探测：
1. http://IP
2. https://IP

要求：
1. 探测超时时间默认 1000ms，可配置。
2. HTTPS 证书错误不要导致程序崩溃。
3. 列表中显示 HTTP 可用 / HTTPS 可用。
4. 点击按钮用系统默认浏览器打开。
5. 不需要内置浏览器。

项目目录结构：
D:\project\NetBootDhcpTool
├─ src
│  ├─ NetBootDhcpTool.App
│  ├─ NetBootDhcpTool.Core
│  ├─ NetBootDhcpTool.Dhcp
│  ├─ NetBootDhcpTool.Network
│  └─ NetBootDhcpTool.Tests
├─ assets
│  ├─ app.ico
│  └─ logo.png
├─ config
│  ├─ appsettings.json
│  └─ favorites.json
├─ i18n
│  ├─ zh-CN.json
│  └─ en-US.json
├─ logs
├─ build
│  ├─ build.ps1
│  ├─ publish.ps1
│  └─ clean.ps1
├─ release
├─ README.md
└─ NetBootDhcpTool.sln

核心配置文件 appsettings.json 示例字段：
{
  "Language": "auto",
  "PingConcurrency": 64,
  "PingTimeoutMs": 800,
  "HttpTimeoutMs": 1000,
  "AllowDhcpOnWifi": false,
  "AllowDhcpOnAdapterWithGateway": false,
  "RestoreIpOnDhcpStop": false,
  "DetectExistingDhcpBeforeStart": true,
  "DefaultDhcp": {
    "ServerIp": "192.168.100.1",
    "SubnetMask": "255.255.255.0",
    "PoolStart": "192.168.100.100",
    "PoolEnd": "192.168.100.200",
    "Gateway": "192.168.100.1",
    "Dns": "192.168.100.1",
    "LeaseSeconds": 3600
  }
}

核心界面布局：
主窗口分为 5 个区域：
1. 顶部工具栏：
   - 语言切换
   - 设置
   - 打开日志
   - 关于
2. 网卡选择区：
   - 网卡下拉框
   - 刷新按钮
   - 当前 IP
   - MAC
   - 网关
   - 状态
3. 功能 Tab：
   - Tab 1：自动 DHCP
   - Tab 2：手动 IP / 扫描
   - Tab 3：收藏夹
   - Tab 4：设置
4. 结果列表区：
   - DHCP 模式显示租约列表
   - 扫描模式显示扫描结果
   - 收藏夹显示收藏记录
5. 底部日志区：
   - 实时日志
   - 清空显示
   - 打开日志目录

交付要求：
1. 生成完整可编译项目。
2. 生成 README.md。
3. 生成 build\publish.ps1，一键发布绿色版到 release 目录。
4. 发布结果包含：
   - NetBootDhcpTool.exe
   - config
   - i18n
   - assets
   - logs
5. 程序第一次运行时，如果 config 或 i18n 文件缺失，要自动创建默认文件。
6. 所有异常都要友好提示，不允许直接崩溃。
7. 代码要模块化，不要把所有逻辑写在 MainWindow 里。
8. 先完成 MVP 版本：
   - 网卡枚举
   - 手动设置 IP
   - Ping 扫描
   - HTTP / HTTPS 跳转
   - 收藏夹
   - 基础 DHCP 分发
   - 中文 / 英文
   - 日志
9. 完成后再优化 UI 和高级功能。
```

---

## 25. Codex 少废话直接开发补充指令

如果 Codex 开始输出太多解释，直接追加下面内容：

```text
不要继续解释设计思路，直接开始创建项目和代码。
要求：
1. 先创建 D:\project\NetBootDhcpTool 目录结构。
2. 直接生成可编译的 .NET 8 WPF 项目。
3. 每完成一个模块就保证项目可以编译。
4. 不要省略代码。
5. 不要输出伪代码。
6. 不要只写示例。
7. 所有关键功能必须落到实际源码文件中。
8. 先实现 MVP，再做美化。
```

---

## 26. Codex 输出格式控制指令

```text
执行模式：直接开发模式。
不要长篇解释。
不要重复需求。
不要给多个方案。
默认选择 C# + .NET 8 + WPF。
遇到不确定项，选择最稳妥实现，不要反复询问。
每次回复只输出：
1. 已创建/修改的文件列表
2. 当前完成的功能
3. 下一步要实现的功能
4. 必要的运行命令
```

---

## 27. 验收标准

### 27.1 基础验收

1. 可以在 Windows 上启动。
2. 非管理员启动时可以提示并自动提权。
3. 可以枚举本机网卡。
4. 可以选择网卡。
5. 可以手动设置网卡 IP。
6. 可以扫描同网段设备。
7. 可以显示 Ping 通的 IP。
8. 可以点击 HTTP / HTTPS 打开设备页面。
9. 可以保存收藏。
10. 可以加载收藏。
11. 可以启动 DHCP。
12. 可以显示 DHCP 分发记录。
13. 可以停止 DHCP。
14. 有中文界面。
15. 有英文界面。
16. 有日志文件。
17. 可以绿色发布。

### 27.2 安全验收

1. DHCP 启动前必须有隔离网络确认。
2. 有默认网关的网卡默认禁止 DHCP。
3. Wi-Fi 网卡默认禁止 DHCP。
4. UDP 67 被占用时提示。
5. DHCP 启动失败不崩溃。
6. IP 设置失败不崩溃。
7. 扫描失败不崩溃。

### 27.3 现场可用性验收

1. 工具可以直接复制到其它 Windows 电脑运行。
2. 收藏夹可以随程序目录一起复制。
3. 日志可以用于排查问题。
4. 常用设备调试 IP 可以一键加载。
5. 扫描出的 IP 可以直接打开 Web 页面。

---

## 28. 重要注意点总结

1. DHCP 功能必须默认用于隔离调试网络，不能鼓励用户在办公网 / 生产网使用。
2. 修改网卡 IP 必须管理员权限。
3. DHCP 使用 UDP 67 / 68，可能被防火墙拦截。
4. Tftpd64 只作为功能参考，不要直接复制 GPL 源码。
5. 第一版不要追求功能过多，先完成 MVP。
6. 代码必须模块化，便于后续扩展 TFTP、Syslog、ONVIF、ARP 扫描。
7. 收藏夹是本项目的现场效率核心功能之一，必须做稳定。
8. 日志是现场排障核心，必须完整。
9. 中英文切换必须从一开始就设计，不要后期硬改。
10. 绿色免安装要求所有数据保存在程序目录下。
