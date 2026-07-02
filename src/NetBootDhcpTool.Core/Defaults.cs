namespace NetBootDhcpTool.Core;

public static class Defaults
{
    public static void EnsureFiles(AppPaths paths)
    {
        paths.Ensure();
        if (!File.Exists(paths.SettingsFile)) JsonStore.Save(paths.SettingsFile, new AppSettings());
        if (!File.Exists(paths.FavoritesFile))
        {
            JsonStore.Save(paths.FavoritesFile, DefaultFavorites());
        }
        else
        {
            var existing = JsonStore.LoadOrDefault(paths.FavoritesFile, new List<FavoriteConfig>());
            if (existing.Count == 0) JsonStore.Save(paths.FavoritesFile, DefaultFavorites());
        }
        var zh = Path.Combine(paths.I18nDirectory, "zh-CN.json");
        var en = Path.Combine(paths.I18nDirectory, "en-US.json");
        if (!File.Exists(zh)) JsonStore.Save(zh, Zh());
        if (!File.Exists(en)) JsonStore.Save(en, En());
    }

    public static Dictionary<string, string> Zh() => new()
    {
        ["app.title"] = "NetBoot DHCP Tool",
        ["language"] = "语言",
        ["refresh"] = "刷新",
        ["open.logs"] = "打开日志",
        ["about"] = "关于",
        ["adapter"] = "网卡",
        ["current.ip"] = "上次/当前 IP",
        ["mac"] = "MAC",
        ["gateway"] = "网关",
        ["status"] = "状态",
        ["auto.dhcp"] = "自动 DHCP",
        ["manual.scan"] = "手动 IP / 扫描",
        ["favorites"] = "收藏夹",
        ["settings"] = "设置",
        ["server.ip"] = "本机 IP",
        ["subnet.mask"] = "子网掩码",
        ["pool.start"] = "起始地址",
        ["pool.end"] = "结束地址",
        ["dns"] = "DNS",
        ["target.ip"] = "对端 IP",
        ["lease.seconds"] = "租约秒数",
        ["start.dhcp"] = "开始 DHCP",
        ["stop.dhcp"] = "停止 DHCP",
        ["isolated.confirm"] = "我确认当前网络为隔离调试网络",
        ["restore.stop"] = "停止后恢复原始 IP",
        ["apply.scan"] = "应用并扫描",
        ["stop.scan"] = "停止扫描",
        ["add.favorite"] = "加入收藏",
        ["new.favorite"] = "手动新增",
        ["delete"] = "删除",
        ["load"] = "加载",
        ["apply.and.scan"] = "应用并扫描",
        ["search"] = "搜索",
        ["clear.log"] = "清空显示",
        ["copy.log"] = "复制日志",
        ["use.local.ip"] = "使用本机 IP",
        ["warning.dhcp"] = "请确认当前网卡连接的是隔离调试网络，不要连接到办公网络、生产网络或已有 DHCP 的网络，否则可能造成 IP 冲突或网络异常。",
        ["blocked.wifi"] = "默认禁止在 Wi-Fi 网卡启动 DHCP。",
        ["blocked.gateway"] = "默认禁止在有默认网关的网卡启动 DHCP。",
        ["existing.dhcp"] = "检测到当前网络已有 DHCP 服务",
        ["blocked.virtual"] = "默认禁止在虚拟网卡启动 DHCP。",
        ["blocked.disconnected"] = "当前网卡未连接，不能启动 DHCP。",
        ["allow.wifi"] = "允许在 Wi-Fi 网卡启动 DHCP",
        ["allow.gateway"] = "允许在有默认网关的网卡启动 DHCP",
        ["detect.existing.dhcp"] = "启动前检测已有 DHCP 服务",
        ["save"] = "保存",
        ["dhcp.client.hint"] = "如果客户端 DHCP 超时，请按顺序尝试：重新获取 IP、禁用/启用客户端网卡、拔插对端网线、重启客户端网络服务或设备，并确认网线、交换机、网口指示灯正常。",
        ["dhcp.timeout.hint"] = "45 秒内没有收到客户端 DHCP 请求。请检查网线、交换机、网口指示灯，并在客户端重新获取 IP、禁用/启用网卡、拔插对端网线或重启客户端设备。",
        ["ok"] = "确定",
        ["cancel"] = "取消"
    };

