$ErrorActionPreference='Stop'
$stage=Join-Path $PSScriptRoot 'usils-work'
$dirs=@((Join-Path $stage 'src\Expanse.WorldBridge\bin\Release\net472'),(Join-Path $stage 'src\Expanse.Domain\bin\Release\net472'),'C:\Kerbal Space Program\KSP_x64_Data\Managed','C:\Kerbal Space Program\GameData\000_Harmony','C:\Kerbal Space Program\GameData\000_USITools','C:\Kerbal Space Program\GameData\UmbraSpaceIndustries\WOLF')
$resolver=[ResolveEventHandler]{param($sender,$eventArgs)$name=[Reflection.AssemblyName]::new($eventArgs.Name).Name;foreach($d in $dirs){$p=Join-Path $d ($name+'.dll');if([IO.File]::Exists($p)){return [Reflection.Assembly]::LoadFrom($p)}}return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
 $bridge=[Reflection.Assembly]::LoadFrom((Join-Path $dirs[0] 'Expanse.WorldBridge.dll'))
 $core=[Reflection.Assembly]::LoadFrom((Join-Path $stage 'src\Expanse.Clock.Core\bin\Release\net8.0\Expanse.Clock.Core.dll'))
 function New-Dto($name,$values){$x=[Activator]::CreateInstance($bridge.GetType('Expanse.WorldBridge.'+$name,$true));foreach($e in $values.GetEnumerator()){$x.GetType().GetField($e.Key).SetValue($x,$e.Value)}return $x}
 $jsonMethod=$bridge.GetType('Expanse.WorldBridge.WorldBridgeAddon').GetMethod('ColonyJson',[Reflection.BindingFlags]'Static,NonPublic')
 $cases=@()
 foreach($case in @('accepted','supply-zero','catch-up','missing-ec','missing-supply','unavailable','reject-stale','reject-crew','reject-basis','reject-average-age')){
  $supply=New-Dto 'LifeSupportSupplyStream' @{SampleUt=[double]100;ProcessedEndUt=[double]100;IntervalGameSeconds=[double]2;CaptureSequence=[long]1;CrewCount=[int]5;TimeFactor=[double]2;RecyclerMultiplier=[double].5;GrossSuppliesPerSecond=[double].0025;GrossMulchPerSecond=[double].0025;ConfiguredSuppliesPerSecond=[double].00125;ConfiguredMulchPerSecond=[double].00125;SuppliesConsumed=[double].002;MulchProduced=[double].001}
  $ec=New-Dto 'LifeSupportEcStream' @{SampleUt=[double]100;ProcessedEndUt=[double]100;IntervalGameSeconds=[double]2;CaptureSequence=[long]2;CrewCount=[int]5;TimeFactor=[double]2;ConfiguredEcPerSecond=[double].05;ElectricityConsumed=[double].08}
  $ls=New-Dto 'ColonyLifeSupportTelemetry' @{Status='partial';Reason='SYNTHETIC DETACHED';ObservedUt=[double]100;Supply=$supply;CrewElectricity=$ec}
  $average=New-Dto 'PowerAverageTelemetry' @{WindowId=[long]1;WindowStartUt=[double]30;WindowEndUt=[double]90;WindowGameSeconds=[double]60;CoveredGameSeconds=[double]60;ReportRealSeconds=[double]60;AgeRealSeconds=[double]10;GenerationEc=[double]600;ConsumptionEc=[double]300;GenerationEcPerSecond=[double]10;ConsumptionEcPerSecond=[double]5;Status='observed';Basis='fulfilled-part-requests';Reason='SYNTHETIC DETACHED'}
  if($case -eq 'supply-zero'){$supply.SuppliesConsumed=0;$supply.MulchProduced=0;$supply.TimeFactor=0}
  if($case -eq 'catch-up'){$supply.ProcessedEndUt=50;$ec.ProcessedEndUt=50}
  if($case -eq 'missing-ec'){$ls.CrewElectricity=$null}
  if($case -eq 'missing-supply'){$ls.Supply=$null}
  if($case -eq 'unavailable'){$ls.Supply=$null;$ls.CrewElectricity=$null;$ls.Status='unavailable'}
  if($case -eq 'reject-stale'){$supply.SampleUt=89;$supply.ProcessedEndUt=89}
  if($case -eq 'reject-crew'){$supply.CrewCount=4}
  if($case -eq 'reject-average-age'){$average.AgeRealSeconds=121}
  $vessel=New-Dto 'ColonyVessel' @{VesselId='e79c13cb-6a7f-4053-925c-afd903382380';Name='Synthetic habitat';Body='Minmus';Biome='Flats';Latitude=[double]1;Longitude=[double]2;ObservationBasis='loaded';Crew=[int]5;LifeSupport=$ls;PowerAverage=$average}
  if($case -eq 'reject-basis'){$vessel.ObservationBasis='snapshot'}
  $colony=New-Dto 'ColonySnapshot' @{Status='observed';ObservedUt=[double]100};$colony.Vessels.Add($vessel)
  $wire=$jsonMethod.Invoke($null,[object[]]@($colony,$null,$true,$false))
  $sample=@{protocolVersion=1;messageType='clockSample';sequence=1;sessionId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';loadEpoch='bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';installNamespace='C:\SyntheticKSP';saveFolder='Fixture';saveTitle='Fixture';utSeconds=100;activeWorld=$true;scene='Flight';paused=$false;formattedDate=$null;warpRate=$null;colony=($wire|ConvertFrom-Json)}|ConvertTo-Json -Depth 50 -Compress
  $decoded=[Expanse.Clock.Core.ClockProtocol]::DecodeClockSample([ReadOnlyMemory[byte]]::new([Text.Encoding]::UTF8.GetBytes($sample)))
  $reject=$case -like 'reject-*';if(($decoded.Colony.Status -eq 'unavailable') -ne $reject){throw ('Wire case failed: '+$case)}
  if(!$reject){
   $viewJson=@{protocolVersion=1;messageType='clockView';status='live';sample=($sample|ConvertFrom-Json);ageSeconds=.1;publisherConnected=$true;colony=($wire|ConvertFrom-Json)}|ConvertTo-Json -Depth 50 -Compress
   $view=[Text.Json.JsonSerializer]::Deserialize($viewJson,[Expanse.Clock.Core.ClockView],[Expanse.Clock.Core.ClockProtocol]::JsonOptions);[Expanse.Clock.Core.ClockProtocol]::Validate($view)
   $frame=[Expanse.Clock.Core.ClockProtocol]::EncodeView($view);if($frame.Length -gt 262148){throw 'Frame limit changed'}
  }
  $cases+=[pscustomobject]@{case=$case;expectedRejected=$reject;pass=$true}
 }
 [pscustomobject]@{scope='Synthetic DTOs through actual staged Bridge ColonyJson, Core sample decoder, Core full view validation and framing; no private game fixture or runtime attachment';bridgeSha256=(Get-FileHash $bridge.Location).Hash;coreSha256=(Get-FileHash $core.Location).Hash;cases=$cases}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $PSScriptRoot 'usils-wire-result.json')
 Get-Content (Join-Path $PSScriptRoot 'usils-wire-result.json')
}finally{[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
