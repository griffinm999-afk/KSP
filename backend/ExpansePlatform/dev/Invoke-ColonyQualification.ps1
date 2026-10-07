[CmdletBinding()]
param(
  [ValidateSet('Snapshot','Command','Control')][string]$Action='Snapshot',
  [string]$Kind, [string]$ColonyId='', [string]$TargetId='', [hashtable]$Fields=@{}
)
$ErrorActionPreference='Stop'
$platform=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=Split-Path -Parent $platform
$isolatedRoot='C:\Users\griff\Documents\Codex\KSP-Colony-Demo'
$manifest=Get-Content -LiteralPath (Join-Path $platform 'run\colony-development.json') -Raw | ConvertFrom-Json
$expectedExe=Join-Path $isolatedRoot 'KSP_x64.exe'
$process=Get-CimInstance Win32_Process -Filter ('ProcessId='+$manifest.ProcessId)
if (!$process -or $process.ExecutablePath -ine $expectedExe -or $manifest.Executable -ine $expectedExe -or $manifest.SaveFolder -notmatch '^ColonyBuild-[0-9]{8}-[0-9]{6}$') { throw 'Isolated process/save identity did not match.' }
$pipe=($manifest.Arguments | Where-Object { $_ -like '-expanseColonyPipe=*' }).Substring(19)
if ($pipe -cne ('ExpanseFoundations.Colonies.dev.'+[Environment]::UserName+'.'+$manifest.Token) -or !$process.CommandLine.Contains($pipe)) { throw 'Isolated IPC identity did not match.' }
$evidence=Join-Path $workspace 'outputs\colony-runtime-tests'
$operation=[guid]::NewGuid().ToString('D')
$prefix=Join-Path $evidence ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$operation)
$driver=Join-Path $platform 'dev\Expanse.Colony.RuntimeDriver\bin\Release\net8.0-windows\Expanse.Colony.RuntimeDriver.exe'
if ($Action -eq 'Control') {
  $path=Join-Path $isolatedRoot 'colony-control.cfg'
  if (Test-Path -LiteralPath $path) { throw 'An unconsumed control request exists; inspect before retrying.' }
  foreach ($key in $Fields.Keys) { if ($key -notin 'name','ut','sha256') { throw 'Unsupported control field.' } }
  $lines=@('authorization = verified-isolated-development-only', ('operationId = '+$operation), ('saveFolder = '+$manifest.SaveFolder), ('token = '+$manifest.Token), ('method = '+$Kind))
  foreach ($key in $Fields.Keys) {
    $value=[string]$Fields[$key]
    if ($value.Contains("`n") -or $value.Contains("`r") -or $value.Contains('{') -or $value.Contains('}')) { throw 'Invalid ConfigNode value.' }
    $lines+=($key+' = '+$value)
  }
  $lines | Set-Content -LiteralPath ($prefix+'-control.cfg')
  Copy-Item -LiteralPath ($prefix+'-control.cfg') -Destination $path
  $result=Join-Path $isolatedRoot ('colony-control-result-'+$operation+'.txt')
  for ($attempt=0; $attempt -lt 50 -and !(Test-Path -LiteralPath $result); $attempt++) { Start-Sleep -Milliseconds 100 }
  if (Test-Path -LiteralPath $result) { Copy-Item -LiteralPath $result -Destination ($prefix+'-result.txt'); Get-Content -LiteralPath $result }
  else { Write-Output ('Pending control '+$operation+'; inspect '+$path+' and '+$result+' before any retry.') }
  return
}
$snapshotPath=$prefix+'-snapshot.json'
& $driver --pipe $pipe --output $snapshotPath
if ($LASTEXITCODE -ne 0) { throw 'Native snapshot unavailable.' }
$snapshot=(Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json).Snapshot
if (!$snapshot.DevelopmentMode -or !$snapshot.State) { throw 'Selected world is not authorized for isolated qualification.' }
if ($Action -eq 'Snapshot') { Write-Output $snapshotPath; return }
$command=@{OperationId=$operation;ContextKey=$snapshot.ContextKey;ExpectedRevision=$snapshot.State.Revision;Kind=$Kind;ColonyId=$ColonyId;TargetId=$TargetId;Fields=$Fields}
$command | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath ($prefix+'-command.json')
& $driver --pipe $pipe --command-file ($prefix+'-command.json') --allow-development-mutation --output ($prefix+'-result.json')
Write-Output ('Retained exact command/result: '+$prefix)
if ($LASTEXITCODE -ne 0) { throw 'Command outcome needs reconciliation using the retained exact operation; do not create a replacement.' }
