// Process fixture, never included in a product package.
using System.Text;

var versionFile = Path.Combine(AppContext.BaseDirectory, "probe-version.txt");
var version = File.ReadAllText(versionFile).Trim();
var mode = Environment.GetEnvironmentVariable("NETBOOT_TEST_HEALTH_MODE");
if (args.Length == 0)
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "previous-version-restarted.test"), version);
    return 0;
}
if (mode == "immediate-exit") return 9;
if (mode == "timeout") { await Task.Delay(TimeSpan.FromMinutes(2)); return 8; }
var values = Enumerable.Range(0, args.Length / 2).ToDictionary(index => args[index * 2], index => args[index * 2 + 1]);
if (mode == "version" || values["--update-target-version"] != version) return 7;
File.WriteAllText(values["--update-health-file"], values["--update-health-token"], new UTF8Encoding(false));
return 0;
