$ErrorActionPreference='Stop'
$stage=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$directories=@((Join-Path $stage 'src\Expanse.WorldBridge\bin\Release\net472'),(Join-Path $stage 'src\Expanse.Domain\bin\Release\net472'),'C:\Kerbal Space Program\KSP_x64_Data\Managed','C:\Kerbal Space Program\GameData\000_Harmony','C:\Kerbal Space Program\GameData\000_USITools','C:\Kerbal Space Program\GameData\UmbraSpaceIndustries\WOLF')
$resolver=[ResolveEventHandler]{param($sender,$eventArgs) $assemblyName=[Reflection.AssemblyName]::new($eventArgs.Name).Name;foreach($directory in $directories){$candidate=Join-Path $directory ($assemblyName+'.dll');if([IO.File]::Exists($candidate)){return [Reflection.Assembly]::LoadFrom($candidate)}}return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
  $bridge=[Reflection.Assembly]::LoadFrom((Join-Path $directories[0] 'Expanse.WorldBridge.dll'))
  function New-Telemetry($name,$values){$instance=[Activator]::CreateInstance($bridge.GetType('Expanse.WorldBridge.'+$name,$true));foreach($entry in $values.GetEnumerator()){$instance.GetType().GetField($entry.Key).SetValue($instance,$entry.Value)}return $instance}
  function Typed-Array($name,$values){$array=[Array]::CreateInstance($bridge.GetType('Expanse.WorldBridge.'+$name,$true),$values.Count);for($i=0;$i -lt $values.Count;$i++){$array.SetValue($values[$i],$i)}return ,$array}
  $rate=New-Telemetry 'ColonyProductionRateTelemetry' @{Resource='Water';UnitsPerSecond=[double]0.1157408;FlowMode='ALL_VESSEL';DumpExcess=$false}
  $zero=New-Telemetry 'ColonyProductionRateTelemetry' @{Resource='Water';UnitsPerSecond=[double]0;FlowMode='ALL_VESSEL';DumpExcess=$false}
  $empty=Typed-Array 'ColonyProductionRateTelemetry' @()
  $vector=New-Telemetry 'ColonyProductionVector' @{Inputs=$empty;Outputs=(Typed-Array 'ColonyProductionRateTelemetry' @($rate))}
  $prepared=New-Telemetry 'ColonyProductionPotential' @{SampleUt=[double]100;EfficiencyMultiplier=[double]1;RequirementMultiplier=[double]1;Rates=$vector}
  $achieved=New-Telemetry 'ColonyProductionAchieved' @{SampleUt=[double]100;IntervalGameSeconds=[double]1;TimeFactor=[double]0;CaptureSequence=[long]1;Inputs=$empty;Outputs=(Typed-Array 'ColonyProductionRateTelemetry' @($zero))}
  $row=New-Telemetry 'ColonyProductionModuleTelemetry' @{PartId=[uint32]1623853143;ModuleId=[uint32]995808686;ModuleIndex=[int]5;ModuleType='WOLF.WOLF_HopperModule';PartName='Harvesting Hopper';Recipe='Water';RecipeHash=('a'*64);Basis='loaded-broker';Enabled=$true;Activated=$true;Configured=$vector;Prepared=$prepared;Achieved=$achieved;Reason='Synthetic serializer fixture, observed zero storage acceptance.'}
  $production=New-Telemetry 'ColonyProductionTelemetry' @{Status='partial';Reason='Synthetic fixture, not game telemetry.';ObservedUt=[double]100}
  $production.Modules.Add($row)
  $addon=$bridge.GetType('Expanse.WorldBridge.WorldBridgeAddon',$true)
  $serializer=$addon.GetMethod('ProductionTelemetryJson',[Reflection.BindingFlags]'Static,NonPublic')
  $json=$serializer.Invoke($null,[object[]]@($production));$decoded=$json | ConvertFrom-Json
  if($decoded.modules[0].configured.outputs[0].unitsPerSecond -ne 0.1157408 -or $decoded.modules[0].achieved.outputs[0].unitsPerSecond -ne 0 -or $decoded.modules[0].moduleId -ne 995808686){throw 'Native serializer lost rate or identity.'}
  $limiter=$addon.GetMethod('LimitProductionTelemetry',[Reflection.BindingFlags]'Static,NonPublic');$null=$limiter.Invoke($null,[object[]]@($production,[int]512))
  $limited=$serializer.Invoke($null,[object[]]@($production)) | ConvertFrom-Json
  if($limited.status -ne 'truncated' -or $limited.modules.Count -ne 0){throw 'Native serializer did not mark omitted rows.'}
  $stock=[Reflection.Assembly]::LoadFrom('C:\Kerbal Space Program\KSP_x64_Data\Managed\Assembly-CSharp.dll')
  $process=$stock.GetType('ResourceConverter').GetMethods() | Where-Object Name -eq 'ProcessRecipe'
  if($process.GetParameters().Count -ne 5 -or $process.ReturnType.Name -ne 'ConverterResults'){throw 'Installed native broker boundary changed.'}
  # Read installed configs into detached managed fixtures. Never instantiate a
  # game vessel or call PrepareRecipe/OnStart/Swap/resource APIs.
  $usi=[Reflection.Assembly]::LoadFrom('C:\Kerbal Space Program\GameData\000_USITools\USITools.dll')
  $native=$usi.GetType('USITools.USI_Harvester',$true)
  $support=$addon.GetMethod('SupportedProductionModule',[Reflection.BindingFlags]'Static,NonPublic')
  $detached=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($native)
  if(!$support.Invoke($null,[object[]]@($detached))){throw 'Installed USI harvester is not observed.'}
  $configType=$stock.GetType('ConfigNode',$true)
  $load=$configType.GetMethod('Load',[Type[]]@([string]))
  $proto=$addon.GetMethod('ProtoProductionRow',[Reflection.BindingFlags]'Static,NonPublic')
  $cases=0
  foreach($file in Get-ChildItem 'C:\Kerbal Space Program\GameData\UmbraSpaceIndustries\MKS\Parts\Atlas\ATLAS_Harvester*.cfg'){
    $config=$load.Invoke($null,[object[]]@($file.FullName)).GetNode('PART');$nodes=$config.GetNodes('MODULE')
    $available=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($stock.GetType('AvailablePart',$true));$available.partConfig=$config;$available.title=$file.Name
    $part=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($stock.GetType('ProtoPartSnapshot',$true));$part.partInfo=$available;$part.persistentId=123
    $listType=[Collections.Generic.List``1].MakeGenericType($stock.GetType('ProtoPartModuleSnapshot',$true));$part.modules=[Activator]::CreateInstance($listType)
    $harvestIndex=-1;$bayIndex=-1
    for($i=0;$i -lt $nodes.Length;$i++){$saved=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($stock.GetType('ProtoPartModuleSnapshot',$true));$saved.moduleName=$nodes[$i].GetValue('name');$saved.moduleValues=[Activator]::CreateInstance($configType);$saved.moduleValues.AddValue('isEnabled','True');$saved.moduleValues.AddValue('IsActivated','True');$part.modules.Add($saved);if($saved.moduleName -eq 'USI_Harvester'){$harvestIndex=$i};if($saved.moduleName -eq 'USI_SwappableBay'){$bayIndex=$i}}
    foreach($loadout in @(1,3)){
      $part.modules[$bayIndex].moduleValues.SetValue('currentLoadout',[string]$loadout,$true)
      $observed=$proto.Invoke($null,[object[]]@($part,$part.modules[$harvestIndex],$harvestIndex,$nodes))
      $expected=if($loadout -eq 1){'Gypsum'}else{'Substrate'}
      if($observed.Harvester.Resource -ne $expected -or $observed.SelectedLoadout -ne $loadout -or $observed.Configured.Outputs.Length -ne 0 -or $observed.Configured.Inputs[0].Resource -ne 'ElectricCharge' -or $null -ne $observed.Achieved){throw 'Installed Atlas recipe/bay qualification failed.'}
      $production.Modules.Clear();$production.Modules.Add($observed);$atlasJson=$serializer.Invoke($null,[object[]]@($production))|ConvertFrom-Json
      if($atlasJson.modules[0].harvester.resource -ne $expected -or $atlasJson.modules[0].harvester.efficiency -le 0){throw 'Native harvester metadata serialization failed.'};$cases++
    }
  }
  if($cases -ne 8){throw 'Expected both target recipes across four installed Atlas variants.'}
  "Installed Atlas variants: $cases detached recipe/bay/serializer cases PASS; no game calls."
  $json
  'Native serializer, truncation and installed callback signature: PASS (synthetic fixture; no game calls).'
} finally {[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
