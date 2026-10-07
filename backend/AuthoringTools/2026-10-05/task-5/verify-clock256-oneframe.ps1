param([string]$SourceRoot)
$ErrorActionPreference='Stop'
$stage=if($SourceRoot){$SourceRoot}else{Join-Path $PSScriptRoot 'performance-work'}
[Reflection.Assembly]::LoadFrom((Join-Path $stage 'src\Expanse.Clock.Core\bin\Release\net8.0\Expanse.Clock.Core.dll'))|Out-Null
$raw=[IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'rotation-unpruned-view.json'))
$view=[Text.Json.JsonSerializer]::Deserialize([Text.Encoding]::UTF8.GetString($raw),[Expanse.Clock.Core.ClockView],[Expanse.Clock.Core.ClockProtocol]::JsonOptions)
[Expanse.Clock.Core.ClockProtocol]::Validate($view)
$encoded=[Expanse.Clock.Core.ClockProtocol]::EncodeView($view)
if($encoded.Length -ne $raw.Length+4){throw 'One-frame view was altered or budgeted'}
$decoded=[Text.Json.JsonSerializer]::Deserialize([Text.Encoding]::UTF8.GetString($encoded,4,$encoded.Length-4),[Expanse.Clock.Core.ClockView],[Expanse.Clock.Core.ClockProtocol]::JsonOptions)
[Expanse.Clock.Core.ClockProtocol]::Validate($decoded)
$counts=@{};foreach($v in $decoded.Colony.Vessels){if($v.Production){$counts[$v.VesselId]=$v.Production.Modules.Length;if($v.Production.BudgetOmittedModuleCount -ne 0){throw 'Unexpected omission'}}}
foreach($entry in @{'602524b9-f5c6-46b5-bd7e-879357299f5e'=6;'a89d0ac9-cf1b-4d96-ab72-8643a2a22bde'=6;'ff54e526-3a87-46de-921a-6f6e292da861'=3}.GetEnumerator()){if($counts[$entry.Key]-ne $entry.Value){throw 'Target missing from single frame'}}
if(![Text.Json.Nodes.JsonNode]::DeepEquals([Text.Json.Nodes.JsonNode]::Parse([Text.Encoding]::UTF8.GetString($raw)),[Text.Json.Nodes.JsonNode]::Parse([Text.Encoding]::UTF8.GetString($encoded,4,$encoded.Length-4)))){throw 'One-frame semantic mutation'}
[pscustomobject]@{status='one-frame-pass';bodyBytes=$encoded.Length-4;cap=262144;targets=@{atlas=6;duna=3;ag=6};censusIds=$decoded.Colony.VesselCensus.VesselIds.Length;vessels=$decoded.Colony.Vessels.Length;rostersAndAllFieldsUnchanged=$true;budgetOmissions=0;fixture='90 reconstructed known rows, 92 live inventory count. Missing callbacks remain unknown; original full wrapper retained.'}|ConvertTo-Json -Depth 5|Tee-Object -FilePath (Join-Path $PSScriptRoot 'clock256-oneframe-result.json')
