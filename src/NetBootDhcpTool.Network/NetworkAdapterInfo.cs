namespace NetBootDhcpTool.Network;

public sealed class NetworkAdapterInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string InterfaceIndex { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string IPv4Address { get; set; } = "";
    public string SubnetMask { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string Dns { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsWifi { get; set; }
    public bool IsVirtual { get; set; }
    public bool HasGateway => !string.IsNullOrWhiteSpace(Gateway) && !Gateway.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase);
    public string DisplayName
    {
        get
        {
            var flags = new List<string>();
            if (IsWifi) flags.Add("Wi-Fi");
            if (IsVirtual) flags.Add("Virtual");
            if (!Status.Equals("Up", StringComparison.OrdinalIgnoreCase)) flags.Add(Status);
            var suffix = flags.Count == 0 ? "" : " [" + string.Join(", ", flags) + "]";
            var ip = string.IsNullOrWhiteSpace(IPv4Address) ? "No IPv4" : IPv4Address;
            return $"{Name} - {Description} - {ip}{suffix}";
        }
    }
    public override string ToString() => DisplayName;
}
