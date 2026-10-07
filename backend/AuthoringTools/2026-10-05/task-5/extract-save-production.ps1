$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$dirs=@('C:\Kerbal Space Program\GameData\ExpanseWorldBridge\Plugins','C:\Kerbal Space Program\KSP_x64_Data\Managed','C:\Kerbal Space Program\GameData\000_Harmony','C:\Kerbal Space Program\GameData\000_USITools','C:\Kerbal Space Program\GameData\UmbraSpaceIndustries\WOLF')
$resolver=[ResolveEventHandler]{param($s,$e) $n=[Reflection.AssemblyName]::new($e.Name).Name;foreach($d in $dirs){$p=Join-Path $d ($n+'.dll');if(Test-Path -LiteralPath $p){return [Reflection.Assembly]::LoadFrom($p)}};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try{
 $bridge=[Reflection.Assembly]::LoadFrom((Join-Path $dirs[0] 'Expanse.WorldBridge.dll'))
 $stock=[Reflection.Assembly]::LoadFrom((Join-Path $dirs[1] 'Assembly-CSharp.dll'))
 $ct=$stock.GetType('ConfigNode',$true);$load=$ct.GetMethod('Load',[Type[]]@([string]))
 $cache=$load.Invoke($null,[object[]]@('C:\Kerbal Space Program\GameData\ModuleManager.ConfigCache'))
 $configs=@{};foreach($u in $cache.GetNodes('UrlConfig')){$partConfig=$u.GetNode('PART');if($partConfig){$configs[$partConfig.GetValue('name')]=$partConfig}}
 $save=$load.Invoke($null,[object[]]@('C:\Kerbal Space Program\saves\The Expanse\persistent.sfs')).GetNode('GAME')
 $frame=Get-Content -LiteralPath (Join-Path $root 'rotation-full-frame.json') -Raw|ConvertFrom-Json
 $wanted=@{};foreach($v in $frame.colony.vessels){$wanted[$v.vesselId.Replace('-','')]=$v}
 $addon=$bridge.GetType('Expanse.WorldBridge.WorldBridgeAddon',$true)
 $proto=$addon.GetMethod('ProtoProductionRow',[Reflection.BindingFlags]'Static,NonPublic')
 $serialize=$addon.GetMethod('ProductionTelemetryJson',[Reflection.BindingFlags]'Static,NonPublic')
 $result=@();$supported=@('USI_Converter','USI_Harvester','ModuleResourceConverter','ModuleResourceHarvester','WOLF_HopperModule','ModuleScienceConverter')
 foreach($vessel in $save.GetNode('FLIGHTSTATE').GetNodes('VESSEL')){
  $id=$vessel.GetValue('pid');if(!$wanted.ContainsKey($id)){continue}
  $allModuleNames=[Collections.Generic.HashSet[string]]::new()
  $production=[Activator]::CreateInstance($bridge.GetType('Expanse.WorldBridge.ColonyProductionTelemetry',$true))
  $production.ObservedUt=[double]$frame.colony.observedUt
  foreach($partNode in $vessel.GetNodes('PART')){
   $partName=$partNode.GetValue('name');if(!$partName){$partName=($partNode.GetValue('part') -replace '_[0-9]+$','')}
   $config=$configs[$partName];if(!$config){$config=$configs[$partName.Replace('.','_')]};if(!$config){continue};$nodes=$config.GetNodes('MODULE');$savedModules=$partNode.GetNodes('MODULE')
   $available=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($stock.GetType('AvailablePart',$true));$available.partConfig=$config;$available.title=$config.GetValue('title')
   $part=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($stock.GetType('ProtoPartSnapshot',$true));$part.partInfo=$available;$part.persistentId=[uint32]$partNode.GetValue('persistentId')
   $lt=[Collections.Generic.List``1].MakeGenericType($stock.GetType('ProtoPartModuleSnapshot',$true));$part.modules=[Activator]::CreateInstance($lt)
   foreach($sn in $savedModules){$sm=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($stock.GetType('ProtoPartModuleSnapshot',$true));$sm.moduleName=$sn.GetValue('name');$null=$allModuleNames.Add($sm.moduleName);$sm.moduleValues=$sn;$part.modules.Add($sm)}
   for($i=0;$i -lt $savedModules.Length;$i++){
    if($savedModules[$i].GetValue('name') -notin $supported){continue}
    if($i -ge $nodes.Length -or $nodes[$i].GetValue('name') -ne $savedModules[$i].GetValue('name')){throw "Config/save module alignment differs for $partName index $i"}
    $row=$proto.Invoke($null,[object[]]@($part,$part.modules[$i],$i,$nodes))
    if($part.modules[$i].moduleName -eq 'ModuleScienceConverter'){
     if(!$stock.GetType('BaseConverter',$true).IsAssignableFrom($stock.GetType('ModuleScienceConverter',$true))){throw 'Science converter hierarchy differs'}
     $row.Configured=[Activator]::CreateInstance($bridge.GetType('Expanse.WorldBridge.ColonyProductionVector',$true))
     $row.Reason='Unsupported converter implementation or mass-based configured recipe; no achieved rate claimed.'
    }
    $production.Modules.Add($row)
   }
  }
  $decoded=$serialize.Invoke($null,[object[]]@($production))|ConvertFrom-Json
  $result += [pscustomobject]@{vesselId=$wanted[$id].vesselId;name=$wanted[$id].name;production=$decoded;moduleNames=@($allModuleNames)}
 }
 $result|ConvertTo-Json -Depth 50|Set-Content -LiteralPath (Join-Path $root 'rotation-save-config-modules.json') -Encoding utf8
 $result|Where-Object {$_.vesselId -in @('602524b9-f5c6-46b5-bd7e-879357299f5e','a89d0ac9-cf1b-4d96-ab72-8643a2a22bde','ff54e526-3a87-46de-921a-6f6e292da861')}|ForEach-Object {[pscustomobject]@{name=$_.name;rows=$_.production.modules.Count;moduleNames=$_.moduleNames}}|ConvertTo-Json -Depth 5
}finally{[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
