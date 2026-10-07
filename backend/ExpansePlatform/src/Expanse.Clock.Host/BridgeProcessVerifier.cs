using System.Diagnostics;

namespace Expanse.Clock.Host;

internal enum ProcessIdentityState { SameLiveProcess, Gone, Unknown }
internal interface IBridgeProcessVerifier
{
    ProcessIdentityState Verify(int processId, long startUtcTicks, string executablePath, string installNamespace, out string reason);
}

internal sealed class OperatingSystemBridgeProcessVerifier : IBridgeProcessVerifier
{
    public ProcessIdentityState Verify(int processId, long startUtcTicks, string executablePath, string installNamespace, out string reason)
    {
        reason = "";
        if (processId <= 0 || startUtcTicks <= 0 || string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(installNamespace))
        { reason = "Bridge process identity is incomplete."; return ProcessIdentityState.Unknown; }
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited) { reason = "Bridge process has exited."; return ProcessIdentityState.Gone; }
            var actualStart = process.StartTime.ToUniversalTime().Ticks;
            if (actualStart != startUtcTicks) { reason = "Bridge PID was reused by a different process start."; return ProcessIdentityState.Gone; }
            var actualPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath)) { reason = "Bridge executable path is unavailable."; return ProcessIdentityState.Unknown; }
            var expected = Path.GetFullPath(executablePath); var actual = Path.GetFullPath(actualPath);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) { reason = "Bridge executable path does not match its claimed process."; return ProcessIdentityState.Unknown; }
            if (!string.Equals(Path.GetFileName(actual), "KSP_x64.exe", StringComparison.OrdinalIgnoreCase)) { reason = "Bridge owner must be the KSP_x64.exe game process."; return ProcessIdentityState.Unknown; }
            var root = Path.GetFullPath(installNamespace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!actual.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { reason = "Bridge executable is outside its install namespace."; return ProcessIdentityState.Unknown; }
            reason = "Verified live game process."; return ProcessIdentityState.SameLiveProcess;
        }
        catch (ArgumentException) { reason = "Bridge process no longer exists."; return ProcessIdentityState.Gone; }
        catch (InvalidOperationException) { reason = "Bridge process exited during identity check."; return ProcessIdentityState.Gone; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException or IOException or ArgumentException)
        { reason = "Bridge process identity could not be verified: " + ex.Message; return ProcessIdentityState.Unknown; }
        finally { process?.Dispose(); }
    }
}
