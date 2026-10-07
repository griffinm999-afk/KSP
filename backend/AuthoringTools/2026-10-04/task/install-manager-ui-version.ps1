# Offline, guarded switch for the UI-only Expanse Foundations version.
# Run without -Apply to verify the package. -Apply requires KSP, Host and Manager closed.
[CmdletBinding()]
param([switch]$Apply)
$ErrorActionPreference='Stop'
$release='C:\Users\griff\Documents\Codex\2026-10-04\task\manager-ui-release-20261005-001323'
$staged=Join-Path $release 'version'
$receipt=Get-Content (Join-Path $release 'RECEIPT.json') -Raw|ConvertFrom-Json
$manifest=Get-Content (Join-Path $staged 'MANIFEST.json') -Raw|ConvertFrom-Json
$install='C:\Users\griff\AppData\Local\Programs\ExpanseFoundations'
$recordPath=Join-Path $install 'INSTALLATION.json'
$launcher=Join-Path $install 'Launch-Expanse-Foundations.ps1'
$pin='C:\Users\griff\AppData\Roaming\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\ExpanseFoundations.lnk'
$newRoot=Join-Path $install ('versions\'+$receipt.version)
$rollback=Join-Path $release 'rollback'
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function SafePath([string]$path){
    $current=[IO.Path]::GetFullPath($path)
    while($current){
        if(Test-Path -LiteralPath $current){if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Redirected path refused: $current"}}
        $parent=Split-Path -Parent $current;if($parent -eq $current){break};$current=$parent
    }
}
function Relative([string]$path){if($path.StartsWith('Apps/')){$path.Substring(5).Replace('/','\')}else{$path.Replace('/','\')}}
function VerifyPayload([string]$root){
    foreach($row in $manifest.Files){
        $path=Join-Path $root (Relative $row.Path);SafePath $path
        if(!(Test-Path -LiteralPath $path) -or (Hash $path) -ine $row.Sha256){throw "Package file mismatch: $($row.Path)"}
    }
    if((Hash (Join-Path $root 'MANIFEST.json')) -ine $receipt.manifestSha256){throw 'Package manifest mismatch'}
    $actual=@(Get-ChildItem -LiteralPath $root -Recurse -File -Force)
    if($actual.Count -ne ($manifest.Files.Count+1)){throw 'Package file inventory changed'}
}
function Running{
    @(Get-CimInstance Win32_Process|Where-Object {$_.Name -in @('KSP_x64.exe','KSP.exe','Expanse.Clock.Host.exe','Expanse.Clock.Manager.exe')}|Select-Object Name,ProcessId)
}
foreach($path in @($release,$staged,$install,$newRoot,$rollback,$launcher,$recordPath,$pin)){SafePath $path}
if($receipt.version -ne '20261005-001323' -or $receipt.baseVersion -ne '20261004-214449' -or $manifest.UiUpdate.version -ne $receipt.version -or $manifest.Files.Count -ne 60){throw 'Unexpected UI release identity'}
VerifyPayload $staged
$archive=Join-Path $release $receipt.archive
if((Hash $archive) -ine $receipt.archiveSha256){throw 'Release archive changed'}
$record=Get-Content $recordPath -Raw|ConvertFrom-Json
if($record.version -ne $receipt.baseVersion){throw 'Installed version changed; rebase package before applying'}
if(Test-Path -LiteralPath $newRoot){throw 'UI version directory already exists'}
$oldLauncher=Get-Content -LiteralPath $launcher -Raw
$oldSegment='versions\'+$receipt.baseVersion
if(!$oldLauncher.Contains($oldSegment)){throw 'Launcher does not point to the recorded base version'}
$newLauncher=$oldLauncher.Replace($oldSegment,'versions\'+$receipt.version)
$pinHash=Hash $pin
$running=@(Running)
if(!$Apply){
    [pscustomobject]@{Status='package-verified';Version=$receipt.version;Files=$manifest.Files.Count;Running=$running;ReadyToApply=($running.Count -eq 0);LauncherTarget=$newRoot;GameDataChanges=0;AutoLaunch=$false}|ConvertTo-Json -Depth 5
    return
}
if($running.Count){throw 'KSP, Host and Manager must close normally before changing the installed launcher'}
if(Test-Path -LiteralPath $rollback){throw 'Rollback directory already exists; preserve it and investigate before retrying'}
New-Item -ItemType Directory -Path $rollback|Out-Null
Copy-Item -LiteralPath $recordPath -Destination (Join-Path $rollback 'INSTALLATION.json')
Copy-Item -LiteralPath $launcher -Destination (Join-Path $rollback 'Launch-Expanse-Foundations.ps1')
Copy-Item -LiteralPath $pin -Destination (Join-Path $rollback 'ExpanseFoundations.lnk')
try{
    if(@(Running).Count){throw 'Game or app reopened during staging'}
    Copy-Item -LiteralPath $staged -Destination $newRoot -Recurse
    VerifyPayload $newRoot
    if(@(Running).Count){throw 'Game or app reopened before launcher switch'}
    if((Hash $pin) -ine $pinHash){throw 'Taskbar pin changed during staging'}
    $launcherTemp=$launcher+'.ui-update.tmp'
    $recordTemp=$recordPath+'.ui-update.tmp'
    if((Test-Path $launcherTemp) -or (Test-Path $recordTemp)){throw 'Temporary switch file already exists'}
    [IO.File]::WriteAllText($launcherTemp,$newLauncher,[Text.UTF8Encoding]::new($false))
    $tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseFile($launcherTemp,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Updated launcher failed syntax check'}
    $record.version=$receipt.version;$record.versionRoot=$newRoot;$record.installedAtUtc=[DateTime]::UtcNow.ToString('o');$record.archiveSha256=$receipt.archiveSha256;$record.backup=$rollback
    $record|Add-Member -NotePropertyName uiManifestSha256 -NotePropertyValue $receipt.manifestSha256 -Force
    [IO.File]::WriteAllText($recordTemp,($record|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($launcherTemp,$launcher,$true)
    [IO.File]::Move($recordTemp,$recordPath,$true)
    if((Hash $pin) -ine $pinHash -or !(Get-Content $launcher -Raw).Contains('versions\'+$receipt.version)){throw 'Post-switch verification failed'}
    [pscustomobject]@{Status='installed';Version=$receipt.version;Launcher=$launcher;Rollback=$rollback;GameDataChanges=0;AutoLaunch=$false}|ConvertTo-Json
}catch{
    Copy-Item -LiteralPath (Join-Path $rollback 'Launch-Expanse-Foundations.ps1') -Destination $launcher -Force
    Copy-Item -LiteralPath (Join-Path $rollback 'INSTALLATION.json') -Destination $recordPath -Force
    throw "UI switch failed; launcher and record restored from $rollback. New version directory retained for diagnosis. $($_.Exception.Message)"
}
