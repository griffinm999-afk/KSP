$ErrorActionPreference='Stop'
$result=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'budget-install-result.json') -Raw | ConvertFrom-Json
$dll=Join-Path $result.versionRoot 'Host\Expanse.Clock.Core.dll'
[Reflection.Assembly]::LoadFrom($dll)|Out-Null
$fixture=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'live-clock-qualification.json') -Raw | ConvertFrom-Json
foreach($v in $fixture.colony.vessels){if($v.production){$v.production|Add-Member NoteProperty budgetOmittedModuleCount 1 -Force;$v.production|Add-Member NoteProperty budgetSelectionSequence ([long]$fixture.colony.vesselCensus.observationSequence) -Force;$v.production.status='truncated';$v.production.inventoryStatus='partial'}}
$json=$fixture|ConvertTo-Json -Depth 100 -Compress
$view=[Text.Json.JsonSerializer]::Deserialize($json,[Expanse.Clock.Core.ClockView],[Expanse.Clock.Core.ClockProtocol]::JsonOptions)
[Expanse.Clock.Core.ClockProtocol]::Validate($view)
$wire=[Expanse.Clock.Core.ClockProtocol]::EncodeView($view)
$body=[Text.Encoding]::UTF8.GetString($wire,4,$wire.Length-4)
$decoded=[Text.Json.JsonSerializer]::Deserialize($body,[Expanse.Clock.Core.ClockView],[Expanse.Clock.Core.ClockProtocol]::JsonOptions)
[Expanse.Clock.Core.ClockProtocol]::Validate($decoded)
if($decoded.Colony.VesselCensus.VesselIds.Count -ne $fixture.colony.vesselCensus.vesselIds.Count){throw 'Census round trip mismatch'}
if($decoded.Colony.Vessels.Count -ne $fixture.colony.vessels.Count){throw 'Vessel round trip mismatch'}
foreach($v in $decoded.Colony.Vessels){$original=$fixture.colony.vessels|Where-Object vesselId -eq $v.VesselId;if($v.CrewRosterComplete -ne $original.crewRosterComplete){throw 'Roster completeness mismatch'};if($v.Production -and $v.Production.BudgetOmittedModuleCount -lt 1){throw 'Budget marker lost'}}
[pscustomobject]@{installedCorePath=$dll;installedCoreSha256=(Get-FileHash -LiteralPath $dll).Hash;wireBodyBytes=$wire.Length-4;censusIds=$decoded.Colony.VesselCensus.VesselIds.Count;vessels=$decoded.Colony.Vessels.Count;rosterCompletenessPreserved=$true;productionBudgetFieldsAccepted=$true;fixture='Synthetic budget fields on prior read-only physical frame; no live publisher or game mutation'}|ConvertTo-Json|Tee-Object -FilePath (Join-Path $PSScriptRoot 'installed-budget-wire-verification.json')
