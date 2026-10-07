param([string]$Project)
$ErrorActionPreference = 'Stop'
$save = 'C:\Kerbal Space Program\saves\The Expanse\persistent.sfs'
$assembly = 'C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform\src\Expanse.Domain\bin\Release\net8.0\Expanse.Domain.dll'
Add-Type -Path $assembly
$line = (Select-String -Path $save -Pattern '^\s*stateBytesBase64\s*=').Line
$encoded = $line.Substring($line.IndexOf('=') + 1).Trim().Replace('-', '+').Replace('_', '/')
$state = [Expanse.Domain.AcceptedStateCodec]::Deserialize([Convert]::FromBase64String($encoded))
$snapshot = Get-Content (Join-Path $Project 'deliveries-snapshot.json') -Raw | ConvertFrom-Json
if ($state.WorldId -ne $snapshot.worldId) { throw 'Saved recovery world differs from Site delivery snapshot' }
$byId = @{}
foreach ($entry in $state.ActiveShipments) { $byId[$entry.ShipmentId] = $entry }
$verified = [ordered]@{}
foreach ($trip in $snapshot.shipments) {
  if ($trip.destinationId -ne 'kerbin-recovery') { continue }
  $entry = $byId[$trip.id]
  if ($null -eq $entry -or $entry.DestinationKind -ne 'virtualKerbinRecovery' -or $entry.FundsPerUnit -ne 100 -or $entry.RouteVersion -ne 2) { throw "Recovery terms not verified for $($trip.id)" }
  if ($trip.cargo.Count -ne 1 -or $trip.cargo[0].resource -ne 'Ore' -or [decimal]$trip.cargo[0].amount -ne ([decimal]$entry.RemainingResources[0].AmountMicroUnits / 1000000)) { throw "Recovery cargo differs for $($trip.id)" }
  $verified[$trip.id] = [ordered]@{ routeVersion = $entry.RouteVersion; fundsPerUnit = $entry.FundsPerUnit }
}
$result = [ordered]@{
  worldId = $snapshot.worldId
  snapshotSavedAt = $snapshot.savedAt
  recoveryDestinationId = 'kerbin-recovery'
  recoveryShipmentTerms = $verified
  fuelFundsPerUnit = [ordered]@{ LiquidFuel = 2; MonoPropellant = 3; Oxidizer = 0.4 }
  fuelRateBasis = 'User standard delivery fuel values; no funds transaction is implied'
}
$target = Join-Path $Project 'shipping-economics.json'
[IO.File]::WriteAllText($target, ($result | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "Verified $($verified.Count) recovery shipment terms from saved state; wrote $target"
