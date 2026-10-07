using System.Diagnostics;

var script = Path.Combine(AppContext.BaseDirectory, "Launch-Expanse-Foundations.ps1");
if (!File.Exists(script))
{
    Log($"Missing launcher script: {script}");
    return;
}

try
{
    var start = new ProcessStartInfo("powershell.exe")
    {
        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
        WorkingDirectory = AppContext.BaseDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
    };
    Process.Start(start);
}
catch (Exception exception)
{
    Log(exception.ToString());
}

static void Log(string message)
{
    try
    {
        var runDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExpanseFoundations", "Run");
        Directory.CreateDirectory(runDir);
        File.AppendAllText(Path.Combine(runDir, "launcher.log"),
            $"{DateTimeOffset.UtcNow:o} {message}{Environment.NewLine}");
    }
    catch { /* Logging must never prevent launch or exit. */ }
}
