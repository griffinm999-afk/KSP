$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$stage='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'
$directories=@((Join-Path $stage 'src\Expanse.WorldBridge\bin\Release\net472'),(Join-Path $stage 'src\Expanse.Domain\bin\Release\net472'),'C:\Kerbal Space Program\KSP_x64_Data\Managed','C:\Kerbal Space Program\GameData\000_Harmony','C:\Kerbal Space Program\GameData\000_USITools','C:\Kerbal Space Program\GameData\UmbraSpaceIndustries\WOLF')
$resolver=[ResolveEventHandler]{param($sender,$eventArgs) $name=[Reflection.AssemblyName]::new($eventArgs.Name).Name;foreach($d in $directories){$p=Join-Path $d ($name+'.dll');if([IO.File]::Exists($p)){return [Reflection.Assembly]::LoadFrom($p)}}return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
 $bridge=[Reflection.Assembly]::LoadFrom((Join-Path $directories[0] 'Expanse.WorldBridge.dll'))
 $core=[Reflection.Assembly]::LoadFrom((Join-Path $stage 'src\Expanse.Clock.Core\bin\Release\net8.0\Expanse.Clock.Core.dll'))
 function New-Telemetry($name,$values){$x=[Activator]::CreateInstance($bridge.GetType('Expanse.WorldBridge.'+$name,$true));foreach($entry in $values.GetEnumerator()){$x.GetType().GetField($entry.Key).SetValue($x,$entry.Value)}return $x}
 function Typed-Array($name,$values){$a=[Array]::CreateInstance($bridge.GetType('Expanse.WorldBridge.'+$name,$true),$values.Count);for($i=0;$i -lt $values.Count;$i++){$a.SetValue($values[$i],$i)}return ,$a}
 $serializer=$bridge.GetType('Expanse.WorldBridge.WorldBridgeAddon',$true).GetMethod('ProductionTelemetryJson',[Reflection.BindingFlags]'Static,NonPublic')
 $originalPublisher=Get-Content (Join-Path $root 'rotation-unpruned-publisher.json') -Raw
 $originalView=Get-Content (Join-Path $root 'rotation-unpruned-view.json') -Raw
 $atlas='602524b9-f5c6-46b5-bd7e-879357299f5e'
 $at=[double](($originalPublisher|ConvertFrom-Json).colony.observedUt)
 $empty=Typed-Array 'ColonyProductionRateTelemetry' @()
 $configured=New-Telemetry 'ColonyProductionVector' @{Inputs=$empty;Outputs=$empty}
 $harvest=New-Telemetry 'ColonyProductionHarvester' @{Resource='Gypsum';Efficiency=[double]26.2;HarvestThreshold=[double]0;HarvesterType=[int]0}
 $results=@()
 foreach($case in @('model-positive','model-zero','unknown','actual-positive','actual-zero','reject-stale','reject-future','reject-mixed')){
  $value=if($case -match 'zero'){[double]0}else{[double]2}
  $rate=New-Telemetry 'ColonyProductionRateTelemetry' @{Resource='Gypsum';UnitsPerSecond=$value;FlowMode='ALL_VESSEL';DumpExcess=$false}
  $rates=Typed-Array 'ColonyProductionRateTelemetry' @($rate)
  $row=New-Telemetry 'ColonyProductionModuleTelemetry' @{PartId=[uint32]1021797267;ModuleId=[uint32]3734604494;ModuleIndex=[int]6;ModuleType='USITools.USI_Harvester';PartName='SYNTHETIC Atlas wire fixture';Recipe='Gypsum';RecipeHash=('a'*64);Enabled=$true;Activated=$true;Basis='loaded-broker';Configured=$configured;Harvester=$harvest;Reason='SYNTHETIC DETACHED reconstructed wire fixture; no current native rates.'}
  if($case -match '^model|^reject'){
   $stamp=if($case -eq 'reject-stale'){$at-11}elseif($case -eq 'reject-future'){$at+2}else{$at}
   $row.Basis='background-model';$row.Background=New-Telemetry 'ColonyProductionBackgroundRate' @{SampleUt=[double]$stamp;ConstraintState='BOUNDARY';Inputs=$empty;Outputs=$rates}
  }
  if($case -match '^actual' -or $case -eq 'reject-mixed'){
   $potentialRate=New-Telemetry 'ColonyProductionRateTelemetry' @{Resource='Gypsum';UnitsPerSecond=[double]3;FlowMode='ALL_VESSEL';DumpExcess=$false}
   $potential=New-Telemetry 'ColonyProductionVector' @{Inputs=$empty;Outputs=(Typed-Array 'ColonyProductionRateTelemetry' @($potentialRate))}
   $row.Prepared=New-Telemetry 'ColonyProductionPotential' @{SampleUt=$at;EfficiencyMultiplier=[double]1;RequirementMultiplier=[double]1;Rates=$potential}
   $row.Achieved=New-Telemetry 'ColonyProductionAchieved' @{SampleUt=$at;IntervalGameSeconds=[double]1;TimeFactor=[double]1;CaptureSequence=[long]1;Inputs=$empty;Outputs=$rates}
  }
  $production=New-Telemetry 'ColonyProductionTelemetry' @{Status='partial';Reason='SYNTHETIC DETACHED wire test.';ObservedUt=$at;InventoryStatus='complete-supported'}
  $production.Modules.Add($row)
  $wire=$serializer.Invoke($null,[object[]]@($production))
  $publisher=$originalPublisher|ConvertFrom-Json
  ($publisher.colony.vessels|Where-Object vesselId -eq $atlas).production=$wire|ConvertFrom-Json
  $publisherText=$publisher|ConvertTo-Json -Depth 80 -Compress
  $bytes=[Text.Encoding]::UTF8.GetBytes($publisherText)
  $diagnostics=[Collections.Generic.List[string]]::new();$sink=[Action[string]]{param($text)$diagnostics.Add($text)}
  $decoded=[Expanse.Clock.Core.ClockProtocol]::DecodeClockSample([ReadOnlyMemory[byte]]::new($bytes),$sink)
  $decodedAtlas=$decoded.Colony.Vessels|Where-Object VesselId -eq $atlas
  $reject=$case -like 'reject-*' -or $case -like 'model-*'
  if($reject){if($diagnostics.Count -eq 0 -or $null -ne $decodedAtlas.Production){throw "Malformed production not isolated: $case"}}
  else{if($diagnostics.Count -ne 0 -or $decodedAtlas.Production.Modules.Length -ne 1){throw "Valid production rejected: $case"}}
  if(!$reject -and $decoded.Colony.Vessels.Length -ne 18){throw 'Valid physical frame was lost.'}
  $view=$originalView|ConvertFrom-Json
  ($view.colony.vessels|Where-Object vesselId -eq $atlas).production=$wire|ConvertFrom-Json
  $viewText=$view|ConvertTo-Json -Depth 80 -Compress
  $viewObject=[Text.Json.JsonSerializer]::Deserialize($viewText,[Expanse.Clock.Core.ClockView],[Expanse.Clock.Core.ClockProtocol]::JsonOptions)
  $viewRejected=$false;$encodedBytes=$null
  try {[Expanse.Clock.Core.ClockProtocol]::Validate($viewObject);$encoded=[Expanse.Clock.Core.ClockProtocol]::EncodeView($viewObject);$encodedBytes=$encoded.Length-4}
  catch {if(!$reject){throw};$viewRejected=$true}
  if($viewRejected -ne $reject){throw "View validation mismatch: $case"}
  if(!$reject){
   $finalText=[Text.Encoding]::UTF8.GetString($encoded,4,$encoded.Length-4)
   $final=$finalText|ConvertFrom-Json;$finalRow=($final.colony.vessels|Where-Object vesselId -eq $atlas).production.modules[0]
   if($finalRow.basis -ne $row.Basis){throw 'Provenance lost.'}
   if($case -like 'model-*' -and ($finalRow.background.outputs[0].unitsPerSecond -ne $value -or $null -ne $finalRow.achieved -or $null -ne $finalRow.prepared)){throw 'Modeled rates conflated.'}
   if($case -eq 'unknown' -and ($null -ne $finalRow.background -or $null -ne $finalRow.achieved)){throw 'Unknown fabricated.'}
   if($case -like 'actual-*' -and ($finalRow.achieved.outputs[0].unitsPerSecond -ne $value -or $null -ne $finalRow.background)){throw 'Actual rate lost.'}
  }
  $results+=[pscustomobject]@{case=$case;expectedRejected=$reject;publisherDiagnostics=$diagnostics.Count;colonyStatus=$decoded.Colony.Status;physicalVesselsRetained=$decoded.Colony.Vessels.Length;clockRetained=($decoded.Sequence -eq $publisher.sequence);viewRejected=$viewRejected;encodedBodyBytes=$encodedBytes;pass=$true}
 }
 [pscustomobject]@{scope='SYNTHETIC current mode-epoch candidate Bridge serializer -> reconstructed complete publisher -> reverted Core/Host decoder and Clock.View. Native positive/zero/unknown accepted; loaded Background rejected. No live runtime used.';sourcePublisher='rotation-unpruned-publisher.json';sourceView='rotation-unpruned-view.json';bridgeSha256=(Get-FileHash $bridge.Location).Hash;coreSha256=(Get-FileHash $core.Location).Hash;cases=$results}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $PSScriptRoot 'native-packed-primary-wire-result.json')
 Get-Content (Join-Path $PSScriptRoot 'native-packed-primary-wire-result.json')
} finally {[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}

