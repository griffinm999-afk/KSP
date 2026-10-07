param([string]$DevRoot='C:\Users\griff\Documents\KSP-RMM-Dev',[string]$Fixture='Space Station Core.craft',[switch]$Minmus,[switch]$Resume,[switch]$Topology,[switch]$FreeTopology,[switch]$Docking,[switch]$DockingSource,[switch]$DockingCycles,[switch]$DockingPersistence,[switch]$DockingFSM,[switch]$StockConstruction)
$ErrorActionPreference='Stop'
$sourceRoot=Split-Path $PSScriptRoot -Parent
$resolved=[IO.Path]::GetFullPath($DevRoot).TrimEnd('\')
if($resolved -ne 'C:\Users\griff\Documents\KSP-RMM-Dev'){throw 'This test runner only operates on the designated development installation.'}
$exe=Join-Path $resolved 'KSP_x64.exe'
if(Get-CimInstance Win32_Process -Filter "Name='KSP_x64.exe'" | Where-Object {$_.ExecutablePath -eq $exe}){throw 'The development KSP is already running.'}
& (Join-Path $PSScriptRoot 'build.ps1')
dotnet build (Join-Path $sourceRoot 'dev\Foundations.DevTests.csproj') -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'Runtime harness build failed.'}
$dest=Join-Path $resolved 'GameData\ExpanseFoundations'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $sourceRoot 'GameData\ExpanseFoundations\*') -Destination $dest -Recurse -Force
$testPlugins=Join-Path $resolved 'GameData\FoundationsDevTests\Plugins'
New-Item -ItemType Directory -Force -Path $testPlugins | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'dev\bin\Release\net472\Foundations.DevTests.dll') -Destination $testPlugins -Force
# Use the user's already installed Harmony runtime, never bundle it with this mod.
Copy-Item -LiteralPath 'C:\Kerbal Space Program\GameData\000_Harmony' -Destination (Join-Path $resolved 'GameData') -Recurse -Force
if($Fixture -notin @('Space Station Core.craft','Two-Stage Lander.craft')){throw 'Unknown stock test fixture.'}
Copy-Item -LiteralPath (Join-Path 'C:\Kerbal Space Program\Ships\VAB' $Fixture) -Destination (Join-Path $resolved 'foundations-fixture.craft') -Force
$settings=Join-Path $resolved 'settings.cfg'
$text=Get-Content -LiteralPath $settings -Raw
$text=[regex]::Replace($text,'(?m)^(MASTER|SHIP|AMBIENCE|MUSIC|UI|VOICE)_VOLUME\s*=.*$','$1_VOLUME = 0')
Set-Content -LiteralPath $settings -Value $text
$request=if($Resume){'Resume'}elseif($StockConstruction){'Minmus StockConstruction'}elseif($DockingFSM){'Minmus DockingFSM'}elseif($DockingPersistence){'Minmus DockingPersistence'}elseif($DockingCycles){'Minmus DockingCycles'}elseif($DockingSource){'Minmus DockingSource'}elseif($Docking){'Minmus Docking'}elseif($FreeTopology){'Minmus FreeTopology'}elseif($Topology){'Minmus Topology'}elseif($Minmus){'Minmus'}else{'Kerbin'}
Set-Content -LiteralPath (Join-Path $resolved 'foundations-test-request.txt') -Value $request
$unityLog=Join-Path $resolved ('foundations-unity-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.log')
$process=Start-Process -FilePath $exe -WorkingDirectory $resolved -WindowStyle Hidden -ArgumentList '-muteaudio','-logFile',$unityLog -PassThru
Write-Output "Development test process: $($process.Id). Results: $resolved\foundations-tests-*.txt"
