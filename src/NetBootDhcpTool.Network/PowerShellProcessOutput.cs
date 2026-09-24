using System.Diagnostics;

namespace NetBootDhcpTool.Network;

internal static class PowerShellProcessOutput
{
    public static async Task<(string Output, string Error)> ReadStandardStreamsAsync(Process process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask);
        return (await outputTask, await errorTask);
    }
}
