[CmdletBinding()]
param([Parameter(Mandatory)][string]$CommandFile,[Parameter(Mandatory)][string]$OutputPrefix,[switch]$Submit)
$ErrorActionPreference='Stop'
$platform=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$commandPath=[IO.Path]::GetFullPath($CommandFile)
$output=[IO.Path]::GetFullPath($OutputPrefix)
$commandBytes=[IO.File]::ReadAllBytes($commandPath)
if ($commandBytes.Length -gt 512KB) { throw 'Reviewed command exceeds the bounded frame.' }
$command=[Text.Encoding]::UTF8.GetString($commandBytes) | ConvertFrom-Json
if (!$command.OperationId -or !$command.ContextKey -or !$command.Kind -or !$command.ColonyId) { throw 'Reviewed exact command is incomplete.' }
$operation=[guid]::Parse($command.OperationId)
if (!$Submit) {
  Write-Output ('Review only: '+$command.Kind+' / '+$operation+' / context '+$command.ContextKey)
  Write-Output 'No process or IPC access occurred. -Submit is for the runtime owner after reviewing this exact file.'
  return
}
# No production fallback. Check the current manifest and all four namespaces
# immediately before the separate native submission, never modify that manifest.
$root='C:\Users\griff\Documents\Codex\KSP-Colony-Demo'
$manifest=Get-Content -LiteralPath (Join-Path $platform 'run\colony-development.json') -Raw | ConvertFrom-Json
$expectedExe=Join-Path $root 'KSP_x64.exe'
$process=Get-CimInstance Win32_Process -Filter ('ProcessId='+$manifest.ProcessId)
if (!$process -or $process.ExecutablePath -ine $expectedExe -or $manifest.Executable -ine $expectedExe -or $manifest.SaveFolder -notmatch '^ColonyBuild-[0-9]{8}-[0-9]{6}$' -or $manifest.Token -notmatch '^[a-f0-9]{32}$') { throw 'Isolated native identity is unavailable or changed.' }
foreach ($relative in @('', ('saves\'+$manifest.SaveFolder))) {
  $item=Get-Item -LiteralPath (Join-Path $root $relative)
  if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Isolated installation/save must not redirect to another path.' }
}
$names=@{
  '-expanseClockPublisherPipe='='ExpanseFoundations.Clock.Publisher.dev.'
  '-expanseEffectsPipe='='ExpanseFoundations.Effects.dev.'
  '-expanseWolfPipe='='ExpanseFoundations.WOLF.Admin.dev.'
  '-expanseColonyPipe='='ExpanseFoundations.Colonies.dev.'
}
$pipe=''
foreach($argPrefix in $names.Keys) {
  $expected=$names[$argPrefix]+[Environment]::UserName+'.'+$manifest.Token
  $matches=@($manifest.Arguments | Where-Object { $_.StartsWith($argPrefix,[StringComparison]::Ordinal) })
  if ($matches.Count -ne 1 -or $matches[0] -cne ($argPrefix+$expected) -or !$process.CommandLine.Contains($argPrefix+$expected)) { throw 'One isolated IPC identity did not match the current process.' }
  if ($argPrefix -eq '-expanseColonyPipe=') { $pipe=$expected }
}
foreach($suffix in @('-before.json','-result.json','-command.json')) { if(Test-Path -LiteralPath ($output+$suffix)) { throw 'Evidence prefix already exists; exact files are immutable.' } }
$driver=Join-Path $platform 'dev\Expanse.Colony.RuntimeDriver\bin\Release\net8.0-windows\Expanse.Colony.RuntimeDriver.exe'
if (!(Test-Path -LiteralPath $driver)) { throw 'Build the reviewed RuntimeDriver Release artifact first.' }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
& $driver --pipe $pipe --output ($output+'-before.json')
if ($LASTEXITCODE -ne 0) { throw 'Selected-save snapshot unavailable; nothing was submitted.' }
$before=(Get-Content -LiteralPath ($output+'-before.json') -Raw | ConvertFrom-Json).Snapshot
$retained=@($before.State.Receipts | Where-Object { $_.OperationId -ceq $command.OperationId })
if (!$before.DevelopmentMode -or !$before.State -or $before.ContextKey -cne $command.ContextKey -or ($before.State.Revision -ne $command.ExpectedRevision -and $retained.Count -ne 1)) { throw 'Reviewed command belongs to an old context/revision with no retained operation. Do not replace an uncertain request; inspect its durable witnesses.' }
# A retained ID permits exact-byte retry after a lost reply. The native engine
# still verifies its full payload hash before returning the duplicate receipt.
# Preserve original bytes; never change OperationId, quote or payload on a retry.
[IO.File]::WriteAllBytes($output+'-command.json',$commandBytes)
& $driver --pipe $pipe --command-file ($output+'-command.json') --allow-development-mutation --output ($output+'-result.json')
if ($LASTEXITCODE -ne 0) { throw ('Submission needs reconciliation. Retain and retry only '+$output+'-command.json with the same operation ID; do not create a replacement.') }
$result=Get-Content -LiteralPath ($output+'-result.json') -Raw | ConvertFrom-Json
Write-Output ($result.Outcome+' · '+$result.Reason)
if ($result.Result) { Write-Output ($result.Result.Outcome+' · '+$result.Result.Reason) }
Write-Output ('Recorded exact reviewed request and native response: '+$output)
