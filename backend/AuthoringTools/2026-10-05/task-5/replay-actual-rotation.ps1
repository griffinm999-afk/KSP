param([switch]$OldAllocator,[switch]$MeasureOnly)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot;$stage=Join-Path $root 'performance-work'
$bridgeDir=if($OldAllocator){'C:\Kerbal Space Program\GameData\ExpanseWorldBridge\Plugins'}else{Join-Path $stage 'src\Expanse.WorldBridge\bin\Release\net472'}
$coreDir=if($OldAllocator){'C:\Users\griff\AppData\Local\Programs\ExpanseFoundations\versions\20261006-180649\Host'}else{Join-Path $stage 'src\Expanse.Clock.Core\bin\Release\net8.0'}
$dirs=@($bridgeDir,(Join-Path $stage 'src\Expanse.Domain\bin\Release\net472'),'C:\Kerbal Space Program\KSP_x64_Data\Managed','C:\Kerbal Space Program\GameData\000_Harmony','C:\Kerbal Space Program\GameData\000_USITools','C:\Kerbal Space Program\GameData\UmbraSpaceIndustries\WOLF')
$resolver=[ResolveEventHandler]{param($s,$e)$n=[Reflection.AssemblyName]::new($e.Name).Name;foreach($d in $dirs){$p=Join-Path $d ($n+'.dll');if(Test-Path -LiteralPath $p){return [Reflection.Assembly]::LoadFrom($p)}};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try{
 $bridge=[Reflection.Assembly]::LoadFrom((Join-Path $bridgeDir 'Expanse.WorldBridge.dll'))
 [Reflection.Assembly]::LoadFrom((Join-Path $coreDir 'Expanse.Clock.Core.dll'))|Out-Null
 function Native([Type]$type,$value){
  if($null -eq $value){return $null}
  if([Nullable]::GetUnderlyingType($type)){return Native ([Nullable]::GetUnderlyingType($type)) $value}
  if($type.IsArray){$arr=[Array]::CreateInstance($type.GetElementType(),@($value).Count);for($j=0;$j -lt @($value).Count;$j++){$arr.SetValue((Native $type.GetElementType() @($value)[$j]),$j)};return ,$arr}
  if($type.IsGenericType -and $type.GetGenericTypeDefinition() -eq [Collections.Generic.List``1]){$list=[Activator]::CreateInstance($type);foreach($x in $value){$list.Add((Native $type.GetGenericArguments()[0] $x))};return ,$list}
  if($type.IsPrimitive -or $type -eq [string]){return [Convert]::ChangeType($value,$type,[Globalization.CultureInfo]::InvariantCulture)}
  $obj=[Activator]::CreateInstance($type)
  foreach($field in $type.GetFields()){$property=$value.PSObject.Properties[$field.Name];if(!$property -or $null -eq $property.Value){continue};$converted=Native $field.FieldType $property.Value;if($field.IsInitOnly){foreach($item in $converted){$field.GetValue($obj).Add($item)}}else{$field.SetValue($obj,$converted)}}
  return ,$obj
 }
 $frame=Get-Content -LiteralPath (Join-Path $root 'rotation-full-frame.json') -Raw|ConvertFrom-Json
 $saveRows=Get-Content -LiteralPath (Join-Path $root 'rotation-save-config-modules.json') -Raw|ConvertFrom-Json
 $originalFrames=Get-Content -LiteralPath (Join-Path $root 'live-installed-production-rotation.json') -Raw|ConvertFrom-Json
 if(@($originalFrames|Where-Object {@($_.targets|Where-Object {$_.name -eq 'Atlas Harvester 1' -and $_.production.modules.Count -gt 0}).Count}).Count){throw 'Original failure evidence unexpectedly contains Atlas rows'}
 $source=$frame.colony|ConvertTo-Json -Depth 100|ConvertFrom-Json
 $expected=@{};$targetCounts=@{'602524b9-f5c6-46b5-bd7e-879357299f5e'=6;'a89d0ac9-cf1b-4d96-ab72-8643a2a22bde'=6;'ff54e526-3a87-46de-921a-6f6e292da861'=3}
 foreach($v in $source.vessels){
  $saved=$saveRows|Where-Object vesselId -eq $v.vesselId
  if(!$v.production){continue};$rows=@($saved.production.modules)
  $observed=@{};foreach($m in $v.production.modules){$observed["$($m.partId):$($m.moduleIndex)"]=$m}
  $rows=@(foreach($m in $rows){$key="$($m.partId):$($m.moduleIndex)";if($observed.ContainsKey($key)){$observed[$key]}else{$m}})|Select-Object -First 32
  if($targetCounts.ContainsKey($v.vesselId) -and $rows.Count -ne $targetCounts[$v.vesselId]){throw "Actual target inventory differs: $($v.name) $($rows.Count)"}
  $v.production.modules=$rows;$v.production.budgetOmittedModuleCount=0;$v.production.budgetSelectionSequence=$null;$v.production.status='partial';$v.production.reason='Offline replay: saved configuration plus exact observed callbacks; missing callbacks remain unknown.';$v.production.inventoryStatus='partial'
  foreach($m in $rows){$expected["$($v.vesselId):$($m.partId):$($m.moduleIndex)"]=($m|ConvertTo-Json -Depth 50 -Compress)}
 }
 $native=Native ($bridge.GetType('Expanse.WorldBridge.ColonySnapshot',$true)) $source
 $addon=$bridge.GetType('Expanse.WorldBridge.WorldBridgeAddon',$true);$fit=$addon.GetMethod('FitProductionColony',[Reflection.BindingFlags]'Static,NonPublic')
 $wolf=$frame.colony.wolf|ConvertTo-Json -Depth 50 -Compress
 $sampleJson=$frame.sample|ConvertTo-Json -Depth 50 -Compress
 $prefix=$sampleJson.Substring(0,$sampleJson.Length-1)+',"colony":'
 $colonyJsonMethod=$addon.GetMethod('ColonyJson',[Reflection.BindingFlags]'Static,NonPublic')
 $unprunedPublisher=$prefix+$colonyJsonMethod.Invoke($null,[object[]]@($native,[string]$wolf,$true,$false))+'}'
 $unprunedView=$frame|ConvertTo-Json -Depth 100|ConvertFrom-Json
 $unprunedView.colony=$unprunedPublisher|ConvertFrom-Json|Select-Object -ExpandProperty colony
 $unprunedViewJson=$unprunedView|ConvertTo-Json -Depth 100 -Compress
 [IO.File]::WriteAllText((Join-Path $root 'rotation-unpruned-publisher.json'),[string]$unprunedPublisher,[Text.UTF8Encoding]::new($false))
 [IO.File]::WriteAllText((Join-Path $root 'rotation-unpruned-view.json'),[string]$unprunedViewJson,[Text.UTF8Encoding]::new($false))
 $nativeTimes=@();$fitTimes=@();for($b=0;$b -lt 25;$b++){$watch=[Diagnostics.Stopwatch]::StartNew();$null=$colonyJsonMethod.Invoke($null,[object[]]@($native,[string]$wolf,$true,$false));$watch.Stop();if($b -gt 4){$nativeTimes+=$watch.Elapsed.TotalMilliseconds};$watch=[Diagnostics.Stopwatch]::StartNew();$null=$fit.Invoke($null,[object[]]@([string]$prefix,$native,[string]$wolf,$true));$watch.Stop();if($b -gt 4){$fitTimes+=$watch.Elapsed.TotalMilliseconds}}
 $typed=[Text.Json.JsonSerializer]::Deserialize([string]$unprunedViewJson,[Expanse.Clock.Core.ClockView],[Expanse.Clock.Core.ClockProtocol]::JsonOptions)
 $hostTimes=@();for($b=0;$b -lt 105;$b++){$watch=[Diagnostics.Stopwatch]::StartNew();$bytes=[Text.Json.JsonSerializer]::SerializeToUtf8Bytes($typed,[Expanse.Clock.Core.ClockProtocol]::JsonOptions);$watch.Stop();if($b -gt 4){$hostTimes+=$watch.Elapsed.TotalMilliseconds}}
 [IO.File]::WriteAllBytes((Join-Path $root 'rotation-unpruned-view.json'),$bytes)
 [pscustomobject]@{unprunedPublisherBytes=[Text.Encoding]::UTF8.GetByteCount($unprunedPublisher);unprunedViewBytes=$bytes.Length;reconstructedModules=$expected.Count;capturedModules=@($frame.colony.vessels|ForEach-Object {$_.production.modules.Count}|Measure-Object -Sum).Sum;capturedInventoryModules=@($frame.colony.vessels|ForEach-Object {$_.production.modules.Count+$_.production.budgetOmittedModuleCount}|Measure-Object -Sum).Sum;nativeFullJsonMeanMs=($nativeTimes|Measure-Object -Average).Average;native64KiBFitMeanMs=($fitTimes|Measure-Object -Average).Average;hostFullJsonMeanMs=($hostTimes|Measure-Object -Average).Average;environment='Detached PowerShell/.NET runtime benchmark, not Unity Mono or live network'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'unpruned-payload-measurement.json')
 if($MeasureOnly){Get-Content -LiteralPath (Join-Path $root 'unpruned-payload-measurement.json') -Raw;return}
 $seen=[Collections.Generic.HashSet[string]]::new();$trace=@();$maxNative=0;$maxHost=0;$initialSelection=[long]$source.vesselCensus.observationSequence
 $bound=$source.vessels.Count*6
 for($i=0;$i -lt $bound;$i++){
  $turn=$initialSelection+$i;$native.VesselCensus.ObservationSequence=$turn
  $publisherJson=$fit.Invoke($null,[object[]]@([string]$prefix,$native,[string]$wolf,$true));if(!$publisherJson){throw 'Native base cannot fit'}
  $publisherBytes=[Text.Encoding]::UTF8.GetByteCount($publisherJson);$maxNative=[Math]::Max($maxNative,$publisherBytes);if($publisherBytes -gt [Expanse.Clock.Core.ClockProtocol]::MaxFrameBytes){throw 'Publisher overflow'}
  $publisher=$publisherJson|ConvertFrom-Json
  $viewFrame=$frame|ConvertTo-Json -Depth 100|ConvertFrom-Json
  $viewFrame.colony=$publisher.colony
  $view=[Text.Json.JsonSerializer]::Deserialize(($viewFrame|ConvertTo-Json -Depth 100 -Compress),[Expanse.Clock.Core.ClockView],[Expanse.Clock.Core.ClockProtocol]::JsonOptions)
  try{[Expanse.Clock.Core.ClockProtocol]::Validate($view)}catch{
   foreach($v in $view.Colony.Vessels){foreach($m in $v.Production.Modules){$one=[Expanse.Clock.Core.ColonyProductionTelemetry]::new('partial','Detached row validation',$v.Production.ObservedUt,[Expanse.Clock.Core.ColonyProductionModuleTelemetry[]]@($m),'partial',0,$null);try{[Expanse.Clock.Core.ColonyProductionTelemetryProtocol]::Validate($one,$v.ObservationBasis,$view.Colony.ObservedUt)}catch{throw "Invalid detached row $($v.Name) $($m.PartId):$($m.ModuleIndex) type=$($m.ModuleType) recipe=$($m.Recipe)"}}};throw
  }
  $wire=[Expanse.Clock.Core.ClockProtocol]::EncodeView($view);$hostBytes=$wire.Length-4;$maxHost=[Math]::Max($maxHost,$hostBytes)
  $decoded=[Text.Encoding]::UTF8.GetString($wire,4,$hostBytes)|ConvertFrom-Json
  if(($decoded.colony.vesselCensus.vesselIds|ConvertTo-Json -Compress) -ne ($frame.colony.vesselCensus.vesselIds|ConvertTo-Json -Compress)){throw 'Census changed'}
  $stageCounts=@()
  foreach($v in $decoded.colony.vessels){
   $original=$frame.colony.vessels|Where-Object vesselId -eq $v.vesselId
   if(($v.crewRoster|ConvertTo-Json -Compress) -ne ($original.crewRoster|ConvertTo-Json -Compress) -or $v.crewRosterComplete -ne $original.crewRosterComplete){throw 'Roster changed'}
   foreach($m in $v.production.modules){$key="$($v.vesselId):$($m.partId):$($m.moduleIndex)";$null=$seen.Add($key);$actualRow=$m|ConvertTo-Json -Depth 50 -Compress;if(![Text.Json.Nodes.JsonNode]::DeepEquals([Text.Json.Nodes.JsonNode]::Parse([string]$actualRow),[Text.Json.Nodes.JsonNode]::Parse([string]$expected[$key]))){throw "Selected whole row changed: $key"}}
   if($targetCounts.ContainsKey($v.vesselId)){$pub=$publisher.colony.vessels|Where-Object vesselId -eq $v.vesselId;$stageCounts += [pscustomobject]@{vesselId=$v.vesselId;publisherRows=@($pub.production.modules|ForEach-Object {"$($_.partId):$($_.moduleIndex)"});publisherOmitted=$pub.production.budgetOmittedModuleCount;publisherSelection=$pub.production.budgetSelectionSequence;hostRows=@($v.production.modules|ForEach-Object {"$($_.partId):$($_.moduleIndex)"});hostOmitted=$v.production.budgetOmittedModuleCount;hostSelection=$v.production.budgetSelectionSequence}}
  }
  $trace += [pscustomobject]@{turn=$turn;publisherBytes=$publisherBytes;hostBytes=$hostBytes;targets=$stageCounts}
 }
 $coverage=@(foreach($id in $targetCounts.Keys){[pscustomobject]@{vesselId=$id;expected=$targetCounts[$id];covered=@($seen|Where-Object {$_.StartsWith($id+':')}).Count}})
 $result=[ordered]@{allocator=if($OldAllocator){'installed-90cf-old'}else{'staged-fix'};originalFailureFrames=$originalFrames.Count;fullCapturedWrapperBytes=(Get-Item (Join-Path $root 'rotation-full-frame.json')).Length;sourceModuleCount=$expected.Count;boundedCycleCaptures=$bound;publisherMaximumBytes=$maxNative;hostMaximumBytes=$maxHost;coverage=$coverage;censusAndRostersPreserved=$true;allSelectedWholeRowsUnchanged=$true;rateEvidence='Only captured callbacks are measured; all missing observations stay unknown. Saved configs are explicitly proto-config.';trace=$trace}
 $name=if($OldAllocator){'rotation-replay-old.json'}else{'rotation-replay-fixed.json'};$result|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $root $name) -Encoding utf8
 $result.Remove('trace');$result|ConvertTo-Json -Depth 8
 if(!$OldAllocator -and @($coverage|Where-Object {$_.covered -ne $_.expected}).Count){throw 'Fixed replay did not cover every target module'}
}finally{[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
