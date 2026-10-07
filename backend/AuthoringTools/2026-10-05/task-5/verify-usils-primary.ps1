$ErrorActionPreference='Stop'
$primary='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'
$dotnet='C:\Program Files\dotnet\dotnet.exe'
foreach($project in @('Expanse.Clock.Host','Expanse.Clock.Manager')){
 $log=Join-Path $PSScriptRoot ('usils-primary-'+$project+'.log')
 & $dotnet build (Join-Path $primary ('src\'+$project+'\'+$project+'.csproj')) -c Release --nologo *> $log
 if($LASTEXITCODE -ne 0){Get-Content $log -Tail 20;throw 'Build failed'}
 Write-Output ($project+': passed')
}
foreach($name in @('RosterCensusChecks','PerformanceChecks','USILifeSupport.Detached','USILifeSupport.HookRegistration')){
 $log=Join-Path $PSScriptRoot ('usils-primary-'+$name+'.log')
 & $dotnet build (Join-Path $primary ('dev\'+$name+'\'+$name+'.csproj')) -c Release --nologo *> $log
 if($LASTEXITCODE -ne 0){Get-Content $log -Tail 20;throw 'Check build failed'}
 if($name -eq 'USILifeSupport.HookRegistration'){
  & (Join-Path $primary ('dev\'+$name+'\bin\Release\net472\'+$name+'.exe')) *>> $log
 }else{
  & $dotnet (Join-Path $primary ('dev\'+$name+'\bin\Release\net8.0\'+$name+'.dll')) *>> $log
 }
 if($LASTEXITCODE -ne 0){Get-Content $log -Tail 20;throw 'Check failed'}
 Write-Output ($name+': passed')
}
