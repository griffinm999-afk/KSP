[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArchivePath,[Parameter(Mandatory)][string]$ExpectedArchiveSha256,[Parameter(Mandatory)][string]$InputManifestPath,[Parameter(Mandatory)][string]$ExpectedInputManifestSha256)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Expanse.Colony.Release.psm1') -Force
$module=Get-Module Expanse.Colony.Release
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureBase=Join-Path $repo 'artifacts/colony-release-fixtures'
$null=[IO.Directory]::CreateDirectory($fixtureBase)
$script:Checks=[Collections.Generic.List[object]]::new()
$script:PayloadProfile=& $module {param($Path) Get-ColonyPayloadProfile (Read-ColonyJson $Path)} $InputManifestPath
$script:PayloadPaths=@(& $module {param($Profile) Get-ColonyPayloadPaths $Profile} $script:PayloadProfile)
$script:TemplateCount=@(& $module {param($Profile) Get-ColonyTemplateIds $Profile} $script:PayloadProfile).Count
# There is no live process query in this test process; the shipping implementation is unchanged.
& $module {
    $script:OriginalProcess=(Get-Command Get-ColonyTargetProcesses).ScriptBlock
    $script:OriginalInstall=(Get-Command Set-ColonyInstalledFile).ScriptBlock
    $script:OriginalWrite=(Get-Command Write-ColonyJson).ScriptBlock
    $script:OriginalCopy=(Get-Command Copy-ColonyVerified).ScriptBlock
    $script:OriginalRestore=(Get-Command Restore-ColonyTransaction).ScriptBlock
    $script:OriginalDefaultState=(Get-Command Get-ColonyDefaultStateRoot).ScriptBlock
    $script:OriginalPrerequisites=(Get-Command Test-ColonyPrerequisites).ScriptBlock
    $script:OriginalPrerequisiteAssertion=(Get-Command Assert-ColonyPrerequisites).ScriptBlock
    function script:Get-ColonyTargetProcesses($Roots) { @() }
    # Provider preflight gets its own read-only metadata fixtures. Fake game
    # roots here contain text executables, not a mod installation.
    function script:Test-ColonyPrerequisites([string]$GameRoot,[string]$ExpectedOptionalAssemblySha256) { @{Status='mocked-disposable-provider'} }
    function script:Assert-ColonyPrerequisites([string]$GameRoot,[string]$ExpectedOptionalAssemblySha256,[bool]$RequireOptional) { @{Status='mocked-disposable-provider'} }
}
function Check([string]$Name,[scriptblock]$Body) {
    try { & $Body; $script:Checks.Add(@{Name=$Name;Passed=$true}); Write-Host "PASS $Name" }
    catch { $script:Checks.Add(@{Name=$Name;Passed=$false;Reason=$_.Exception.Message}); Write-Host "FAIL $Name : $($_.Exception.Message)"; throw }
}
function Require([bool]$Condition,[string]$Reason) { if (-not $Condition) { throw $Reason } }
function Reject([scriptblock]$Body,[string]$Pattern) {
    $held=$false
    try { & $Body | Out-Null } catch { if ($_.Exception.Message -notmatch $Pattern) { throw "Unexpected refusal: $($_.Exception.Message)" };$held=$true }
    Require $held 'Expected refusal did not occur.'
}
function Write-Text([string]$Path,[string]$Text) { $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path));[IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false)) }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Fixture {
    $id=[guid]::NewGuid().ToString('N');$root=Join-Path $fixtureBase $id
    $roots=@{GameRoot=Join-Path $root 'Game';AppRoot=Join-Path $root 'App';StateRoot=Join-Path $root 'State';BackupRoot=Join-Path $root 'Backup'}
    foreach ($path in $roots.Values) { $null=[IO.Directory]::CreateDirectory($path) }
    Write-Text (Join-Path $root 'ISOLATED-COLONY-TEST.json') (@{SchemaVersion=1;Id=$id;Purpose='disposable-colony-tooling-test'} | ConvertTo-Json)
    Write-Text (Join-Path $roots.GameRoot 'KSP_x64.exe') "DISPOSABLE-COLONY-TEST $id"
    Write-Text (Join-Path $roots.GameRoot 'saves/Fixture/persistent.sfs') 'BASELINE SELECTED SAVE'
    Write-Text (Join-Path $roots.GameRoot 'saves/Fixture/quicksave.sfs') 'BASELINE QUICKSAVE'
    Write-Text (Join-Path $roots.GameRoot 'saves/Other/persistent.sfs') 'UNSELECTED SAVE'
    Write-Text (Join-Path $roots.StateRoot 'host.db') 'BASELINE APP STATE'
    Write-Text (Join-Path $roots.GameRoot 'GameData/ExpanseWorldBridge/Plugins/Expanse.WorldBridge.dll') 'OLD BRIDGE'
    Write-Text (Join-Path $roots.GameRoot 'GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll') 'OLD DOMAIN'
    Write-Text (Join-Path $roots.GameRoot 'GameData/ExpanseWorldBridge/ColonyEconomy.cfg') 'OLD ECONOMY'
    Write-Text (Join-Path $roots.GameRoot 'GameData/ExpanseFoundations/Plugins/ExpanseFoundations.dll') 'OLD FOUNDATION'
    Write-Text (Join-Path $roots.GameRoot 'GameData/ExpanseFoundations/Settings.cfg') 'OLD FOUNDATION SETTINGS'
    Write-Text (Join-Path $roots.AppRoot 'versions/prior-version/Apps/Host/Expanse.Clock.Host.exe') 'OLD HOST'
    Write-Text (Join-Path $roots.AppRoot 'versions/prior-version/Apps/Manager/Expanse.Clock.Manager.exe') 'OLD MANAGER'
    Write-Text (Join-Path $roots.AppRoot 'current.json') '{"ReleaseId":"prior-version"}'
    # The prior version has the entire managed binary/config/runtime allowlist, not just two executables.
    foreach($path in $script:PayloadPaths){
        $old=if($path -like 'GameData/*'){Join-Path $roots.GameRoot $path}else{Join-Path $roots.AppRoot "versions/prior-version/$path"}
        if(-not (Test-Path -LiteralPath $old)){Write-Text $old "OLD DISPOSABLE FIXTURE $path"}
    }
    [pscustomobject]@{Root=$root;Roots=$roots;SelectedSaveRelativePath='saves/Fixture/persistent.sfs';ExpectedSaveSha256=Hash (Join-Path $roots.GameRoot 'saves/Fixture/persistent.sfs')}
}
function Install($F,[string]$Archive=$ArchivePath,[string]$Digest=$ExpectedArchiveSha256,[bool]$Isolated=$true) {
    $roots=$F.Roots
    Invoke-ColonyInstall @roots -ArchivePath $Archive -ExpectedArchiveSha256 $Digest -SelectedSaveRelativePath $F.SelectedSaveRelativePath -ExpectedSaveSha256 $F.ExpectedSaveSha256 -IsolatedTestCandidate:$Isolated
}
function Rollback($F,$Receipt,[switch]$RecoverIncomplete,[switch]$RestoreUnchangedSaveAndState) {
    $roots=$F.Roots
    Invoke-ColonyRollback @roots -ReceiptPath $Receipt.ReceiptPath -ExpectedReceiptSha256 $Receipt.ReceiptSha256 -RecoverIncomplete:$RecoverIncomplete -RestoreUnchangedSaveAndState:$RestoreUnchangedSaveAndState
}
function Inventory($F) {
    & $module {param($F) Get-ColonyScopeInventory @{Game=$F.Roots.GameRoot;App=$F.Roots.AppRoot;State=$F.Roots.StateRoot;Backup=$F.Roots.BackupRoot} 'saves/Fixture'} $F
}
function InventoryKey($Value) { & $module {param($Value) ($Value | ConvertTo-Json -Depth 100 -Compress)} $Value }
function AssertBaseline($F,$Before) { Require ((InventoryKey (Inventory $F)) -ceq (InventoryKey $Before)) 'Complete binary/config/app/save/state baseline did not return.' }
function ReceiptFor($F) {
    $paths=@(Get-ChildItem -LiteralPath $F.Roots.BackupRoot -Recurse -File -Filter receipt.json)
    Require ($paths.Count -eq 1) 'Expected one immutable recovery receipt.'
    @{ReceiptPath=$paths[0].FullName;ReceiptSha256=Hash $paths[0].FullName}
}
function ResetFaults {
    & $module {
        Set-Item Function:script:Set-ColonyInstalledFile $script:OriginalInstall
        Set-Item Function:script:Write-ColonyJson $script:OriginalWrite
        Set-Item Function:script:Copy-ColonyVerified $script:OriginalCopy
        Set-Item Function:script:Restore-ColonyTransaction $script:OriginalRestore
        Set-Item Function:script:Get-ColonyDefaultStateRoot $script:OriginalDefaultState
        function script:Get-ColonyTargetProcesses($Roots) { @() }
    }
}
function MutatedArchive($F,[string]$Operation) {
    $path=Join-Path $F.Root 'modified.zip';[IO.File]::Copy($ArchivePath,$path)
    $zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Update)
    try {
        switch ($Operation) {
            missing { $zip.GetEntry('GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll').Delete() }
            extra { $entry=$zip.CreateEntry('GameData/ExpanseWorldBridge/Plugins/Unexpected.dll');$stream=$entry.Open();$stream.WriteByte(1);$stream.Dispose() }
            traversal { $entry=$zip.CreateEntry('../outside.dll');$stream=$entry.Open();$stream.WriteByte(1);$stream.Dispose() }
            duplicate { $entry=$zip.CreateEntry('GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll');$stream=$entry.Open();$stream.WriteByte(1);$stream.Dispose() }
            corrupt { $entry=$zip.GetEntry('GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll');$entry.Delete();$entry=$zip.CreateEntry('GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll');$stream=$entry.Open();$stream.WriteByte(1);$stream.Dispose() }
            certified {
                $entry=$zip.GetEntry('package.json');$reader=[IO.StreamReader]::new($entry.Open());$text=$reader.ReadToEnd();$reader.Dispose();$data=$text|ConvertFrom-Json -AsHashtable;$data.NativeQualified=$true;$entry.Delete();$entry=$zip.CreateEntry('package.json');$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write(($data|ConvertTo-Json -Depth 100));$writer.Dispose()
            }
        }
    } finally { $zip.Dispose() }
    $path
}
try {
    Check 'candidate cannot install by default' { $f=Fixture;$before=Inventory $f;Reject {Install $f -Isolated $false} 'Candidate installation refused';AssertBaseline $f $before }
    Check 'archive pin mismatch holds' { $f=Fixture;Reject {Install $f -Digest ('0'*64)} 'SHA256 mismatch' }
    Check 'selected save pin mismatch holds before mutation' { $f=Fixture;$before=Inventory $f;$f.ExpectedSaveSha256='0'*64;Reject {Install $f} 'SHA256 mismatch';AssertBaseline $f $before }
    foreach ($case in @('missing','extra','traversal','duplicate','corrupt','certified')) {
        Check "archive $case holds without target mutation" { $f=Fixture;$before=Inventory $f;$archive=MutatedArchive $f $case;Reject {Install $f $archive (Hash $archive)} 'allowlist|Unsafe|SHA256|qualification';AssertBaseline $f $before }
    }
    Check 'extra installed managed DLL holds' { $f=Fixture;Write-Text (Join-Path $f.Roots.GameRoot 'GameData/ExpanseWorldBridge/Plugins/Unexpected.dll') 'EXTRA';$before=Inventory $f;Reject {Install $f} 'Unexpected managed file';AssertBaseline $f $before }
    Check 'unversioned app DLL holds' { $f=Fixture;Write-Text (Join-Path $f.Roots.AppRoot 'Unexpected.dll') 'EXTRA';Reject {Install $f} 'Unversioned' }
    Check 'backup nested under GameData holds' { $f=Fixture;$f.Roots.BackupRoot=Join-Path $f.Roots.GameRoot 'GameData/Backup';$null=[IO.Directory]::CreateDirectory($f.Roots.BackupRoot);Reject {Install $f} 'overlaps' }
    Check 'state and app overlapping holds' { $f=Fixture;$f.Roots.StateRoot=$f.Roots.AppRoot;Reject {Install $f} 'overlaps' }
    Check 'candidate refuses real executable bytes' { $f=Fixture;Write-Text (Join-Path $f.Roots.GameRoot 'KSP_x64.exe') 'MZ NOT A FIXTURE';Reject {Install $f} 'real game executable' }
    Check 'candidate refuses incorrect disposable marker' { $f=Fixture;Write-Text (Join-Path $f.Root 'ISOLATED-COLONY-TEST.json') '{"SchemaVersion":1,"Id":"wrong","Purpose":"disposable-colony-tooling-test"}';Reject {Install $f} 'Invalid isolated fixture' }
    Check 'candidate cannot relocate roots outside its exact disposable contract' { $f=Fixture;$f.Roots.AppRoot=Join-Path $f.Root 'DifferentApp';$null=[IO.Directory]::CreateDirectory($f.Roots.AppRoot);Reject {Install $f} 'contract mismatch' }
    Check 'hard linked selected save holds' { $f=Fixture;$path=Join-Path $f.Roots.GameRoot 'saves/Fixture/persistent.sfs';$null=New-Item -ItemType HardLink -Path (Join-Path $f.Root 'linked.sfs') -Target $path;Reject {Install $f} 'Hard-linked' }
    Check 'junction game ancestor holds' { $f=Fixture;$real=Join-Path $f.Root 'RealGame';[IO.Directory]::Move($f.Roots.GameRoot,$real);$null=New-Item -ItemType Junction -Path $f.Roots.GameRoot -Target $real;Reject {Install $f} 'Reparse' }
    Check 'junction nested managed folder holds without following it' { $f=Fixture;$path=Join-Path $f.Roots.GameRoot 'GameData/ExpanseWorldBridge/Plugins';$other=Join-Path $f.Root 'OtherPlugins';[IO.Directory]::Move($path,$other);$null=New-Item -ItemType Junction -Path $path -Target $other;Reject {Install $f} 'Reparse' }
    Check 'running target process holds and is not terminated' {
        $f=Fixture;$before=Inventory $f
        & $module {function script:Get-ColonyTargetProcesses($Roots) { @{Name='KSP_x64.exe';ExecutablePath=(Join-Path $Roots.Game 'KSP_x64.exe');ProcessId=123456} }}
        Reject {Install $f} 'running';ResetFaults;AssertBaseline $f $before
    }
    Check 'exact full binary/config/app rollback and complete backup' {
        $f=Fixture;$before=Inventory $f;$receipt=Install $f
        $data=Get-Content -LiteralPath $receipt.ReceiptPath -Raw | ConvertFrom-Json -AsHashtable
        Require ($data.Before.Save.Count -eq 2 -and $data.Before.State.Count -eq 1 -and $data.Before.App.Count -eq (@($script:PayloadPaths|Where-Object{$_ -notlike 'GameData/*'}).Count+1)) 'Complete save/state/prior app backup missing.'
        $expectedBackupCount=0;foreach($scope in $before.Keys){$expectedBackupCount+=@($before[$scope]).Count}
        Require ((Get-ChildItem -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($receipt.ReceiptPath)) 'before') -Recurse -File).Count -eq $expectedBackupCount) 'Expected complete baseline backup.'
        Rollback $f $receipt | Out-Null;AssertBaseline $f $before
    }
    Check 'mid-apply failure restores the exact baseline' {
        $f=Fixture;$before=Inventory $f
        & $module {$script:ApplyCount=0;function script:Set-ColonyInstalledFile($Roots,$Transaction,$Operation) {$script:ApplyCount++;if($script:ApplyCount -eq 9){throw 'injected apply failure'};& $script:OriginalInstall $Roots $Transaction $Operation}}
        Reject {Install $f} 'failed and known binaries restored';ResetFaults;AssertBaseline $f $before
        Rollback $f (ReceiptFor $f) -RecoverIncomplete | Out-Null;AssertBaseline $f $before
    }
    Check 'crashed partial temporary copy is recovered only as a known prefix' {
        $f=Fixture;$before=Inventory $f
        & $module {$script:FailedCopy=$false;function script:Copy-ColonyVerified($Source,$Destination,$Hash) {if(-not $script:FailedCopy -and $Destination.EndsWith('.tmp')){$script:FailedCopy=$true;$null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination));$bytes=[IO.File]::ReadAllBytes($Source);[IO.File]::WriteAllBytes($Destination,$bytes[0..7]);throw 'injected partial copy'};& $script:OriginalCopy $Source $Destination $Hash}}
        Reject {Install $f} 'failed and known binaries restored';ResetFaults;AssertBaseline $f $before
    }
    Check 'completion status failure preserves immutable receipt and restores baseline' {
        $f=Fixture;$before=Inventory $f
        & $module {$script:FailedStatus=$false;function script:Write-ColonyJson($Path,$Value) {if(-not $script:FailedStatus -and $Path.EndsWith('status.json')){$script:FailedStatus=$true;throw 'injected status write failure'};& $script:OriginalWrite $Path $Value}}
        Reject {Install $f} 'failed and known binaries restored';ResetFaults;AssertBaseline $f $before;Rollback $f (ReceiptFor $f) -RecoverIncomplete | Out-Null
    }
    Check 'immutable receipt write failure cannot change targets' {
        $f=Fixture;$before=Inventory $f
        & $module {function script:Write-ColonyJson($Path,$Value) {if($Path.EndsWith('receipt.json')){throw 'injected receipt write failure'};& $script:OriginalWrite $Path $Value}}
        Reject {Install $f} 'injected receipt';ResetFaults;AssertBaseline $f $before
    }
    Check 'installed binary drift holds ordinary and incomplete rollback before mutation' {
        $f=Fixture;$receipt=Install $f;Write-Text (Join-Path $f.Roots.GameRoot 'GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll') 'PLAYER CHANGED DLL';$before=Inventory $f
        Reject {Rollback $f $receipt} 'SHA256';Reject {Rollback $f $receipt -RecoverIncomplete} 'Unknown recovery bytes';AssertBaseline $f $before
    }
    Check 'missing prior app bytes hold incomplete recovery' {
        $f=Fixture;$receipt=Install $f;Remove-Item -LiteralPath (Join-Path $f.Roots.AppRoot 'versions/prior-version/Apps/Host/Expanse.Clock.Host.exe');Reject {Rollback $f $receipt -RecoverIncomplete} 'Prior inventory file is missing'
    }
    Check 'extra postinstall DLL holds rollback' { $f=Fixture;$receipt=Install $f;Write-Text (Join-Path $f.Roots.GameRoot 'GameData/ExpanseWorldBridge/Plugins/Extra.dll') 'EXTRA';Reject {Rollback $f $receipt} 'allowlist' }
    Check 'receipt hash and roots are exact fences' { $f=Fixture;$receipt=Install $f;$wrong=@{ReceiptPath=$receipt.ReceiptPath;ReceiptSha256='0'*64};Reject {Rollback $f $wrong} 'SHA256';$f.Roots.StateRoot=Join-Path $f.Root 'OtherState';$null=[IO.Directory]::CreateDirectory($f.Roots.StateRoot);Reject {Rollback $f $receipt} 'root mismatch' }
    Check 'corrupt backup holds rollback before target mutation' { $f=Fixture;$receipt=Install $f;$path=Join-Path ([IO.Path]::GetDirectoryName($receipt.ReceiptPath)) 'before/GameData/ExpanseWorldBridge/Plugins/Expanse.WorldBridge.dll';Write-Text $path 'CORRUPT BACKUP';$before=Inventory $f;Reject {Rollback $f $receipt} 'SHA256';AssertBaseline $f $before }
    Check 'default binary rollback refuses advanced save without rewinding it' {
        $f=Fixture;$before=Inventory $f;$receipt=Install $f
        Write-Text (Join-Path $f.Roots.GameRoot 'saves/Fixture/persistent.sfs') 'NEW PLAY PROGRESS'
        $installed=Inventory $f
        Reject {Rollback $f $receipt} 'Inventory drift'
        Reject {Rollback $f $receipt -RecoverIncomplete} 'Inventory drift'
        Reject {Rollback $f $receipt -RestoreUnchangedSaveAndState} 'Inventory drift'
        AssertBaseline $f $installed
        Require ([IO.File]::ReadAllText((Join-Path $f.Roots.GameRoot 'saves/Fixture/persistent.sfs')) -ceq 'NEW PLAY PROGRESS') 'Save progress was rewound.'
        Require ([IO.File]::ReadAllText((Join-Path $f.Roots.GameRoot 'saves/Other/persistent.sfs')) -ceq 'UNSELECTED SAVE') 'Unselected save changed.'
    }
    Check 'default binary rollback refuses advanced app state' {$f=Fixture;$receipt=Install $f;Write-Text (Join-Path $f.Roots.StateRoot 'host.db') 'NEW STATE';$installed=Inventory $f;Reject {Rollback $f $receipt} 'Inventory drift';Reject {Rollback $f $receipt -RecoverIncomplete} 'Inventory drift';AssertBaseline $f $installed}
    Check 'unchanged save/state option is verified without rewriting either' { $f=Fixture;$before=Inventory $f;$receipt=Install $f;$save=Join-Path $f.Roots.GameRoot 'saves/Fixture/persistent.sfs';$time=(Get-Item -LiteralPath $save).LastWriteTimeUtc;Rollback $f $receipt -RestoreUnchangedSaveAndState|Out-Null;AssertBaseline $f $before;Require ((Get-Item -LiteralPath $save).LastWriteTimeUtc -eq $time) 'Unchanged save was needlessly rewritten.' }
    Check 'all lexical path attacks hold' { foreach($path in @('../outside.dll','/rooted','C:/absolute','a//b','a/../b','a/CON.txt','a/name.','a/name:ads','a\b','a/./b')){Reject {& $module {param($Path) Assert-ColonyRelative $Path} $path} 'Unsafe'} }
    Check 'public documentation redacts absolute local paths while preserving links' { $result=& $module {Convert-ColonyPublicText 'Public https://example.com/reference, local "C:\private\evidence.log".'};Require ($result -ceq 'Public https://example.com/reference, local "<local-path>".') 'Documentation sanitization altered a public link or kept private path.' }
    Check 'external named Host using selected state is still a target' {
        $f=Fixture;$roots=@{Game=$f.Roots.GameRoot;App=$f.Roots.AppRoot;State=$f.Roots.StateRoot}
        $process=@{Name='Expanse.Clock.Host.exe';ExecutablePath=Join-Path $f.Root 'OtherBinaries/Expanse.Clock.Host.exe';CommandLine=('"other-host" --data-dir "'+$f.Roots.StateRoot+'"')}
        Require (& $module {param($Process,$Roots) Test-ColonyProcessOwnership $Process $Roots} $process $roots) 'External Host state ownership bypassed closure.'
        $process.CommandLine='';Reject {& $module {param($Process,$Roots) Test-ColonyProcessOwnership $Process $Roots} $process $roots} 'command-line/state ownership'
        $process.CommandLine='"other-host" --data-dir relative-state';Reject {& $module {param($Process,$Roots) Test-ColonyProcessOwnership $Process $Roots} $process $roots} 'relative Host'
        $process.CommandLine='"other-host"'
        & $module {param($Path) $script:FixtureDefaultState=$Path;function script:Get-ColonyDefaultStateRoot{$script:FixtureDefaultState}} $f.Roots.StateRoot
        Require (& $module {param($Process,$Roots) Test-ColonyProcessOwnership $Process $Roots} $process $roots) 'Default Host state ownership bypassed closure.'
        ResetFaults
    }
    Check 'external dotnet Host default and parent state ownership are targets' {
        $f=Fixture;$roots=@{Game=$f.Roots.GameRoot;App=$f.Roots.AppRoot;State=$f.Roots.StateRoot}
        & $module {param($Path) $script:FixtureDefaultState=$Path;function script:Get-ColonyDefaultStateRoot{$script:FixtureDefaultState}} $f.Roots.StateRoot
        $process=@{Name='dotnet.exe';ExecutablePath=Join-Path $f.Root 'OtherBinaries/dotnet.exe';CommandLine=('dotnet "'+(Join-Path $f.Root 'OtherBinaries/Expanse.Clock.Host.dll')+'"')}
        Require (& $module {param($Process,$Roots) Test-ColonyProcessOwnership $Process $Roots} $process $roots) 'External dotnet Host default-state ownership bypassed closure.'
        $process.CommandLine+=' --data-dir "'+$f.Root+'"'
        Require (& $module {param($Process,$Roots) Test-ColonyProcessOwnership $Process $Roots} $process $roots) 'External dotnet Host parent-of-state ownership bypassed closure.'
        $process.CommandLine='dotnet "'+(Join-Path $f.Root 'OtherBinaries/Expanse.Clock.Host.dll')+'" --data-dir'
        Reject {& $module {param($Process,$Roots) Test-ColonyProcessOwnership $Process $Roots} $process $roots} 'ambiguous Host'
        $process.CommandLine='dotnet "'+(Join-Path $f.Root 'OtherBinaries/Unrelated.dll')+'"'
        Require (-not (& $module {param($Process,$Roots) Test-ColonyProcessOwnership $Process $Roots} $process $roots)) 'Unrelated dotnet process was classified as a target.'
        ResetFaults
    }
    Check 'install between-copy-and-commit drift is preserved' {
        $f=Fixture
        & $module {function script:Copy-ColonyVerified($Source,$Destination,$Hash) {& $script:OriginalCopy $Source $Destination $Hash;if($Destination.EndsWith('.tmp') -and $Destination.Contains('Expanse.WorldBridge.dll.expanse-')){$target=$Destination.Substring(0,$Destination.IndexOf('.expanse-'));[IO.File]::WriteAllText($target,'INTERVENING EDIT')}}}
        Reject {Install $f} 'Recovery held';ResetFaults
        Require ([IO.File]::ReadAllText((Join-Path $f.Roots.GameRoot 'GameData/ExpanseWorldBridge/Plugins/Expanse.WorldBridge.dll')) -ceq 'INTERVENING EDIT') 'Intervening target bytes were overwritten.'
        Reject {Rollback $f (ReceiptFor $f) -RecoverIncomplete} 'Unknown recovery bytes'
    }
    Check 'rollback between-copy-and-commit drift is preserved' {
        $f=Fixture;$receipt=Install $f
        & $module {$script:RollbackDriftPath=$null;function script:Copy-ColonyVerified($Source,$Destination,$Hash) {& $script:OriginalCopy $Source $Destination $Hash;if($Source.Contains('before') -and $Destination.EndsWith('.tmp')){$target=$Destination.Substring(0,$Destination.IndexOf('.expanse-'));$script:RollbackDriftPath=$target;[IO.File]::WriteAllText($target,'INTERVENING ROLLBACK EDIT')}}}
        Reject {Rollback $f $receipt} 'SHA256';ResetFaults
        Reject {Rollback $f $receipt -RecoverIncomplete} 'Unknown recovery bytes'
        $driftPath=& $module {$script:RollbackDriftPath}
        Require (-not [string]::IsNullOrWhiteSpace($driftPath) -and [IO.File]::ReadAllText($driftPath) -ceq 'INTERVENING ROLLBACK EDIT') 'Exact injected rollback target bytes were overwritten.'
    }
    Check 'interrupted install can recover from immutable receipt without completion status' {
        $f=Fixture;$before=Inventory $f
        & $module {$script:ApplyCount=0;function script:Set-ColonyInstalledFile($Roots,$Transaction,$Operation){$script:ApplyCount++;if($script:ApplyCount -eq 9){throw 'simulated interruption'};& $script:OriginalInstall $Roots $Transaction $Operation};function script:Restore-ColonyTransaction{throw 'simulated process loss before automatic restoration'}}
        Reject {Install $f} 'Recovery held';ResetFaults
        Rollback $f (ReceiptFor $f) -RecoverIncomplete|Out-Null;AssertBaseline $f $before
    }
    Check 'interrupted rollback is recoverable without replacing its receipt' {
        $f=Fixture;$before=Inventory $f;$receipt=Install $f
        & $module {$script:RestoreCopies=0;function script:Copy-ColonyVerified($Source,$Destination,$Hash){if($Source.Contains('before') -and $Destination.EndsWith('.tmp')){$script:RestoreCopies++;if($script:RestoreCopies -eq 2){$bytes=[IO.File]::ReadAllBytes($Source);[IO.File]::WriteAllBytes($Destination,$bytes[0..3]);throw 'interrupted rollback copy'}};& $script:OriginalCopy $Source $Destination $Hash}}
        Reject {Rollback $f $receipt} 'interrupted rollback';ResetFaults
        Rollback $f $receipt -RecoverIncomplete|Out-Null;AssertBaseline $f $before
        Require ((Hash $receipt.ReceiptPath) -eq $receipt.ReceiptSha256) 'Recovery altered immutable receipt.'
    }
    Check 'release and false native packaging declarations hold' {
        $f=Fixture;$input=Get-Content -LiteralPath $InputManifestPath -Raw|ConvertFrom-Json -AsHashtable
        $input.Status='release';$path=Join-Path $f.Root 'release-input.json';Write-Text $path ($input|ConvertTo-Json -Depth 100)
        Reject {New-ColonyPackage -InputManifestPath $path -ExpectedInputManifestSha256 (Hash $path) -OutputRoot (Join-Path $f.Root 'release-package')} 'Release blocked'
        $input.Status='candidate';$input.NativeQualified=$true;Write-Text $path ($input|ConvertTo-Json -Depth 100)
        Reject {New-ColonyPackage -InputManifestPath $path -ExpectedInputManifestSha256 (Hash $path) -OutputRoot (Join-Path $f.Root 'false-package')} 'False native certification'
    }
    Check 'reproducible archives contain complete relative allowlist and no private assets' {
        $f=Fixture;$a=New-ColonyPackage -InputManifestPath $InputManifestPath -ExpectedInputManifestSha256 $ExpectedInputManifestSha256 -OutputRoot (Join-Path $f.Root 'package-a')
        $b=New-ColonyPackage -InputManifestPath $InputManifestPath -ExpectedInputManifestSha256 $ExpectedInputManifestSha256 -OutputRoot (Join-Path $f.Root 'package-b')
        Require ($a.Sha256 -ceq $b.Sha256) 'Equivalent pinned inputs did not reproduce archive bytes.'
        $zip=[IO.Compression.ZipFile]::OpenRead($a.Archive)
        try {Require (@($zip.Entries|Where-Object FullName -like '*.craft').Count -eq $script:TemplateCount) 'Craft set incomplete.';Require (@($zip.Entries|Where-Object FullName -like '*.manifest.json').Count -eq $script:TemplateCount) 'Template manifest set incomplete.';Require (@($zip.Entries|Where-Object FullName -match 'harness|\.sfs$|\.log$|\.pdb$|token|request').Count -eq 0) 'Private/dev asset shipped.';$reader=[IO.StreamReader]::new($zip.GetEntry('package.json').Open());$manifest=$reader.ReadToEnd();$reader.Dispose();Require ($manifest -notmatch '[A-Za-z]:\\|[A-Za-z]:/|Users[\\/]griff') 'Absolute/private path in public manifest.'} finally{$zip.Dispose()}
    }
    Write-Host "$($script:Checks.Count)/$($script:Checks.Count) disposable filesystem checks passed. No live process or game API was queried."
} finally {
    ResetFaults
    & $module { Set-Item Function:script:Get-ColonyTargetProcesses $script:OriginalProcess; Set-Item Function:script:Test-ColonyPrerequisites $script:OriginalPrerequisites; Set-Item Function:script:Assert-ColonyPrerequisites $script:OriginalPrerequisiteAssertion }
    $result=Join-Path $fixtureBase ('validation-'+[guid]::NewGuid().ToString('N')+'.json')
    Write-Text $result (@{Scope='disposable-filesystem-fixtures-only';ArchiveSha256=$ExpectedArchiveSha256;Checks=@($script:Checks)}|ConvertTo-Json -Depth 10)
    Write-Host "Fixture report: $result"
}
