$ErrorActionPreference='Stop'
[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'colony-diagnostic-work\src\Expanse.Clock.Core\bin\Release\net8.0\Expanse.Clock.Core.dll'))|Out-Null
$raw=[IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'rotation-unpruned-publisher.json'))
$diagnostics=[Collections.Generic.List[string]]::new()
$sink=[Action[string]]{param($text) $diagnostics.Add($text)}
$sample=[Expanse.Clock.Core.ClockProtocol]::DecodeClockSample([ReadOnlyMemory[byte]]::new($raw),$sink)
if($sample.Colony.Status -ne 'observed' -or $sample.Colony.Vessels.Length -ne 18 -or $diagnostics.Count -ne 0){throw 'Reconstructed publisher rejected by staged decoder'}
$viewRaw=[IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'rotation-unpruned-view.json'))
$view=[Text.Json.JsonSerializer]::Deserialize([Text.Encoding]::UTF8.GetString($viewRaw),[Expanse.Clock.Core.ClockView],[Expanse.Clock.Core.ClockProtocol]::JsonOptions)
[Expanse.Clock.Core.ClockProtocol]::Validate($view)
$encoded=[Expanse.Clock.Core.ClockProtocol]::EncodeView($view)
if($encoded.Length -ne $viewRaw.Length+4){throw 'Full wrapper size changed'}
$expected=@{'602524b9-f5c6-46b5-bd7e-879357299f5e'=6;'a89d0ac9-cf1b-4d96-ab72-8643a2a22bde'=6;'ff54e526-3a87-46de-921a-6f6e292da861'=3}
foreach($v in $sample.Colony.Vessels){if($expected.ContainsKey($v.VesselId) -and ($v.Production.Modules.Length -ne $expected[$v.VesselId] -or $v.Production.BudgetOmittedModuleCount -ne 0)){throw 'Target rows not retained'}}
[pscustomobject]@{status='decoder-and-wrapper-replay-pass';publisherBytes=$raw.Length;wrapperBytes=$viewRaw.Length;diagnostics=$diagnostics.Count;targetCounts=@{atlas=6;ag=6;duna=3};liveFailure='not reproduced by existing reconstruction'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'staged-diagnostic-replay-result.json')
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'staged-diagnostic-replay-result.json')
