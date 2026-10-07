# Isolated Host restart and capsule-recovery procedure

This is a manual, bounded procedure for Sol to restart **only** the M3a development Host while the exact muted KSP development process stays alive. It can optionally exercise database-loss recovery after making a SQLite online backup. It has not been run by this fixture and does not authorize touching the production KSP or its Host/database.

## Preconditions and identity checks

Use the exact development root `C:\Users\griff\Documents\KSP-RMM-Dev`. Record the live dev KSP PID, Host PID, Host DLL path, four pipe names, and the explicit Host `--data-dir` from the command line used to start that Host. Do not discover or stop processes by wildcard name.

In PowerShell, check the exact KSP image and Host command line before doing anything:

```powershell
$devRoot = 'C:\Users\griff\Documents\KSP-RMM-Dev'
$kspPid = <recorded-dev-ksp-pid>
$hostPid = <recorded-isolated-host-pid>
$ksp = Get-CimInstance Win32_Process -Filter "ProcessId = $kspPid"
$host = Get-CimInstance Win32_Process -Filter "ProcessId = $hostPid"
if (-not $ksp -or [IO.Path]::GetFullPath($ksp.ExecutablePath) -ine (Join-Path $devRoot 'KSP_x64.exe')) { throw 'Refusing: KSP PID is not the exact dev installation.' }
if (-not $host -or $host.CommandLine -notmatch 'Expanse\.Clock\.Host\.dll') { throw 'Refusing: recorded PID is not the Expanse Host.' }
$host.CommandLine
```

Manually verify that the Host command line names the expected Release Host DLL, all four `ExpanseFoundations.*.dev.<Environment.UserName>.<token>` pipes for one token, `--enable-dev-counter`, and the exact existing `--data-dir`. The data directory must be the unique M3a dev directory recorded at launch, contain `recovery.sqlite3`, and be distinct from any production/local-app-data directory. Verify neither it nor any parent is a reparse point. If any process argument, path, PID or token is ambiguous, stop here. Confirm the same KSP PID and executable path again after every Host restart.

Use this path guard for the exact dev root, the data directory and any backup/archive parent:

```powershell
function Assert-NoReparseAncestor([string]$path) {
    $current = [IO.Path]::GetFullPath($path)
    if (Test-Path -LiteralPath $current -PathType Leaf) { $current = [IO.Path]::GetDirectoryName($current) }
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $entry = Get-Item -LiteralPath $current -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing reparse path ancestor: $current" }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent -ieq $current) { break }
        $current = $parent
    }
}
Assert-NoReparseAncestor $devRoot
Assert-NoReparseAncestor '<exact --data-dir shown above>'
```

The deployed game stays up. The bridge worker retries its Host effect connection and reattaches with the currently loaded capsule; do not reload KSP or select a different save during this procedure.

## Backup and ordinary Host restart

Choose a new backup filename outside the live data directory. The one-shot backup Host uses SQLite's online backup API and leaves the running Host process alone:

```powershell
$hostDll = '<exact Host DLL path shown above>'
$dataDir = '<exact --data-dir shown above>'
$backup = '<new backup path outside dataDir, e.g. a unique file under %TEMP%>'
if (-not (Test-Path -LiteralPath (Join-Path $dataDir 'recovery.sqlite3') -PathType Leaf)) { throw 'Refusing: expected isolated DB is missing.' }
if ([IO.Path]::GetFullPath($backup).StartsWith(([IO.Path]::GetFullPath($dataDir).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing: backup must be outside the live DB directory.' }
& dotnet $hostDll --data-dir $dataDir --backup-and-exit $backup
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $backup -PathType Leaf) -or (Get-Item -LiteralPath $backup).Length -le 0) { throw 'SQLite online backup did not complete.' }
Get-FileHash -LiteralPath $backup -Algorithm SHA256
```

Re-check that `$hostPid` still resolves to the verified Host command line and that `$kspPid` still resolves to the exact dev executable. Stop only the verified Host PID, wait for exit, then restart `dotnet $hostDll` with the **identical** publisher/view/effects/command pipe names and `--data-dir`, plus `--enable-dev-counter`. Use the original recorded arguments; do not substitute default pipe names or data paths. Record the new Host PID and verify its command line. Confirm the same KSP PID is still present. Observe the Host log for capsule attach and the fixture/bridge log for the new connection; do not submit a new counter command just to test reconnect.

## Optional database-loss recovery rehearsal

Only do this after the backup above succeeded and its hash is recorded. Re-check the exact Host PID, KSP PID, pipe token and data directory. Stop only the verified Host PID and wait for it to exit. Verify the database and sidecars are direct children of the exact data directory and are ordinary files, not reparse points. Move only these exact files aside in that directory; do not remove the directory or use a recursive delete:

```powershell
$db = Join-Path $dataDir 'recovery.sqlite3'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
foreach ($path in @($db, "$db-wal", "$db-shm")) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ine [IO.Path]::GetFullPath($dataDir)) { throw "Refusing unsafe DB path: $path" }
        Move-Item -LiteralPath $path -Destination "$path.loss-test-$stamp"
    }
}
```

Restart the Host with the original isolated arguments and verify the KSP PID/path again. The still-loaded bridge should reconnect by sending its full accepted capsule; Host should log/return that selected capsule was attached with earlier DB history unavailable. Verify the fixture's counter/revision/sequence and receipt before ending the run. Preserve the backup and moved DB files for comparison. If the selected-save capsule does not reattach, stop the rehearsal and report the exact Host/bridge logs; do not restore an external DB checkpoint over the game's capsule. The original DB can be restored only after stopping the verified isolated Host and preserving any new DB/sidecars created during the rehearsal.

## Scope and limits

This exercises Host process reconnect and, optionally, operational-state reconstruction from the live save capsule. It does not simulate KSP process restart, save selection/revert, crash timing, SQLite power loss, or any production behavior. The one-shot `--backup-and-exit` operation may create an empty DB if passed the wrong directory, which is why the existing exact `recovery.sqlite3` file and Host `--data-dir` must be verified first.
