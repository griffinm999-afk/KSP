$ErrorActionPreference = 'Stop'
$relayHome = 'C:\Users\griff\AppData\Local\ExpanseFoundations\SiteRelay'
$relayPath = Join-Path $relayHome 'relay.cjs'
$nodePath = 'C:\Users\griff\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
$running = @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($relayPath) })
if ($running.Count -ne 0) { throw 'Relay already running; no change or duplicate launch performed' }
if ((Get-Content -LiteralPath $relayPath -Raw).Contains('const telemetryOnly=')) { throw 'Relay already modified; inspect before installation' }
$backupDir = Join-Path $PSScriptRoot ('telemetry-relay-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupDir | Out-Null
Copy-Item -LiteralPath $relayPath -Destination $backupDir
Copy-Item -LiteralPath (Join-Path $relayHome 'start.vbs') -Destination $backupDir
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'relay-telemetry.cjs') -Destination $relayPath
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-telemetry-only.vbs') -Destination (Join-Path $relayHome 'start-telemetry-only.vbs')
if ((Get-FileHash -LiteralPath $relayPath).Hash -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'relay-telemetry.cjs')).Hash) { throw 'Installed hash mismatch' }
$relayProcess = Start-Process -FilePath $nodePath -ArgumentList ('"' + $relayPath + '" --telemetry-only') -WorkingDirectory $relayHome -WindowStyle Hidden -PassThru
[pscustomobject]@{ mode='telemetry-only'; processId=$relayProcess.Id; backupDirectory=$backupDir; relaySha256=(Get-FileHash -LiteralPath $relayPath).Hash; launcher=(Join-Path $relayHome 'start-telemetry-only.vbs') } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $PSScriptRoot 'telemetry-relay-install-result.json')
