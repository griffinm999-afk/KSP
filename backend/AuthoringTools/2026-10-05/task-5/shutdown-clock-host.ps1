param([int]$TargetProcessId)
$ErrorActionPreference='Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ConsoleStop {
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool FreeConsole();
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool AttachConsole(uint pid);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler,bool add);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GenerateConsoleCtrlEvent(uint evt,uint group);
}
'@
[ConsoleStop]::FreeConsole() | Out-Null
if(-not [ConsoleStop]::AttachConsole([uint32]$TargetProcessId)){throw 'Could not attach Host console; no forced stop attempted.'}
try {
 [ConsoleStop]::SetConsoleCtrlHandler([IntPtr]::Zero,$true) | Out-Null
 if(-not [ConsoleStop]::GenerateConsoleCtrlEvent(0,0)){throw 'Could not request graceful Host shutdown.'}
 Start-Sleep -Milliseconds 500
} finally {[ConsoleStop]::FreeConsole() | Out-Null}
try {$target=Get-Process -Id $TargetProcessId -ErrorAction Stop; if(-not $target.WaitForExit(15000)){throw 'Host did not exit gracefully; no forced stop attempted.'}}catch [Microsoft.PowerShell.Commands.ProcessCommandException] {}
