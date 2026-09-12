namespace NetBootDhcpTool.App;

public enum RecoveryEntryKind
{
    AdapterConfiguration,
    MacAddress,
    StaticRoute
}

public sealed class RecoveryEntryViewModel
{
    public required RecoveryEntryKind Kind { get; init; }
    public required object Payload { get; init; }
    public required string TypeDisplay { get; init; }
    public required string AdapterName { get; init; }
    public required string IdentityDisplay { get; init; }
    public required string CapturedAtDisplay { get; init; }
    public required string Summary { get; init; }
    public required string StatusDisplay { get; init; }
    public bool IsAvailable { get; init; }
}
