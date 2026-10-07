[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{2,70}$')][string]$ReleaseId,
    [ValidateSet('legacy14','source15')][string]$PayloadProfile='legacy14',
    [Parameter(Mandatory)][string]$FreezeManifestPath,[Parameter(Mandatory)][string]$ExpectedFreezeManifestSha256,
    [Parameter(Mandatory)][string]$BuildEvidencePath,[Parameter(Mandatory)][string]$ExpectedBuildEvidenceSha256,
    [Parameter(Mandatory)][string]$UiEvidencePath,[Parameter(Mandatory)][string]$ExpectedUiEvidenceSha256,
    [string]$ToolEvidencePath,[string]$ExpectedToolEvidenceSha256,
    [string]$NativeReviewPath,[string]$ExpectedNativeReviewSha256,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Expanse.Colony.Release.psm1') -Force
$module = Get-Module Expanse.Colony.Release
# The initial local input is not release authority. Review and pin its hash before package-colony.
& $module {
    param($Arguments)
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $freezePath = Get-ColonyAbsolute $Arguments.FreezeManifestPath
    Assert-ColonyHash $freezePath $Arguments.ExpectedFreezeManifestSha256
    $freeze = Read-ColonyJson $freezePath
    $freezeRoot = [IO.Path]::GetDirectoryName($freezePath)
    $output = Get-ColonyAbsolute $Arguments.OutputRoot
    if (Test-Path -LiteralPath $output) { throw 'Snapshot output must be new.' }
    $null = [IO.Directory]::CreateDirectory($output)
    $sources = [Collections.Generic.List[object]]::new()
    $profile=if($Arguments.ContainsKey('PayloadProfile')){$Arguments.PayloadProfile}else{'legacy14'}
    foreach ($path in Get-ColonyPayloadPaths $profile) {
        $transform = 'None'
        if ($path -like 'GameData/ExpanseWorldBridge/*') {
            $artifact = @($freeze.Artifacts | Where-Object { $_.Path.Replace('\','/') -ceq $path })
            if ($artifact.Count -ne 1) { throw "Missing frozen production input: $path" }
            $source = Join-ColonyPath $freezeRoot $path
            Assert-ColonyHash $source $artifact[0].Sha256
        } elseif ($path -like 'GameData/ExpanseFoundations/*') {
            $source = Join-ColonyPath ([IO.Path]::GetFullPath((Join-Path $repo '../ExpanseFoundations'))) $path
            if ($path.EndsWith('.md')) { $transform='PublicDocumentation' }
        } elseif ($path -like 'Apps/*') {
            $parts = $path.Split('/',3)
            $tfm = if ($parts[1] -eq 'Host') { 'net8.0' } else { 'net8.0-windows' }
            $runtime = Join-ColonyPath $repo "src/Expanse.Clock.$($parts[1])/bin/Release/$tfm"
            $source = Join-ColonyPath $runtime $parts[2]
        } elseif ($path -like 'Docs/*') { $source=Join-ColonyPath $repo ('docs/' + $path.Substring(5));$transform='PublicDocumentation' }
        else { $source=Join-ColonyPath $repo ('tools/' + $path.Substring(6)) }
        $hash = Get-ColonyHash $source
        $snapshot = Join-ColonyPath $output "snapshot/$path"
        Copy-ColonyVerified $source $snapshot $hash
        $sources.Add([ordered]@{Path=$path;Source=$snapshot;Sha256=$hash;Transform=$transform;OriginalSource=$source})
    }
    # Reject unexpected runtime DLLs instead of quietly omitting a new dependency or test assembly.
    foreach ($app in @('Host','Manager')) {
        $tfm=if ($app -eq 'Host') {'net8.0'} else {'net8.0-windows'}
        $runtime=Join-ColonyPath $repo "src/Expanse.Clock.$app/bin/Release/$tfm"
        foreach ($file in Get-ColonyInventory $runtime | Where-Object Path -like '*.dll') {
            $mapped="Apps/$app/$($file.Path)"
            if ($mapped -cnotin @(Get-ColonyPayloadPaths $profile)) { throw "Unexpected runtime DLL: $mapped" }
        }
    }
    $compiledFoundation=Get-ColonyAbsolute (Join-Path $repo '../ExpanseFoundations/src/bin/Release/net472/ExpanseFoundations.dll')
    Assert-ColonyHash $compiledFoundation $script:FoundationHash
    $domainRoot=Join-ColonyPath $repo 'src/Expanse.Domain'
    $net472=Join-ColonyPath $domainRoot 'bin/Release/net472/Expanse.Domain.dll'
    $net8=Join-ColonyPath $domainRoot 'bin/Release/net8.0/Expanse.Domain.dll'
    $frozenDomain=Join-ColonyPath $freezeRoot 'GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll'
    Assert-ColonyHash $net472 (Get-ColonyHash $frozenDomain)
    foreach($app in @('Host','Manager')){Assert-ColonyHash (Join-ColonyPath $output "snapshot/Apps/$app/Expanse.Domain.dll") (Get-ColonyHash $net8)}
    $documents472=@(Get-ColonyCompiledDocuments $net472 ([IO.Path]::ChangeExtension($net472,'.pdb')) $domainRoot)
    $documents8=@(Get-ColonyCompiledDocuments $net8 ([IO.Path]::ChangeExtension($net8,'.pdb')) $domainRoot)
    if(($documents472|ConvertTo-Json -Depth 10 -Compress) -cne ($documents8|ConvertTo-Json -Depth 10 -Compress)){throw 'Frozen net472 and net8 Domain were compiled from different source documents/checksums.'}
    $proofPath=Join-Path $output 'domain-source-proof.json'
    Write-ColonyJson $proofPath ([ordered]@{SchemaVersion=1;FrozenNet472Sha256=Get-ColonyHash $frozenDomain;Net8Sha256=Get-ColonyHash $net8;Net472PdbSha256=Get-ColonyHash ([IO.Path]::ChangeExtension($net472,'.pdb'));Net8PdbSha256=Get-ColonyHash ([IO.Path]::ChangeExtension($net8,'.pdb'));SourceChecksumsMatch=$true;CurrentSourceMatches=$true;Documents=$documents472})
    $provenance=[Collections.Generic.List[object]]::new()
    $provenance.Add([ordered]@{Id='domain-source-proof';Kind='source';Source=$proofPath;Sha256=Get-ColonyHash $proofPath})
    $appAssemblies=[Collections.Generic.List[object]]::new()
    foreach($component in @('Host','Manager','Core','Foundation')){
        if($component -eq 'Foundation'){$sourceRoot=Get-ColonyAbsolute (Join-Path $repo '../ExpanseFoundations');$assembly=$compiledFoundation;$destinations=@('GameData/ExpanseFoundations/Plugins/ExpanseFoundations.dll')}
        else{$sourceRoot=Join-ColonyPath $repo "src/Expanse.Clock.$component";$tfm=if($component -eq 'Manager'){'net8.0-windows'}else{'net8.0'};$assembly=Join-ColonyPath $sourceRoot "bin/Release/$tfm/Expanse.Clock.$component.dll";$destinations=if($component -eq 'Core'){@('Apps/Host/Expanse.Clock.Core.dll','Apps/Manager/Expanse.Clock.Core.dll')}else{@("Apps/$component/Expanse.Clock.$component.dll")}}
        $pdb=[IO.Path]::ChangeExtension($assembly,'.pdb');$documents=@(Get-ColonyCompiledDocuments $assembly $pdb $sourceRoot)
        foreach($destination in $destinations){Assert-ColonyHash (Join-ColonyPath $output "snapshot/$destination") (Get-ColonyHash $assembly);$appAssemblies.Add([ordered]@{Path=$destination;Sha256=Get-ColonyHash $assembly;PdbSha256=Get-ColonyHash $pdb;Documents=$documents})}
    }
    $appProofPath=Join-Path $output 'app-source-proof.json';Write-ColonyJson $appProofPath ([ordered]@{SchemaVersion=1;CurrentCompiledSourceMatches=$true;Assemblies=@($appAssemblies|Sort-Object {$_.Path})})
    $provenance.Add([ordered]@{Id='app-source-proof';Kind='source';Source=$appProofPath;Sha256=Get-ColonyHash $appProofPath})
    if($profile -eq 'source15'){
        $sourceRoot=Join-ColonyPath $repo 'src/Expanse.BrpColony';$assembly=Join-ColonyPath $sourceRoot 'bin/Release/net481/Expanse.BrpColony.dll';$pdb=[IO.Path]::ChangeExtension($assembly,'.pdb')
        $destination='GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll'
        Assert-ColonyHash (Join-ColonyPath $output "snapshot/$destination") (Get-ColonyHash $assembly)
        $documents=@(Get-ColonyCompiledDocuments $assembly $pdb $sourceRoot)
        $optionalProofPath=Join-Path $output 'optional-source-proof.json'
        Write-ColonyJson $optionalProofPath ([ordered]@{SchemaVersion=1;CurrentCompiledSourceMatches=$true;Path=$destination;Sha256=Get-ColonyHash $assembly;PdbSha256=Get-ColonyHash $pdb;Documents=$documents})
        $provenance.Add([ordered]@{Id='optional-source-proof';Kind='source';Source=$optionalProofPath;Sha256=Get-ColonyHash $optionalProofPath})
    }
    foreach ($entry in @(@{Id='bridge-freeze';Kind='freeze';Source=$freezePath;Sha256=$Arguments.ExpectedFreezeManifestSha256},@{Id='app-build';Kind='build';Source=$Arguments.BuildEvidencePath;Sha256=$Arguments.ExpectedBuildEvidenceSha256},@{Id='ui-fixture';Kind='evidence';Source=$Arguments.UiEvidencePath;Sha256=$Arguments.ExpectedUiEvidenceSha256})) {
        $entry.Source=Get-ColonyAbsolute $entry.Source;Assert-ColonyHash $entry.Source $entry.Sha256
        $snapshot=Join-ColonyPath $output "provenance/$($entry.Id).txt";Copy-ColonyVerified $entry.Source $snapshot $entry.Sha256
        $provenance.Add([ordered]@{Id=$entry.Id;Kind=$entry.Kind;Source=$snapshot;Sha256=$entry.Sha256;OriginalSource=$entry.Source})
    }
    foreach($optional in @(@{PathKey='ToolEvidencePath';HashKey='ExpectedToolEvidenceSha256';Id='filesystem-fixture'},@{PathKey='NativeReviewPath';HashKey='ExpectedNativeReviewSha256';Id='development-freeze-review'})){
        $hasPath=$Arguments.ContainsKey($optional.PathKey);$hasHash=$Arguments.ContainsKey($optional.HashKey)
        if($hasPath -ne $hasHash){throw 'Optional evidence path and expected hash must be supplied together.'}
        if($hasPath){$source=Get-ColonyAbsolute $Arguments[$optional.PathKey];$hash=$Arguments[$optional.HashKey];Assert-ColonyHash $source $hash;$snapshot=Join-ColonyPath $output "provenance/$($optional.Id).json";Copy-ColonyVerified $source $snapshot $hash;$provenance.Add([ordered]@{Id=$optional.Id;Kind='evidence';Source=$snapshot;Sha256=$hash;OriginalSource=$source})}
    }
    # Local source inventory includes actual source, project and config bytes, never bin/obj or saves.
    $sourceRecords=[Collections.Generic.List[object]]::new()
    $areas=@('src/Expanse.Domain','src/Expanse.WorldBridge','src/Expanse.Clock.Core','src/Expanse.Clock.Host','src/Expanse.Clock.Manager')
    if($profile -eq 'source15'){$areas+='src/Expanse.BrpColony'}
    foreach ($area in $areas) {
        $root=Join-ColonyPath $repo $area
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs','.csproj','.xaml','.cfg') } | Sort-Object FullName) {
            Assert-ColonyPath $file.FullName
            $sourceRecords.Add([ordered]@{Path=$file.FullName.Substring($repo.Length+1).Replace('\','/');Sha256=Get-ColonyHash $file.FullName})
        }
    }
    $inventory=Join-Path $output 'source-inventory.json';Write-ColonyJson $inventory @($sourceRecords)
    $provenance.Add([ordered]@{Id='source-inventory';Kind='source';Source=$inventory;Sha256=Get-ColonyHash $inventory})
    $inputPath=Join-Path $output 'input-manifest.json'
    Write-ColonyJson $inputPath ([ordered]@{SchemaVersion=1;ReleaseId=$Arguments.ReleaseId;Status='candidate';PayloadProfile=$profile;Files=@($sources);Provenance=@($provenance)})
    [pscustomobject]@{InputManifestPath=$inputPath;Sha256=Get-ColonyHash $inputPath;Status='candidate';Instruction='Review this local snapshot and pin its input hash before packaging. Frozen Bridge source provenance is its selected immutable manifest, not later source.'}
} $PSBoundParameters
