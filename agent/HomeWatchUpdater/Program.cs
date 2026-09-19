using System.Diagnostics;
using System.IO.Compression;

if (args.Length != 3 || !int.TryParse(args[0], out var pid)) return;
var installDir = args[1]; var zipPath = args[2];
try { Process.GetProcessById(pid).WaitForExit(30000); } catch { }
var staging = Path.Combine(Path.GetTempPath(), "HomeWatchUpdate-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(staging);
try
{
    ZipFile.ExtractToDirectory(zipPath, staging, true);
    foreach (var source in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(staging, source); var dest = Path.Combine(installDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(source, dest, true);
    }
    var agent = Path.Combine(installDir, "HomeWatchAgent.exe");
    if (File.Exists(agent)) Process.Start(new ProcessStartInfo(agent) { UseShellExecute = true });
}
finally
{
    try { Directory.Delete(staging, true); } catch { }
    try { File.Delete(zipPath); } catch { }
}
