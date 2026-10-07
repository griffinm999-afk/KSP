param([string]$SourceRoot)
$ErrorActionPreference='Stop'
$stage=if($SourceRoot){$SourceRoot}else{Join-Path $PSScriptRoot 'performance-work'}
[Reflection.Assembly]::LoadFrom((Join-Path $stage 'src\Expanse.Clock.Core\bin\Release\net8.0\Expanse.Clock.Core.dll'))|Out-Null
if([Expanse.Clock.Core.ClockProtocol]::MaxFrameBytes -ne 262144){throw 'Clock cap mismatch'}
if([Expanse.Clock.Core.EffectsProtocol]::MaxFrameBytes -ne 65536){throw 'Effects cap changed'}
$text='x'*(262144-2)
$encoded=[Expanse.Clock.Core.ClockProtocol]::Encode([string]$text)
if($encoded.Length -ne 262148){throw 'Exact-cap encoding mismatch'}
$rejected=$false;try{$null=[Expanse.Clock.Core.ClockProtocol]::Encode([string]($text+'x'))}catch{$rejected=$true};if(!$rejected){throw 'Over-cap Core encoding accepted'}
$input=[IO.MemoryStream]::new($encoded)
$read=[Expanse.Clock.Core.ClockProtocol]::ReadFrameAsync($input,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
if($read.Length -ne 262144){throw 'Exact-cap Core read mismatch'}
$header=[BitConverter]::GetBytes([uint32]262145);$input=[IO.MemoryStream]::new($header);$rejected=$false;try{$null=[Expanse.Clock.Core.ClockProtocol]::ReadFrameAsync($input,[Threading.CancellationToken]::None).GetAwaiter().GetResult()}catch{$rejected=$true};if(!$rejected){throw 'Over-cap Core read accepted'}
$dirs=@((Join-Path $stage 'src\Expanse.WorldBridge\bin\Release\net472'),(Join-Path $stage 'src\Expanse.Domain\bin\Release\net472'),'C:\Kerbal Space Program\KSP_x64_Data\Managed','C:\Kerbal Space Program\GameData\000_Harmony','C:\Kerbal Space Program\GameData\000_USITools','C:\Kerbal Space Program\GameData\UmbraSpaceIndustries\WOLF')
$resolver=[ResolveEventHandler]{param($s,$e)$n=[Reflection.AssemblyName]::new($e.Name).Name;foreach($d in $dirs){$p=Join-Path $d ($n+'.dll');if(Test-Path -LiteralPath $p){return [Reflection.Assembly]::LoadFrom($p)}};return $null};[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try{$bridge=[Reflection.Assembly]::LoadFrom((Join-Path $dirs[0] 'Expanse.WorldBridge.dll'));$addon=$bridge.GetType('Expanse.WorldBridge.WorldBridgeAddon',$true);$write=$addon.GetMethod('WriteFrame',[Reflection.BindingFlags]'Static,NonPublic');foreach($size in @(262144,262145)){$stream=[IO.MemoryStream]::new();$body='"'+('x'*($size-2))+'"';$write.Invoke($null,[object[]]@($stream,[string]$body));if($size -eq 262144 -and $stream.Length -ne 262148){throw 'Native exact-cap write mismatch'};if($size -eq 262145 -and $stream.Length -ne 0){throw 'Native oversized clock write accepted'}}}finally{[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
'Clock Bridge/Core: 262144 accepted, 262145 rejected; Effects cap 65536 unchanged. PASS'