    public static Dictionary<string, string> En() => new()
    {
        ["app.title"] = "NetBoot DHCP Tool",
        ["language"] = "Language",
        ["refresh"] = "Refresh",
        ["open.logs"] = "Open Logs",
        ["about"] = "About",
        ["adapter"] = "Adapter",
        ["current.ip"] = "Last / Current IP",
        ["mac"] = "MAC",
        ["gateway"] = "Gateway",
        ["status"] = "Status",
        ["auto.dhcp"] = "Auto DHCP",
        ["manual.scan"] = "Manual IP / Scan",
        ["favorites"] = "Favorites",
        ["settings"] = "Settings",
        ["server.ip"] = "Local IP",
        ["subnet.mask"] = "Subnet Mask",
        ["pool.start"] = "Pool Start",
        ["pool.end"] = "Pool End",
        ["dns"] = "DNS",
        ["target.ip"] = "Target IP",
        ["lease.seconds"] = "Lease Seconds",
        ["start.dhcp"] = "Start DHCP",
        ["stop.dhcp"] = "Stop DHCP",
        ["isolated.confirm"] = "I confirm this is an isolated test network",
        ["restore.stop"] = "Restore original IP after stop",
        ["apply.scan"] = "Apply and Scan",
        ["stop.scan"] = "Stop Scan",
        ["add.favorite"] = "Add Favorite",
        ["new.favorite"] = "New",
        ["delete"] = "Delete",
        ["load"] = "Load",
        ["apply.and.scan"] = "Apply and Scan",
        ["search"] = "Search",
        ["clear.log"] = "Clear",
        ["copy.log"] = "Copy Log",
        ["use.local.ip"] = "Use local IP",
        ["warning.dhcp"] = "Please make sure the selected adapter is connected to an isolated test network. Do not use this DHCP server on an office network, production network, or a network that already has DHCP service. Otherwise, IP conflicts or network outages may occur.",
        ["blocked.wifi"] = "Starting DHCP on Wi-Fi is blocked by default.",
        ["blocked.gateway"] = "Starting DHCP on an adapter with a default gateway is blocked by default.",
        ["existing.dhcp"] = "Existing DHCP service detected",
        ["blocked.virtual"] = "Starting DHCP on a virtual adapter is blocked by default.",
        ["blocked.disconnected"] = "The selected adapter is disconnected. DHCP cannot start.",
        ["allow.wifi"] = "Allow DHCP on Wi-Fi",
        ["allow.gateway"] = "Allow DHCP on adapter with default gateway",
        ["detect.existing.dhcp"] = "Detect existing DHCP before start",
        ["save"] = "Save",
        ["dhcp.client.hint"] = "If the client DHCP request times out, try renew IP, disable/enable the client adapter, unplug/replug the client cable, restart the client network service or device, and check cable, switch, and link lights.",
        ["dhcp.timeout.hint"] = "No client DHCP request was received within 45 seconds. Check cable, switch, and link lights, then renew IP, disable/enable the client adapter, unplug/replug the client cable, or restart the client device.",
        ["ok"] = "OK",
        ["cancel"] = "Cancel"
    };

    public static List<FavoriteConfig> DefaultFavorites()
    {
        var now = DateTime.Now;
        return
        [
            Bmc("Dell iDRAC default", "Dell", "192.168.0.120", "root", "calvin", "Common iDRAC factory default. Verify by model and site policy.", now),
            Bmc("HPE iLO default", "HPE", "192.168.1.1", "Administrator", "", "iLO password is often on the chassis tag; fixed IP varies by generation.", now),
            Bmc("Lenovo XCC default", "Lenovo", "192.168.70.125", "USERID", "PASSW0RD", "Common Lenovo XClarity Controller default. Verify before use.", now),
            Bmc("Supermicro IPMI default", "Supermicro", "192.168.100.100", "ADMIN", "ADMIN", "Newer devices may use a unique password on the label.", now),
            Bmc("Inspur BMC template", "Inspur", "192.168.1.100", "admin", "admin", "Template entry; verify actual project default.", now),
            Bmc("Huawei iBMC template", "Huawei", "192.168.2.100", "Administrator", "", "Template entry; verify actual project default.", now)
        ];
    }

    private static FavoriteConfig Bmc(string name, string vendor, string targetIp, string user, string password, string description, DateTime now) => new()
    {
        Name = name,
        DeviceNumber = vendor,
        LocalIp = LocalHostFor(targetIp),
        SubnetMask = "255.255.255.0",
        TargetIp = targetIp,
        Username = user,
        Password = password,
        Description = description,
        MemoryText = "BMC management port preset. Keep passwords updated according to site policy.",
        CustomFields =
        [
            new FavoriteField { Name = "Type", Value = "BMC" },
            new FavoriteField { Name = "Vendor", Value = vendor },
            new FavoriteField { Name = "Open", Value = "http://" + targetIp }
        ],
        CreatedAt = now,
        UpdatedAt = now
    };

    private static string LocalHostFor(string targetIp)
    {
        var parts = targetIp.Split('.');
        if (parts.Length != 4) return "";
        var host = parts[3] == "1" ? "2" : "1";
        return $"{parts[0]}.{parts[1]}.{parts[2]}.{host}";
    }
}
