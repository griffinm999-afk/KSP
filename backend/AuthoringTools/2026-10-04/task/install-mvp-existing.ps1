# Offline adaptation of ExpansePlatform/tools/install-production-delivery.ps1.
# Uses the reviewed MVP JSON manifest/layout; preserves launcher/pin and never launches.
[CmdletBinding()]
param([switch]$Apply)
$ErrorActionPreference='Stop'
$candidate='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\outputs\colony-mvp-candidate'
$payload=Join-Path $candidate 'release-payload'
$archive=Join-Path $candidate 'expanse-minmus-mvp.zip'
$archiveHash='1BF388FEE25CFDC94454F5693565DF4B2E60CA9AA0B6FB4E24223523CF2540B6'
$install='C:\Users\griff\AppData\Local\Programs\ExpanseFoundations'
$game='C:\Kerbal Space Program'
$state='C:\Users\griff\AppData\Local\ExpanseFoundations'
$backup='C:\Users\griff\Documents\Codex\2026-10-04\task\installation-backup-20261004'
$version='20261004-214449'
$versionRoot=Join-Path $install "versions\$version"
$pin='C:\Users\griff\AppData\Roaming\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\ExpanseFoundations.lnk'
$launcher=Join-Path $install 'Launch-Expanse-Foundations.ps1'
$recordPath=Join-Path $install 'INSTALLATION.json'
$modulePath='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform\tools\Expanse.Colony.Release.psm1'
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function Closed {
    $p=@(Get-CimInstance Win32_Process | Where-Object {$_.Name -match '^(KSP(?:_x64)?|Expanse.*)\.exe$'})
    if($p.Count){throw ('Game/app reopened; no installation allowed: '+(($p|ForEach-Object {$_.Name+' '+$_.ProcessId}) -join ', '))}
}
function SafePath([string]$path) {
    $current=[IO.Path]::GetFullPath($path)
    while($current){
        if(Test-Path -LiteralPath $current){if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Redirected path refused: $current"}}
        $parent=Split-Path -Parent $current
        if($parent -eq $current){break};$current=$parent
    }
}
function Inventory([string]$root) {
    if(!(Test-Path -LiteralPath $root)){return @()}
    @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        SafePath $_.FullName
        [pscustomobject]@{Relative=$_.FullName.Substring($root.Length).TrimStart('\');Sha256=(Hash $_.FullName)}
    })
}
function AssertInventory([string]$root,$expected){
    $actual=Inventory $root
    if(($actual|ConvertTo-Json -Depth 4 -Compress) -cne ($expected|ConvertTo-Json -Depth 4 -Compress)){throw "Protected inventory changed: $root"}
}
function AtomicCopy([string]$source,[string]$target,[string]$expected){
    SafePath $target
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    $temp=$target+'.mvp-install.tmp'
    if(Test-Path -LiteralPath $temp){throw "Prior temporary file exists: $temp"}
    Copy-Item -LiteralPath $source -Destination $temp
    if((Hash $temp) -ine $expected){throw "Temporary file hash differs: $target"}
    [IO.File]::Move($temp,$target,$true)
}
Closed
foreach($root in @($payload,$install,$game,$state,$backup)){SafePath $root}
if((Hash $archive) -ine $archiveHash){throw 'Candidate archive changed'}
$manifestPath=Join-Path $payload 'MANIFEST.json'
$manifestHash=Hash $manifestPath
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
if($manifest.Name -ne 'Expanse Minmus MVP' -or !$manifest.NativeHabitatQualified -or $manifest.Files.Count -ne 60){throw 'Unexpected candidate manifest'}
$map=@()
foreach($row in $manifest.Files){
    $source=[IO.Path]::GetFullPath((Join-Path $payload $row.Path))
    if(!$source.StartsWith($payload+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Source escapes payload'}
    SafePath $source
    if((Hash $source) -ine $row.Sha256){throw "Candidate file hash mismatch: $($row.Path)"}
    $relative=if($row.Path.StartsWith('Apps/')){$row.Path.Substring(5)}else{$row.Path}
    $map+=[pscustomobject]@{Path=$row.Path;Source=$source;VersionTarget=(Join-Path $versionRoot $relative);Sha256=$row.Sha256}
}
# Verify the archived bytes as well as the staged files before any target mutation.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try {
    if($zip.Entries.Count -ne 61){throw 'Unexpected archive inventory'}
    foreach($row in $manifest.Files){
        $entry=$zip.GetEntry($row.Path);if(!$entry){throw "Archive entry absent: $($row.Path)"}
        $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
        try{if([Convert]::ToHexString($sha.ComputeHash($stream)) -ine $row.Sha256){throw "Archive hash mismatch: $($row.Path)"}}finally{$stream.Dispose();$sha.Dispose()}
    }
}finally{$zip.Dispose()}
$record=Get-Content -LiteralPath $recordPath -Raw|ConvertFrom-Json
if($record.version -ne '20261002-045716'){throw 'Installation changed concurrently'}
if(Test-Path -LiteralPath $versionRoot){throw 'New version directory already exists'}
$oldLauncher=Get-Content -LiteralPath $launcher -Raw
if(!$oldLauncher.Contains('versions\'+$record.version)){throw 'Launcher does not select recorded old version'}
$newLauncher=$oldLauncher.Replace('versions\'+$record.version,'versions\'+$version)
$optional=($map|Where-Object {$_.Path -eq 'GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll'}).Sha256
$module=Import-Module $modulePath -Force -PassThru
$preflight=& $module {param($g,$h) Assert-ColonyPrerequisites $g $h $false} $game $optional
$saveRoot=Join-Path $game 'saves\The Expanse'
$saveBaseline=Inventory $saveRoot
$stateBaseline=Inventory $state
$pinHash=Hash $pin
$launcherHash=Hash $launcher
$recordHash=Hash $recordPath
$gamePlan=@()
foreach($row in $map|Where-Object {$_.Path.StartsWith('GameData/')} ){
    $target=[IO.Path]::GetFullPath((Join-Path $game $row.Path));SafePath $target
    if(!$target.StartsWith($game+'\GameData\',[StringComparison]::OrdinalIgnoreCase)){throw 'Game target escapes GameData'}
    $before=if(Test-Path -LiteralPath $target){Hash $target}else{$null}
    if($row.Path.EndsWith('.cfg') -and $before -and $before -ine $row.Sha256){throw "User configuration differs; refusing overwrite: $target"}
    $gamePlan+=[pscustomobject]@{Target=$target;Source=$row.Source;Before=$before;After=$row.Sha256;Backup=(Join-Path $backup ('managed-before\'+$row.Path))}
}
if(!$Apply){
    [pscustomobject]@{Status='preflight-passed';PackageFiles=$map.Count;GameFiles=$gamePlan.Count;Version=$version;TaskbarPreserved=$true;SaveFiles=$saveBaseline.Count;StateFiles=$stateBaseline.Count;ProviderPreflight=$preflight.Status;AutoLaunch=$false}|ConvertTo-Json
    return
}
Closed
if(Test-Path -LiteralPath (Join-Path $backup 'transaction.json')){throw 'Existing transaction must be preserved'}
Copy-Item -LiteralPath $recordPath,$launcher -Destination $backup
Copy-Item -LiteralPath $record.versionRoot -Destination (Join-Path $backup 'previous-version') -Recurse
Copy-Item -LiteralPath $state -Destination (Join-Path $backup 'AppData-ExpanseFoundations') -Recurse
AssertInventory (Join-Path $backup 'AppData-ExpanseFoundations') $stateBaseline
AssertInventory (Join-Path $backup 'The Expanse') $saveBaseline
foreach($row in $gamePlan|Where-Object {$_.Before}){
    New-Item -ItemType Directory -Path (Split-Path -Parent $row.Backup) -Force|Out-Null
    Copy-Item -LiteralPath $row.Target -Destination $row.Backup
    if((Hash $row.Backup) -ine $row.Before){throw 'Managed backup hash mismatch'}
}
$transaction=[ordered]@{AtUtc=[DateTime]::UtcNow.ToString('o');Version=$version;PreviousVersion=$record.version;ArchiveSha256=$archiveHash;ManifestSha256=$manifestHash;Backup=$backup;SaveBaseline=$saveBaseline;StateBaseline=$stateBaseline;PinSha256=$pinHash;LauncherBefore=$launcherHash;RecordBefore=$recordHash;GamePlan=$gamePlan}
$transaction|ConvertTo-Json -Depth 9|Set-Content -LiteralPath (Join-Path $backup 'transaction.json') -Encoding utf8
New-Item -ItemType Directory -Path $versionRoot|Out-Null
foreach($row in $map){AtomicCopy $row.Source $row.VersionTarget $row.Sha256}
AtomicCopy $manifestPath (Join-Path $versionRoot 'MANIFEST.json') $manifestHash
$changed=[Collections.Generic.List[object]]::new()
try{
    Closed
    if((Hash $archive) -ine $archiveHash -or (Hash $manifestPath) -ine $manifestHash){throw 'Candidate changed during staging'}
    AssertInventory $saveRoot $saveBaseline;AssertInventory $state $stateBaseline
    if((Hash $pin) -ine $pinHash -or (Hash $launcher) -ine $launcherHash -or (Hash $recordPath) -ine $recordHash){throw 'Launcher/installation changed concurrently'}
    foreach($row in $gamePlan){
        Closed
        $actual=if(Test-Path -LiteralPath $row.Target){Hash $row.Target}else{$null}
        if($actual -ine $row.Before){throw "Game target changed concurrently: $($row.Target)"}
        if($row.Before -ine $row.After){AtomicCopy $row.Source $row.Target $row.After;$changed.Add($row)}
    }
    $postflight=& $module {param($g,$h) Assert-ColonyPrerequisites $g $h $true} $game $optional
    foreach($row in $map){if((Hash $row.VersionTarget) -ine $row.Sha256){throw 'Version-file verification failed'}}
    foreach($row in $gamePlan){if((Hash $row.Target) -ine $row.After){throw 'Installed game-file verification failed'}}
    AssertInventory $saveRoot $saveBaseline;AssertInventory $state $stateBaseline
    Closed
    $launcherStage=Join-Path $backup 'new-launcher.ps1'
    Set-Content -LiteralPath $launcherStage -Value $newLauncher -Encoding utf8
    $tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseFile($launcherStage,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'New launcher syntax invalid'}
    $record.version=$version;$record.versionRoot=$versionRoot;$record.installedAtUtc=[DateTime]::UtcNow.ToString('o');$record.archiveSha256=$archiveHash;$record.backup=$backup;$record.verifiedPackageFiles=$map.Count
    $record.bridgeSha256=Hash (Join-Path $game 'GameData\ExpanseWorldBridge\Plugins\Expanse.WorldBridge.dll')
    $record.domainSha256=Hash (Join-Path $game 'GameData\ExpanseWorldBridge\Plugins\Expanse.Domain.dll')
    $record.regularPersistentSha256=Hash (Join-Path $saveRoot 'persistent.sfs')
    $record|Add-Member -NotePropertyName candidateManifestSha256 -NotePropertyValue $manifestHash -Force
    $record|Add-Member -NotePropertyName launchVerifiedBy -NotePropertyValue 'static launcher resolution only; applications not launched' -Force
    $recordStage=Join-Path $backup 'new-INSTALLATION.json'
    $record|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $recordStage -Encoding utf8
    AtomicCopy $launcherStage $launcher (Hash $launcherStage)
    AtomicCopy $recordStage $recordPath (Hash $recordStage)
    if((Hash $pin) -ine $pinHash){throw 'Taskbar shortcut changed'}
    AssertInventory $saveRoot $saveBaseline;AssertInventory $state $stateBaseline
    $result=[ordered]@{Status='installed-and-hash-verified';AtUtc=[DateTime]::UtcNow.ToString('o');Version=$version;VersionRoot=$versionRoot;PackageFilesVerified=$map.Count;GameFilesVerified=$gamePlan.Count;ProviderPreflight=$postflight.Status;TaskbarUnchanged=$true;SavesUnchanged=$true;StateUnchanged=$true;AutoLaunch=$false;Backup=$backup;ArchiveSha256=$archiveHash;ManagerExe=(Join-Path $versionRoot 'Manager\Expanse.Clock.Manager.exe');HostExe=(Join-Path $versionRoot 'Host\Expanse.Clock.Host.exe')}
    $result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $backup 'completion.json') -Encoding utf8
    $result|ConvertTo-Json -Depth 5
}catch{
    $failure=$_
    Closed
    AssertInventory $saveRoot $saveBaseline;AssertInventory $state $stateBaseline
    foreach($row in $changed){
        if((Hash $row.Target) -ine $row.After){throw "Rollback held for changed target: $($row.Target). Original error: $failure"}
        if($row.Before){AtomicCopy $row.Backup $row.Target $row.Before}else{
            $full=[IO.Path]::GetFullPath($row.Target)
            if(!$full.StartsWith($game+'\GameData\',[StringComparison]::OrdinalIgnoreCase)){throw 'Rollback deletion outside GameData'}
            Remove-Item -LiteralPath $full
        }
    }
    AtomicCopy (Join-Path $backup 'Launch-Expanse-Foundations.ps1') $launcher $launcherHash
    AtomicCopy (Join-Path $backup 'INSTALLATION.json') $recordPath $recordHash
    throw "Installation failed; old managed files and launcher restored. New staging retained. $failure"
}
