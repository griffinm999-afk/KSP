[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$platformRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$hostProject = Join-Path $platformRoot 'src\Expanse.Clock.Host\Expanse.Clock.Host.csproj'
$managerProject = Join-Path $platformRoot 'src\Expanse.Clock.Manager\Expanse.Clock.Manager.csproj'
$hostExe = Join-Path $platformRoot "src\Expanse.Clock.Host\bin\$Configuration\net8.0\Expanse.Clock.Host.exe"
$managerExe = Join-Path $platformRoot "src\Expanse.Clock.Manager\bin\$Configuration\net8.0-windows\Expanse.Clock.Manager.exe"

if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf)) {
  dotnet build $hostProject --configuration $Configuration
  if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
}
if (-not (Test-Path -LiteralPath $managerExe -PathType Leaf)) {
  dotnet build $managerProject --configuration $Configuration
  if ($LASTEXITCODE -ne 0) { throw 'Manager build failed.' }
}

function Find-Running([string]$Name, [string]$ExecutablePath) {
  Get-CimInstance Win32_Process -Filter "Name='$Name'" | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.Equals($ExecutablePath, [StringComparison]::OrdinalIgnoreCase)
  } | Select-Object -First 1
}

$runDir = Join-Path $platformRoot 'run'
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$hostProcess = Find-Running 'Expanse.Clock.Host.exe' $hostExe
if ($hostProcess) {
  Write-Output "Reusing Clock Host PID $($hostProcess.ProcessId)."
  $hostId = $hostProcess.ProcessId
} else {
  $startedHost = Start-Process -FilePath $hostExe -WorkingDirectory (Split-Path $hostExe) -WindowStyle Hidden -PassThru
  $hostId = $startedHost.Id
  Start-Sleep -Milliseconds 400
  Write-Output "Started Clock Host PID $hostId."
}
Set-Content -LiteralPath (Join-Path $runDir 'clock-host.pid') -Value $hostId
Set-Content -LiteralPath (Join-Path $runDir 'clock-host.path') -Value $hostExe
Set-Content -LiteralPath (Join-Path $runDir 'clock-host.args') -Value ''

$managerProcess = Find-Running 'Expanse.Clock.Manager.exe' $managerExe
if ($managerProcess) {
  Write-Output "Reusing Clock Manager PID $($managerProcess.ProcessId)."
  $managerId = $managerProcess.ProcessId
} else {
  $startedManager = Start-Process -FilePath $managerExe -WorkingDirectory (Split-Path $managerExe) -PassThru
  $managerId = $startedManager.Id
  Write-Output "Opened Clock Manager PID $managerId."
}
Set-Content -LiteralPath (Join-Path $runDir 'clock-manager.pid') -Value $managerId
Set-Content -LiteralPath (Join-Path $runDir 'clock-manager.path') -Value $managerExe
Set-Content -LiteralPath (Join-Path $runDir 'clock-manager.args') -Value ''
