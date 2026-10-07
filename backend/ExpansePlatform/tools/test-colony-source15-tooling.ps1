[CmdletBinding()]
param([string]$ReviewedProviderRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Expanse.Colony.Release.psm1') -Force
$module=Get-Module Expanse.Colony.Release
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=Join-Path $repo ('artifacts/colony-release-fixtures/source15-tools-'+[guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($output)
& $module {
    param($Repo,$Output,$ReviewedProviderRoot)
    $checks=[Collections.Generic.List[object]]::new()
    function Require($Condition,$Reason){if(-not $Condition){throw $Reason}}
    function Reject([scriptblock]$Body,$Pattern){$held=$false;try{& $Body|Out-Null}catch{if($_.Exception.Message -notmatch $Pattern){throw};$held=$true};Require $held 'Expected refusal missing.'}
    function Check($Name,[scriptblock]$Body){& $Body;$checks.Add(@{Name=$Name;Passed=$true});Write-Host "PASS $Name"}
    function Text($Path,$Value){$null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path));[IO.File]::WriteAllText($Path,$Value)}
    $package=Join-Path $Repo 'package'
    Check 'legacy profile retains exact immutable14 file names' {
        $old=Read-ColonyJson (Join-Path $Repo 'artifacts/colony-release-package-20261003-h/payload/package.json')
        Require ((Get-ColonyPayloadProfile $old) -ceq 'legacy14') 'Old profile changed.'
        Assert-ColonyExactPaths @($old.Files|ForEach-Object Path) (Get-ColonyPayloadPaths legacy14)
        Require (@(Get-ColonyPayloadPaths legacy14).Count -eq 63) 'Legacy size changed.'
        Assert-ColonyCandidate $old (Join-Path $Repo 'artifacts/colony-release-package-20261003-h/payload')
    }
    Check 'immutable14 ZIP verifies with profile-aware extraction into new workspace tree' {
        $archive=Join-Path $Repo 'artifacts/colony-release-package-20261003-h/colony-candidate14-source14-20261003.zip'
        $manifest=Expand-ColonyVerifiedArchive $archive (Get-ColonyHash $archive) (Join-Path $Output 'LegacyArchive')
        Require ((Get-ColonyPayloadProfile $manifest) -ceq 'legacy14') 'Legacy archive profile changed.'
    }
    Check 'source15 complete allowlist is69 files and11 crafts' {
        $paths=@(Get-ColonyPayloadPaths source15);Require ($paths.Count -eq 69) 'Source15 size changed.'
        Require (@($paths|Where-Object {$_ -like '*.craft'}).Count -eq 11) 'Craft set changed.'
        foreach($path in @('GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll','GameData/ExpanseWorldBridge/Optional/BrpColony.cfg')){
            Reject {Assert-ColonyExactPaths @($paths|Where-Object {$_ -cne $path}) $paths} 'allowlist'
        }
        Reject {Get-ColonyPayloadPaths 'future'} 'profile'
        Reject {Assert-ColonyExactPaths ($paths+'GameData/ExpanseWorldBridge/Plugins/BackgroundResourceProcessing.dll') $paths} 'allowlist'
    }
    $catalogPath=Join-Path $package 'GameData/ExpanseWorldBridge/Templates/colony-template-catalog.json'
    Check 'all11 catalog quote and engineering records equal individual manifests' {Assert-ColonySourceCatalog (Read-ColonyJson $catalogPath) $package}
    Check 'unlisted malformed duplicate and oversized provenance lists hold' {
        $c=Read-ColonyJson $catalogPath;$c.Remove('AdditionalSourcePartCacheHashes');Reject {Assert-ColonySourceCatalog $c $package} 'not explicitly permitted'
        foreach($value in @('not-an-array',@($c.PartConfigurationHash),@('bad'),@(for($i=0;$i -lt 129;$i++){'f'*64}))){$c.AdditionalSourcePartCacheHashes=$value;Reject {Assert-ColonySourceCatalog $c $package} 'bounded array|Invalid or duplicated'}
    }
    Check 'changed displayed cash or engineering terms hold even with unchanged Hash' {
        $c=Read-ColonyJson $catalogPath;$c.Templates[-1].BuildFunds++;Reject {Assert-ColonySourceCatalog $c $package} 'terms differ'
        $c=Read-ColonyJson $catalogPath;$c.Templates[-1].OperatingLimits.RefillAuthorized=$true;Reject {Assert-ColonySourceCatalog $c $package} 'terms differ'
    }
    $optionalPayload=Join-Path $Output 'OptionalMetadata';$optionalRelative='GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll'
    $compiled=Join-Path $Repo 'src/Expanse.BrpColony/bin/Release/net481/Expanse.BrpColony.dll';$compiledHash=Get-ColonyHash $compiled
    Copy-ColonyVerified $compiled (Join-Path $optionalPayload $optionalRelative) $compiledHash
    $configRelative='GameData/ExpanseWorldBridge/Optional/BrpColony.cfg';$configSource=Join-Path $package $configRelative
    Copy-ColonyVerified $configSource (Join-Path $optionalPayload $configRelative) (Get-ColonyHash $configSource)
    $basis=@{AssemblySha256=$compiledHash;ProofSha256='a'*64;MissingDependencyPolicy='stock-loader-exclusion-and-known-provider-absent-integration-hold';ChangedProviderPolicy='preflight-refusal-before-install-or-manual-start';ChangedIntegrationPolicy='preflight-refusal-before-install-or-manual-start'}
    $optionalManifest=@{OptionalIntegrationBasis=$basis;Provenance=@(@{Id='optional-source-proof';Sha256='a'*64})}
    Check 'actual compiled optional PE has net481 and four exact loader dependencies' {Assert-ColonyOptionalPayload $optionalManifest $optionalPayload}
    Check 'changed optional bytes or unconditional converter patch hold' {
        $bad=@{OptionalIntegrationBasis=$basis.Clone();Provenance=$optionalManifest.Provenance};$bad.OptionalIntegrationBasis.AssemblySha256='b'*64;Reject {Assert-ColonyOptionalPayload $bad $optionalPayload} 'SHA256'
        Text (Join-Path $optionalPayload $configRelative) '@BACKGROUND_CONVERTER { @adapter = ExpanseColonyProportionalAdapter }';Reject {Assert-ColonyOptionalPayload $optionalManifest $optionalPayload} 'SHA256';Remove-Item -LiteralPath (Join-Path $optionalPayload $configRelative);Copy-ColonyVerified $configSource (Join-Path $optionalPayload $configRelative) (Get-ColonyHash $configSource)
    }
    Check 'missing transitive loader dependency metadata holds' {
        $script:MetadataFixture=Get-ColonyAssemblyMetadata (Join-Path $optionalPayload $optionalRelative);$script:MetadataFixture.Dependencies=@($script:MetadataFixture.Dependencies|Where-Object Name -ne 'KSPBurst')
        $saved=(Get-Command Get-ColonyAssemblyMetadata).ScriptBlock
        try{function script:Get-ColonyAssemblyMetadata($Path){$script:MetadataFixture};Reject {Assert-ColonyOptionalPayload $optionalManifest $optionalPayload} 'metadata is incomplete'}finally{Set-Item Function:script:Get-ColonyAssemblyMetadata $saved}
    }
    if($ReviewedProviderRoot){
        $ReviewedProviderRoot=Get-ColonyAbsolute $ReviewedProviderRoot
        Check 'actual reviewed provider hashes and full GameData PE metadata scan are read-only' {
            $result=Assert-ColonyPrerequisites $ReviewedProviderRoot $compiledHash $false
            Require ($result.Providers.Count -ge 5) 'Actual provider rows missing.'
            Write-ColonyJson (Join-Path $Output 'actual-provider-preflight.json') $result
        }
        Check 'actual two-int constructor decodes zero revision and rejects incorrect or unknown signatures' {
            $path=Join-ColonyPath $ReviewedProviderRoot 'GameData/000_Harmony/HarmonyInstallChecker.dll'
            $stream=[IO.File]::OpenRead($path);$pe=[System.Reflection.PortableExecutable.PEReader]::new($stream)
            try{
                $reader=[System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe);$tested=$false
                foreach($handle in $reader.GetAssemblyDefinition().GetCustomAttributes()){
                    $attribute=$reader.GetCustomAttribute($handle);if($attribute.Constructor.Kind -ne 'MemberReference'){continue}
                    $member=$reader.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor);if($member.Parent.Kind -ne 'TypeReference'){continue}
                    $type=$reader.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$member.Parent)
                    if($reader.GetString($type.Name) -cne 'KSPAssembly'){continue}
                    Require ([Convert]::ToHexString($reader.GetBlobBytes($member.Signature)) -ceq '2003010E0808') 'Reviewed overload changed.'
                    $row=Read-ColonyLoaderAttribute '2003010E0808' ($reader.GetBlobReader($attribute.Value))
                    Require ($row.Name -ceq 'HarmonyKSP' -and $row.Version -ceq '1.0.0') 'Revision normalization wrong.'
                    Reject {Read-ColonyLoaderAttribute '2004010E080808' ($reader.GetBlobReader($attribute.Value))} 'Malformed or unreviewed'
                    Reject {Read-ColonyLoaderAttribute '2003010E0908' ($reader.GetBlobReader($attribute.Value))} 'constructor signature'
                    $tested=$true
                }
                Require $tested 'Reviewed overload fixture absent.'
            }finally{$pe.Dispose();$stream.Dispose()}
        }
    }
    $originalProviders=(Get-Command Get-ColonyKnownProviders).ScriptBlock
    $originalMetadata=(Get-Command Get-ColonyAssemblyMetadata).ScriptBlock
    $originalProcesses=(Get-Command Get-ColonyTargetProcesses).ScriptBlock
    $originalPrerequisites=(Get-Command Assert-ColonyPrerequisites).ScriptBlock
    try{
        # Only fake text files; provider lookup/PE observations are mocked inside
        # this test module. No live roots, process query or product assembly load.
        $script:FixtureRoot=Join-Path $Output 'Providers';$script:ProviderRows=@(& $originalProviders)
        $script:ProviderMetadata=@{}
        foreach($p in $script:ProviderRows){$path=Join-Path $script:FixtureRoot $p.Path;Text $path ('FAKE '+$p.Name);$p.Sha256=Get-ColonyHash $path;$script:ProviderMetadata[[IO.Path]::GetFullPath($path)]=@{Name=$p.Name;Version=$p.Version;Identities=@(if($p.LoaderName -and $p.LoaderVersion -ne '0.0.0'){@{Name=$p.LoaderName;Version=$p.LoaderVersion}})}}
        $optional=Join-Path $script:FixtureRoot 'GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll';Text $optional 'FAKE OPTIONAL';$optionalHash=Get-ColonyHash $optional
        Copy-ColonyVerified $configSource (Join-Path $script:FixtureRoot $configRelative) (Get-ColonyHash $configSource)
        $script:ProviderMetadata[[IO.Path]::GetFullPath($optional)]=@{Name='Expanse.BrpColony';Version='1.0.0.0';Identities=@()}
        function script:Get-ColonyKnownProviders {@($script:ProviderRows)}
        function script:Get-ColonyAssemblyMetadata($Path){$script:ProviderMetadata[[IO.Path]::GetFullPath($Path)]}
        function script:Get-ColonyTargetProcesses($Roots){@()}
        Check 'read-only prerequisite accepts exact fixture identities and hashes' {Require ((Test-ColonyPrerequisites $script:FixtureRoot $optionalHash).NativeQualified -eq $false) 'Preflight certified runtime.'}
        Check 'changed BRP bytes hold before any target mutation' {
            $p=$script:ProviderRows[0];$path=Join-Path $script:FixtureRoot $p.Path;Text $path 'CHANGED BRP';Reject {Test-ColonyPrerequisites $script:FixtureRoot $optionalHash} 'SHA256';Text $path ('FAKE '+$p.Name)
        }
        Check 'changed BRP version and loader registration hold' {
            $p=$script:ProviderRows[0];$m=$script:ProviderMetadata[[IO.Path]::GetFullPath((Join-Path $script:FixtureRoot $p.Path))];$m.Version='0.2.8.0';Reject {Test-ColonyPrerequisites $script:FixtureRoot $optionalHash} 'version';$m.Version=$p.Version;$m.Identities[0].Version='0.2.8';Reject {Test-ColonyPrerequisites $script:FixtureRoot $optionalHash} 'registration';$m.Identities[0].Version=$p.LoaderVersion
        }
        Check 'missing required dependencies hold' {
            foreach($p in $script:ProviderRows){$path=Join-Path $script:FixtureRoot $p.Path;Remove-Item -LiteralPath $path;Reject {Test-ColonyPrerequisites $script:FixtureRoot $optionalHash} 'absent';Text $path ('FAKE '+$p.Name)}
        }
        Check 'missing optional permits preapply only and changed optional refuses' {
            Remove-Item -LiteralPath $optional;Reject {Test-ColonyPrerequisites $script:FixtureRoot $optionalHash} 'absent';Assert-ColonyPrerequisites $script:FixtureRoot $optionalHash $false|Out-Null
            Text $optional 'MISMATCHED OPTIONAL';Reject {Assert-ColonyPrerequisites $script:FixtureRoot $optionalHash $false} 'SHA256';Text $optional 'FAKE OPTIONAL'
        }
        Check 'missing or changed installed optional config holds manual start' {
            $path=Join-Path $script:FixtureRoot $configRelative;Remove-Item -LiteralPath $path;Reject {Test-ColonyPrerequisites $script:FixtureRoot $optionalHash} 'config is absent'
            Text $path 'CHANGED CONFIG';Reject {Test-ColonyPrerequisites $script:FixtureRoot $optionalHash} 'SHA256';Remove-Item -LiteralPath $path;Copy-ColonyVerified $configSource $path (Get-ColonyHash $configSource)
        }
        Check 'renamed duplicate provider or optional cannot bypass expected path' {
            $path=Join-Path $script:FixtureRoot 'GameData/Else/renamed.dll';Text $path 'FAKE DUPLICATE';$script:ProviderMetadata[[IO.Path]::GetFullPath($path)]=@{Name='BackgroundResourceProcessing';Version='0.2.7.0';Identities=@()};Reject {Test-ColonyPrerequisites $script:FixtureRoot $optionalHash} 'Duplicate';$script:ProviderMetadata[[IO.Path]::GetFullPath($path)].Name='Expanse.BrpColony';Reject {Test-ColonyPrerequisites $script:FixtureRoot $optionalHash} 'Duplicate';Remove-Item -LiteralPath $path
        }
        function script:Assert-ColonyPrerequisites($GameRoot,$ExpectedOptionalAssemblySha256,$RequireOptional){@{Status='fixture-provider-only'}}
        foreach($prior in @($false,$true)){
            Check "exact rollback removes new optional files or restores prior bytes (prior=$prior)" {
                $root=Join-Path $Output ([guid]::NewGuid().ToString('N'));$roots=@{};foreach($name in @('Game','App','State','Backup')){$roots[$name]=Join-Path $root $name;$null=[IO.Directory]::CreateDirectory($roots[$name])}
                Text (Join-Path $roots.State 'state.json') 'BASELINE STATE';Text (Join-Path $roots.Game 'saves/Fixture/persistent.sfs') 'BASELINE SAVE'
                $paths=@('GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll','GameData/ExpanseWorldBridge/Optional/BrpColony.cfg')
                if($prior){foreach($path in $paths){Text (Join-Path $roots.Game $path) ('OLD '+$path)}}
                $before=Get-ColonyScopeInventory $roots 'saves/Fixture';$transaction=Join-Path $roots.Backup 'transaction';$ops=@()
                foreach($path in $paths){$target=Join-Path $roots.Game $path;$old=if($prior){Get-ColonyHash $target}else{$null};$backup=if($prior){'before/'+$path}else{$null};if($prior){Copy-ColonyVerified $target (Join-Path $transaction $backup) $old};Text (Join-Path $transaction ('stage/'+$path)) ('NEW '+$path);$new=Get-ColonyHash (Join-Path $transaction ('stage/'+$path));$ops+=@{Scope='Game';Path=$path;BeforeSha256=$old;BeforeBackup=$backup;AfterSha256=$new;AfterSource='stage/'+$path;TemporaryPath=$path+'.expanse-test.tmp'}}
                foreach($scope in @('Save','State')){$source=if($scope -eq 'Save'){Join-Path $roots.Game 'saves/Fixture'}else{$roots.State};foreach($file in $before[$scope]){Copy-ColonyVerified (Join-Path $source $file.Path) (Join-Path $transaction "before/$scope/$($file.Path)") $file.Sha256}}
                $receipt=@{Roots=$roots;Before=$before;Operations=$ops;SaveDirectory='saves/Fixture';ReleaseId='fixture-source15';PayloadProfile='source15';OptionalAssemblySha256='a'*64}
                foreach($op in $ops){Set-ColonyInstalledFile $roots $transaction $op};Restore-ColonyTransaction $receipt $transaction
                Require ((Get-ColonyJsonKey (Get-ColonyScopeInventory $roots 'saves/Fixture')) -ceq (Get-ColonyJsonKey $before)) 'Optional rollback baseline differs.'
            }
        }
        Check 'unrecognized stale optional files hold instead of silent cleanup' {
            $inventory=@{'GameData/ExpanseWorldBridge'=@(@{Path='Plugins/Expanse.BrpColony.dll'});'GameData/ExpanseFoundations'=@();App=@()};$manifest=@{Files=@(Get-ColonyPayloadPaths legacy14|ForEach-Object {@{Path=$_}})};Reject {Assert-ColonyInstallScope $inventory $manifest} 'Unexpected managed file'
        }
    }finally{
        Set-Item Function:script:Get-ColonyKnownProviders $originalProviders;Set-Item Function:script:Get-ColonyAssemblyMetadata $originalMetadata;Set-Item Function:script:Get-ColonyTargetProcesses $originalProcesses;Set-Item Function:script:Assert-ColonyPrerequisites $originalPrerequisites
        Write-ColonyJson (Join-Path $Output 'validation.json') @{Scope='source-only-helper-and-disposable-filesystem; no archive/build/native/install';Checks=@($checks)}
    }
    Write-Host "$($checks.Count)/$($checks.Count) bounded source15 tooling checks passed. Evidence: $Output"
} $repo $output $ReviewedProviderRoot
