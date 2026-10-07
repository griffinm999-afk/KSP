Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Authority is the caller-pinned input/archive/receipt hash, never a mutable status file.
# This policy intentionally cannot certify the currently unqualified native product.
$script:QualificationPolicy = 'candidate-only-native-gates-open-v1'
$script:TemplateIds = @('agriculture-duna-v1','cultivation-duna-v1','cultivation-feeds-v1','fertilizer-tundra-v1','housing-kpbs-v1','lamp-stock-v1','power-duna-v1','service-kpbs-v1','storage-kpbs-v1','wolf-hoppers-v1')
$script:FoundationHash = '1D5138568042C9F510A11C6F580EC6652BFDF883EAC7DE32E97688CF372799EB'
$script:OptionalConfigHash = '20758B21329D81AE754EDD08569782D15E67347B0DB20FC9419DBCA6A893B042'

if (-not ('ColonyReleaseFileIdentity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ColonyReleaseFileIdentity {
    [StructLayout(LayoutKind.Sequential)] struct Info {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;
        public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
    }
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle,out Info info);
    public static uint Links(string path) {
        using(var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
            Info info; if(!GetFileInformationByHandle(stream.SafeFileHandle,out info)) throw new System.ComponentModel.Win32Exception();
            return info.Links;
        }
    }
}
'@
}

function Get-ColonyHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Assert-ColonyHash([string]$Path,[string]$Expected) {
    if ($Expected -notmatch '^[a-fA-F0-9]{64}$' -or (Get-ColonyHash $Path) -cne $Expected.ToUpperInvariant()) { throw "SHA256 mismatch: $Path" }
}
function Assert-ColonyRelative([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -cne $Path.Replace('\','/') -or $Path.StartsWith('/') -or $Path.Contains(':') -or $Path.Contains([char]0)) { throw "Unsafe relative path: $Path" }
    foreach ($segment in $Path.Split('/')) {
        if ($segment -eq '' -or $segment -in @('.','..') -or $segment -match '[<>"|?*\x00-\x1F]' -or $segment -match '[. ]$' -or $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw "Unsafe path segment: $Path" }
    }
    $Path
}
function Get-ColonyAbsolute([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\') -or $Path -match '^[a-zA-Z]:[\\/]?$') { throw "Explicit local directory required: $Path" }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    Assert-ColonyPath $full
    $full
}
function Assert-ColonyPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.Substring(2).Contains(':')) { throw "Alternate stream forbidden: $Path" }
    $cursor = $full
    while ($cursor -and $cursor -ne [IO.Path]::GetPathRoot($cursor)) {
        $attributes=$null
        try {$attributes=[IO.File]::GetAttributes($cursor)} catch [IO.FileNotFoundException] {} catch [IO.DirectoryNotFoundException] {}
        if ($null -ne $attributes) {
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point forbidden: $cursor" }
            if (($attributes -band [IO.FileAttributes]::Directory) -eq 0 -and [ColonyReleaseFileIdentity]::Links($cursor) -ne 1) { throw "Hard-linked file forbidden: $cursor" }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Join-ColonyPath([string]$Root,[string]$Relative) {
    $null = Assert-ColonyRelative $Relative
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $full.StartsWith($Root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Path escaped its root.' }
    Assert-ColonyPath $full
    $full
}
function Test-ColonyNested([string]$A,[string]$B) { $A.Equals($B,[StringComparison]::OrdinalIgnoreCase) -or $A.StartsWith($B + '\',[StringComparison]::OrdinalIgnoreCase) }
function Assert-ColonyRoots($Roots) {
    $names = @('Game','App','State','Backup')
    foreach ($a in $names) { foreach ($b in $names) { if ($a -ne $b -and (Test-ColonyNested $Roots[$a] $Roots[$b])) { throw "$a root overlaps $b root." } } }
    foreach ($name in $names) { if (-not (Test-Path -LiteralPath $Roots[$name] -PathType Container)) { throw "$name root must already exist." } }
}
function Get-ColonyInventory([string]$Root) {
    Assert-ColonyPath $Root
    if (-not (Test-Path -LiteralPath $Root)) { return @() }
    $result = [Collections.Generic.List[object]]::new()
    # Walk explicitly; recursive enumeration must never follow a link.
    $queue = [Collections.Generic.Queue[string]]::new(); $queue.Enqueue($Root)
    while ($queue.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
            Assert-ColonyPath $item.FullName
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName); continue }
            $relative = $item.FullName.Substring($Root.Length).TrimStart('\','/').Replace('\','/')
            $null = Assert-ColonyRelative $relative
            $result.Add([ordered]@{ Path=$relative; Sha256=Get-ColonyHash $item.FullName; Length=$item.Length })
        }
    }
    @($result | Sort-Object { $_.Path })
}
function Get-ColonyInventoryKey($Inventory) { (@($Inventory | Sort-Object { $_.Path } | ForEach-Object { '{0}|{1}|{2}' -f $_.Path,$_.Sha256,$_.Length }) -join "`n") }
function Assert-ColonyInventory([string]$Root,$Expected) {
    if ((Get-ColonyInventoryKey (Get-ColonyInventory $Root)) -cne (Get-ColonyInventoryKey $Expected)) { throw "Inventory drift: $Root" }
}
function Write-ColonyJson([string]$Path,$Value) {
    Assert-ColonyPath $Path
    $parent = [IO.Path]::GetDirectoryName($Path)
    $null = [IO.Directory]::CreateDirectory($parent)
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary,($Value | ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary,$Path,$true)
}
function Read-ColonyJson([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable }
function Get-ColonyPayloadProfile($Manifest) {
    $profile = if ($Manifest.Contains('PayloadProfile')) { $Manifest.PayloadProfile } else { 'legacy14' }
    if ($profile -cnotin @('legacy14','source15')) { throw 'Unknown colony payload profile.' }
    $profile
}
function Get-ColonyTemplateIds([string]$Profile='legacy14') {
    if ($Profile -cnotin @('legacy14','source15')) { throw 'Unknown colony payload profile.' }
    if ($Profile -eq 'source15') { @($script:TemplateIds) + 'power-ranger-bank-v1' } else { @($script:TemplateIds) }
}
function Get-ColonyPayloadPaths([string]$Profile='legacy14') {
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($name in @('ColonyEconomy','ColonyPassengers','ColonyPlacement','ColonyPlanning')) { $paths.Add("GameData/ExpanseWorldBridge/$name.cfg") }
    foreach ($id in Get-ColonyTemplateIds $Profile) { $paths.Add("GameData/ExpanseWorldBridge/Templates/$id.craft"); $paths.Add("GameData/ExpanseWorldBridge/Templates/$id.manifest.json") }
    $paths.Add('GameData/ExpanseWorldBridge/Templates/colony-template-catalog.json')
    foreach ($name in @('Expanse.WorldBridge','Expanse.Domain')) { $paths.Add("GameData/ExpanseWorldBridge/Plugins/$name.dll") }
    if ($Profile -eq 'source15') {
        $paths.Add('GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll')
        $paths.Add('GameData/ExpanseWorldBridge/Optional/BrpColony.cfg')
        $paths.Add('Docs/COLONY-RANGER-POWER-BANK-CANDIDATE15.md')
        $paths.Add('Tools/test-colony-prerequisites.ps1')
    }
    foreach ($path in @('Plugins/ExpanseFoundations.dll','Settings.cfg','README.md','TEST-EVIDENCE.md','LICENSE','licenses/PhysicsHold-MIT.txt','ExpanseFoundations.version')) { $paths.Add("GameData/ExpanseFoundations/$path") }
    foreach ($app in @('Host','Manager')) {
        foreach ($extension in @('exe','dll','deps.json','runtimeconfig.json')) { $paths.Add("Apps/$app/Expanse.Clock.$app.$extension") }
        foreach ($name in @('Expanse.Clock.Core','Expanse.Domain')) { $paths.Add("Apps/$app/$name.dll") }
    }
    foreach ($name in @('Microsoft.Data.Sqlite','SQLitePCLRaw.batteries_v2','SQLitePCLRaw.core','SQLitePCLRaw.provider.e_sqlite3')) { $paths.Add("Apps/Host/$name.dll") }
    # Windows x64 product: all four packaged Windows native assets are retained; other OS assets are not installed.
    foreach ($rid in @('win-arm','win-arm64','win-x64','win-x86')) { $paths.Add("Apps/Host/runtimes/$rid/native/e_sqlite3.dll") }
    foreach ($name in @('COLONY-OPERATOR-GUIDE.md','COLONY-PACKAGING-INSTALL-ROLLBACK.md','ORE-EXPORT-DESIGN.md','RECOVERY-DELIVERY-USAGE.md','COLONY-PRODUCTION-INVENTORY-INTEGRATION.md','COLONY-APP-SOURCE-HANDOFF-13.md')) { $paths.Add("Docs/$name") }
    foreach ($name in @('Expanse.Colony.Release.psm1','install-colony.ps1','rollback-colony.ps1')) { $paths.Add("Tools/$name") }
    @($paths | Sort-Object)
}
function Assert-ColonyExactPaths($Paths,$Expected) {
    $actual = @($Paths | Sort-Object)
    if (@($actual | Select-Object -Unique).Count -ne $actual.Count -or ($actual -join "`n") -cne (@($Expected | Sort-Object) -join "`n")) { throw 'File allowlist is incomplete, duplicated or contains unexpected files.' }
    foreach ($path in $actual) { $null = Assert-ColonyRelative $path }
}
function Convert-ColonyPublicText([string]$Text) {
    # Documentation only. Runtime/template/config bytes are never transformed.
    [regex]::Replace($Text,'(?i)(?<![a-z0-9])[a-z]:[\\/][^\r\n"''`<>]+','<local-path>')
}
function Get-ColonyAssemblyFramework([string]$Path) {
    Assert-ColonyPath $Path
    $stream=[IO.File]::OpenRead($Path);$pe=[System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $reader=[System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        foreach($handle in $reader.GetAssemblyDefinition().GetCustomAttributes()) {
            $attribute=$reader.GetCustomAttribute($handle)
            if($attribute.Constructor.Kind -ne 'MemberReference'){continue}
            $member=$reader.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if($member.Parent.Kind -ne 'TypeReference'){continue}
            $type=$reader.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$member.Parent)
            if($reader.GetString($type.Name) -eq 'TargetFrameworkAttribute'){
                $blob=$reader.GetBlobReader($attribute.Value);$null=$blob.ReadUInt16();return $blob.ReadSerializedString()
            }
        }
        throw "Missing framework metadata: $Path"
    } finally {$pe.Dispose();$stream.Dispose()}
}
function Get-ColonyCompiledDocuments([string]$Assembly,[string]$Pdb,[string]$SourceRoot) {
    Assert-ColonyPath $Assembly;Assert-ColonyPath $Pdb
    $stream=[IO.File]::OpenRead($Pdb);$provider=[System.Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($stream)
    $assemblyStream=[IO.File]::OpenRead($Assembly);$pe=[System.Reflection.PortableExecutable.PEReader]::new($assemblyStream)
    try {
        $reader=$provider.GetMetadataReader();[byte[]]$id=$reader.DebugMetadataHeader.Id
        $entries=@($pe.ReadDebugDirectory() | Where-Object Type -eq 'CodeView')
        if($entries.Count -ne 1){throw 'Missing/ambiguous assembly PDB identity.'}
        $code=$pe.ReadCodeViewDebugDirectoryData($entries[0])
        $guidBytes=[byte[]]$id[0..15]
        if($code.Guid -ne [guid]::new($guidBytes) -or $code.Age -ne 1 -or $entries[0].Stamp -ne [BitConverter]::ToUInt32($id,16)){throw 'PDB does not match its compiled assembly.'}
        $documents=[Collections.Generic.List[object]]::new()
        foreach($handle in $reader.Documents){
            $document=$reader.GetDocument($handle);$path=Get-ColonyAbsolute ($reader.GetString($document.Name))
            if(-not (Test-ColonyNested $path $SourceRoot)){throw 'Unexpected compiler document outside component source.'}
            $relative=$path.Substring($SourceRoot.Length+1).Replace('\','/')
            if($relative -match '(^|/)obj/'){continue} # Framework/assembly attributes differ by target framework.
            $algorithm=$reader.GetGuid($document.HashAlgorithm)
            $name=switch($algorithm.ToString()){'8829d00f-11b8-4213-878b-770e8597ac16'{'SHA256'} 'ff1816ec-aa5e-4d10-87f7-6f4963833460'{'SHA1'} default{throw 'Unsupported PDB source checksum.'}}
            $hash=[Convert]::ToHexString($reader.GetBlobBytes($document.Hash))
            if((Get-FileHash -LiteralPath $path -Algorithm $name).Hash -ne $hash){throw "Compiled component source has changed: $relative"}
            $documents.Add([ordered]@{Path=$relative;Algorithm=$name;Checksum=$hash})
        }
        if(-not $documents.Count){throw 'No compiled source documents.'}
        @($documents|Sort-Object {$_.Path})
    } finally{$pe.Dispose();$assemblyStream.Dispose();$provider.Dispose();$stream.Dispose()}
}
function Get-ColonyJsonKey($Value) {
    if($Value -is [Collections.IDictionary]){
        $rows=@(foreach($key in @($Value.Keys|Sort-Object)){ (ConvertTo-Json -InputObject ([string]$key) -Compress)+':'+(Get-ColonyJsonKey $Value[$key]) })
        return '{'+($rows -join ',')+'}'
    }
    if($Value -is [Collections.IEnumerable] -and $Value -isnot [string]){
        $rows=@(foreach($row in $Value){Get-ColonyJsonKey $row});return '['+($rows -join ',')+']'
    }
    ConvertTo-Json -InputObject $Value -Compress -Depth 100
}
function Assert-ColonySourceCatalog($Catalog,[string]$PayloadRoot) {
    if($Catalog.PartConfigurationHash -notmatch '^[a-fA-F0-9]{64}$'){throw 'Invalid catalog source provenance.'}
    $hashes=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $null=$hashes.Add($Catalog.PartConfigurationHash)
    if($Catalog.Contains('AdditionalSourcePartCacheHashes')){
        $extra=$Catalog.AdditionalSourcePartCacheHashes
        if($extra -isnot [array] -or $extra.Count -gt 128){throw 'Additional source provenance must be a bounded array.'}
        foreach($hash in $extra){if($hash -isnot [string] -or $hash -notmatch '^[a-fA-F0-9]{64}$' -or -not $hashes.Add($hash)){throw 'Invalid or duplicated source provenance hash.'}}
    }
    foreach($entry in $Catalog.Templates){
        $detail=Read-ColonyJson (Join-ColonyPath $PayloadRoot "GameData/ExpanseWorldBridge/Templates/$($entry.Id).manifest.json")
        if((Get-ColonyJsonKey $entry) -cne (Get-ColonyJsonKey $detail)){throw 'Catalog and manifest quote/engineering terms differ.'}
        if($detail.PartConfigurationHash -isnot [string] -or -not $hashes.Contains($detail.PartConfigurationHash)){throw 'Template source provenance is not explicitly permitted.'}
        if($detail.CraftRelativePath -cne "$($entry.Id).craft"){throw 'Template craft path differs from the allowlist.'}
    }
}
function Read-ColonyLoaderAttribute([string]$Signature,[System.Reflection.Metadata.BlobReader]$Blob) {
    # ECMA instance .ctor, void, string then two or three Int32 arguments.
    # Stock's three-argument overload sets versionRevision to zero.
    if($Signature -cnotin @('2003010E0808','2004010E080808')){throw 'Unreviewed loader constructor signature.'}
    try{
        if($Blob.ReadUInt16() -ne 1){throw 'Malformed loader attribute.'}
        $id=$Blob.ReadSerializedString();$major=$Blob.ReadInt32();$minor=$Blob.ReadInt32()
        $revision=if($Signature -ceq '2004010E080808'){$Blob.ReadInt32()}else{0}
        if([string]::IsNullOrEmpty($id) -or $major -lt 0 -or $minor -lt 0 -or $revision -lt 0 -or $Blob.ReadUInt16() -ne 0 -or $Blob.RemainingBytes -ne 0){throw 'Unreviewed loader attribute arguments.'}
        [ordered]@{Name=$id;Version=('{0}.{1}.{2}' -f $major,$minor,$revision)}
    }catch{throw "Malformed or unreviewed loader attribute: $($_.Exception.Message)"}
}
function Get-ColonyAssemblyMetadata([string]$Path) {
    Assert-ColonyPath $Path
    $stream=[IO.File]::OpenRead($Path);$pe=[System.Reflection.PortableExecutable.PEReader]::new($stream)
    try{
        if(-not $pe.HasMetadata){return $null}
        $reader=[System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe);$assembly=$reader.GetAssemblyDefinition()
        $dependencies=[Collections.Generic.List[object]]::new();$identities=[Collections.Generic.List[object]]::new()
        foreach($handle in $assembly.GetCustomAttributes()){
            $attribute=$reader.GetCustomAttribute($handle)
            if($attribute.Constructor.Kind -ne 'MemberReference'){continue}
            $member=$reader.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if($member.Parent.Kind -ne 'TypeReference'){continue}
            $type=$reader.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$member.Parent);$name=$reader.GetString($type.Name)
            if($name -cnotin @('KSPAssembly','KSPAssemblyDependency')){continue}
            if($reader.GetString($type.Namespace) -cne '' -or $type.ResolutionScope.Kind -ne 'AssemblyReference' -or $reader.GetString($reader.GetAssemblyReference([System.Reflection.Metadata.AssemblyReferenceHandle]$type.ResolutionScope).Name) -cne 'Assembly-CSharp'){throw 'Unreviewed loader metadata identity.'}
            if($reader.GetString($member.Name) -cne '.ctor'){throw 'Unreviewed loader constructor member.'}
            $signature=[Convert]::ToHexString($reader.GetBlobBytes($member.Signature))
            $row=Read-ColonyLoaderAttribute $signature ($reader.GetBlobReader($attribute.Value))
            if($name -eq 'KSPAssembly'){$identities.Add($row)}else{$dependencies.Add($row)}
        }
        [ordered]@{Name=$reader.GetString($assembly.Name);Version=$assembly.Version.ToString();Identities=@($identities);Dependencies=@($dependencies|Sort-Object {$_.Name})}
    }finally{$pe.Dispose();$stream.Dispose()}
}
function Assert-ColonyOptionalPayload($Manifest,[string]$PayloadRoot) {
    if(-not $Manifest.Contains('OptionalIntegrationBasis')){throw 'Missing optional integration source basis.'}
    $basis=$Manifest.OptionalIntegrationBasis;$proofs=@($Manifest.Provenance|Where-Object Id -eq 'optional-source-proof')
    if($proofs.Count -ne 1 -or $proofs[0].Sha256 -cne $basis.ProofSha256 -or $basis.MissingDependencyPolicy -cne 'stock-loader-exclusion-and-known-provider-absent-integration-hold' -or $basis.ChangedProviderPolicy -cne 'preflight-refusal-before-install-or-manual-start' -or $basis.ChangedIntegrationPolicy -cne 'preflight-refusal-before-install-or-manual-start'){throw 'Invalid optional integration source/dependency policy.'}
    $path=Join-ColonyPath $PayloadRoot 'GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll'
    Assert-ColonyHash $path $basis.AssemblySha256
    if((Get-ColonyAssemblyFramework $path) -cne '.NETFramework,Version=v4.8.1'){throw 'Optional adapter must target reviewed net481.'}
    $metadata=Get-ColonyAssemblyMetadata $path
    $expected=@(@{Name='BackgroundResourceProcessing';Version='0.2.7'},@{Name='HarmonyKSP';Version='1.0.0'},@{Name='KSPBurst';Version='1.5.5'},@{Name='USITools';Version='0.0.0'})
    if($metadata.Name -cne 'Expanse.BrpColony' -or $metadata.Version -cne '1.0.0.0' -or (Get-ColonyJsonKey $metadata.Identities) -cne (Get-ColonyJsonKey @(@{Name='Expanse.BrpColony';Version='1.0.0'})) -or (Get-ColonyJsonKey $metadata.Dependencies) -cne (Get-ColonyJsonKey $expected)){throw 'Optional loader dependency exclusion metadata is incomplete or changed.'}
    $configPath=Join-ColonyPath $PayloadRoot 'GameData/ExpanseWorldBridge/Optional/BrpColony.cfg'
    Assert-ColonyHash $configPath $script:OptionalConfigHash
    $config=[IO.File]::ReadAllText($configPath)
    $config=[regex]::Replace($config,'(?m)//[^\r\n]*','')
    if($config -notmatch '^\s*@BACKGROUND_CONVERTER:HAS\[#name\[USI_Converter\],#adapter\[BackgroundResourceConverter\]\]:NEEDS\[BackgroundResourceProcessing,Expanse\.BrpColony\]:AFTER\[BackgroundResourceProcessing\]\s*\{\s*@adapter\s*=\s*ExpanseColonyProportionalAdapter\s*\}\s*$'){throw 'Optional converter patch is not the exact reviewed conditional replacement.'}
}
function Get-ColonyKnownProviders {
    @(
        @{Path='GameData/BackgroundResourceProcessing/Plugins/BackgroundResourceProcessing.dll';Name='BackgroundResourceProcessing';Version='0.2.7.0';LoaderName='BackgroundResourceProcessing';LoaderVersion='0.2.7';Sha256='D514CFEF35388FECEAAFC79D235D8F800D7748FF273814F1E511B9E30ECC8E55'},
        @{Path='GameData/000_USITools/USITools.dll';Name='USITools';Version='1.0.0.0';LoaderName='USITools';LoaderVersion='0.0.0';Sha256='B67965E8C41A79634B17216E987222F57D4A5E1D584FA2FF92A803F72243FAC0'},
        @{Path='GameData/000_Harmony/HarmonyInstallChecker.dll';Name='HarmonyInstallChecker';Version='2.2.1.0';LoaderName='HarmonyKSP';LoaderVersion='1.0.0';Sha256='F11E659E91A52C21C5BDA78E52B82A7BDC246BCAB6A999733AF3C69A11A9DC0E'},
        @{Path='GameData/000_Harmony/0Harmony.dll';Name='0Harmony';Version='2.2.1.0';LoaderName='';LoaderVersion='';Sha256='19CE60AC3280F72EC1751D36A40CB7E2FECE2934DF8345969DC7FEB83BD633E4'},
        @{Path='GameData/000_KSPBurst/Plugins/KSPBurst.dll';Name='KSPBurst';Version='1.7.4.10';LoaderName='KSPBurst';LoaderVersion='1.7.4';Sha256='6955BC0A369F39F5F377C28CFB833349EAB682AAC1F18B51D71E426A54157E60'}
    )
}
function Get-ColonyNativeDllPaths([string]$Root) {
    Assert-ColonyPath $Root
    $queue=[Collections.Generic.Queue[string]]::new();$queue.Enqueue($Root)
    $files=[Collections.Generic.List[string]]::new()
    while($queue.Count){
        foreach($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force){
            if($item.PSIsContainer){Assert-ColonyPath $item.FullName;$queue.Enqueue($item.FullName)}
            elseif($item.Extension -ieq '.dll'){
                Assert-ColonyPath $item.FullName;$files.Add($item.FullName.Substring($Root.Length+1).Replace('\','/'))
                if($files.Count -gt 4096){throw 'Native assembly inventory exceeds the source15 bound.'}
            }
        }
    }
    @($files|Sort-Object)
}
function Assert-ColonyPrerequisites([string]$GameRoot,[string]$ExpectedOptionalAssemblySha256,[bool]$RequireOptional) {
    $root=Get-ColonyAbsolute $GameRoot;$providers=@(Get-ColonyKnownProviders)
    if($ExpectedOptionalAssemblySha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Pin the reviewed candidate optional assembly SHA256 explicitly.'}
    $found=[Collections.Generic.List[object]]::new()
    foreach($provider in $providers){
        $path=Join-ColonyPath $root $provider.Path
        if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "Required reviewed provider is absent: $($provider.Path). Source15 installation/start is held."}
        Assert-ColonyHash $path $provider.Sha256
        $metadata=Get-ColonyAssemblyMetadata $path
        if($null -eq $metadata -or $metadata.Name -cne $provider.Name -or $metadata.Version -cne $provider.Version){throw "Required reviewed provider version differs: $($provider.Path)"}
        if($provider.LoaderName){
            $loader=if($metadata.Identities.Count -eq 0){@{Name=[IO.Path]::GetFileNameWithoutExtension($provider.Path);Version='0.0.0'}}elseif($metadata.Identities.Count -eq 1){$metadata.Identities[0]}else{throw 'Ambiguous native loader identity.'}
            if($loader.Name -cne $provider.LoaderName -or $loader.Version -cne $provider.LoaderVersion){throw "Required reviewed loader registration differs: $($provider.Path)"}
        }
        $found.Add([ordered]@{Path=$provider.Path;Sha256=$provider.Sha256;Name=$metadata.Name;Version=$metadata.Version})
    }
    $optionalPath='GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll';$optional=Join-ColonyPath $root $optionalPath
    if(Test-Path -LiteralPath $optional){
        Assert-ColonyHash $optional $ExpectedOptionalAssemblySha256
        $metadata=Get-ColonyAssemblyMetadata $optional
        if($null -eq $metadata -or $metadata.Name -cne 'Expanse.BrpColony' -or $metadata.Version -cne '1.0.0.0'){throw 'Reviewed optional integration version differs.'}
        $found.Add([ordered]@{Path=$optionalPath;Sha256=$ExpectedOptionalAssemblySha256.ToUpperInvariant();Name=$metadata.Name;Version=$metadata.Version})
    }elseif($RequireOptional){throw 'Reviewed optional integration is absent; candidate15 manual start is held.'}
    $config=Join-ColonyPath $root 'GameData/ExpanseWorldBridge/Optional/BrpColony.cfg'
    if(Test-Path -LiteralPath $config){Assert-ColonyHash $config $script:OptionalConfigHash}
    elseif($RequireOptional){throw 'Reviewed optional integration config is absent; candidate15 manual start is held.'}
    # KSP discovers DLLs recursively. A renamed or duplicate provider outside
    # its reviewed path must not silently bypass the pinned identity checks.
    $dlls=@(Get-ColonyNativeDllPaths (Join-ColonyPath $root 'GameData'))
    foreach($file in $dlls){
        $path='GameData/'+$file
        if($path -cin @($providers|ForEach-Object Path) -or $path -ceq $optionalPath){continue}
        $metadata=Get-ColonyAssemblyMetadata (Join-ColonyPath $root $path)
        if($null -eq $metadata){continue}
        if($metadata.Name -ceq 'Expanse.BrpColony' -or $metadata.Name -cin @($providers|ForEach-Object Name) -or @($metadata.Identities|Where-Object {$_.Name -cin @($providers|Where-Object LoaderName|ForEach-Object LoaderName)}).Count){throw "Duplicate or relocated native provider/integration: $path"}
    }
    [pscustomobject]@{Status='reviewed-provider-preflight-passed';NativeQualified=$false;Scope='read-only-file-hashes-and-PE-metadata; rerun before every manual start';Providers=@($found)}
}
function Test-ColonyPrerequisites {
    [CmdletBinding()]param([Parameter(Mandatory)][string]$GameRoot,[Parameter(Mandatory)][string]$ExpectedOptionalAssemblySha256)
    Assert-ColonyPrerequisites $GameRoot $ExpectedOptionalAssemblySha256 $true
}
function Assert-ColonyCandidate($Manifest,[string]$PayloadRoot) {
    if ($Manifest.SchemaVersion -ne 1 -or $Manifest.Status -ne 'candidate' -or $Manifest.QualificationPolicy -ne $script:QualificationPolicy) { throw 'Release blocked: no reviewed native qualification policy exists. This tool produces candidates only.' }
    if ($Manifest.NativeQualified -ne $false -or $Manifest.TemplatesRuntimeCertified -ne $false) { throw 'False native qualification declaration.' }
    if ($Manifest.ReleaseId -notmatch '^[a-z0-9][a-z0-9-]{2,70}$') { throw 'Invalid release ID.' }
    $profile = Get-ColonyPayloadProfile $Manifest
    Assert-ColonyExactPaths @($Manifest.Files | ForEach-Object Path) (Get-ColonyPayloadPaths $profile)
    foreach ($file in $Manifest.Files) { Assert-ColonyHash (Join-ColonyPath $PayloadRoot $file.Path) $file.Sha256 }
    Assert-ColonyHash (Join-ColonyPath $PayloadRoot 'GameData/ExpanseFoundations/Plugins/ExpanseFoundations.dll') $script:FoundationHash
    $catalog = Read-ColonyJson (Join-ColonyPath $PayloadRoot 'GameData/ExpanseWorldBridge/Templates/colony-template-catalog.json')
    Assert-ColonyExactPaths @($catalog.Templates | ForEach-Object Id) (Get-ColonyTemplateIds $profile)
    if ($profile -eq 'source15') { Assert-ColonySourceCatalog $catalog $PayloadRoot; Assert-ColonyOptionalPayload $Manifest $PayloadRoot }
    foreach ($template in $catalog.Templates) {
        if ($template.RuntimeCertified -ne $false) { throw "False certification: $($template.Id). Native gates remain open." }
        Assert-ColonyHash (Join-ColonyPath $PayloadRoot "GameData/ExpanseWorldBridge/Templates/$($template.Id).craft") $template.CraftSha256
        $detail = Read-ColonyJson (Join-ColonyPath $PayloadRoot "GameData/ExpanseWorldBridge/Templates/$($template.Id).manifest.json")
        if ($detail.Id -ne $template.Id -or $detail.Hash -ne $template.Hash -or $detail.CraftSha256 -ne $template.CraftSha256 -or $detail.RuntimeCertified -ne $false) { throw 'Catalog/template identity or certification mismatch.' }
    }
    foreach ($app in @('Host','Manager')) {
        $deps = Read-ColonyJson (Join-ColonyPath $PayloadRoot "Apps/$app/Expanse.Clock.$app.deps.json")
        if ($deps.runtimeTarget.name -notlike '.NETCoreApp,Version=v8.0*') { throw 'App runtime must be .NET 8.' }
    }
    foreach($path in @('GameData/ExpanseWorldBridge/Plugins/Expanse.WorldBridge.dll','GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll','GameData/ExpanseFoundations/Plugins/ExpanseFoundations.dll')){
        if((Get-ColonyAssemblyFramework (Join-ColonyPath $PayloadRoot $path)) -ne '.NETFramework,Version=v4.7.2'){throw 'Game assemblies must target net472.'}
    }
    foreach($path in @('Apps/Host/Expanse.Domain.dll','Apps/Manager/Expanse.Domain.dll','Apps/Host/Expanse.Clock.Host.dll','Apps/Manager/Expanse.Clock.Manager.dll')){
        if((Get-ColonyAssemblyFramework (Join-ColonyPath $PayloadRoot $path)) -ne '.NETCoreApp,Version=v8.0'){throw 'App assemblies must target net8.'}
    }
    foreach ($name in @('Expanse.Domain.dll','Expanse.Clock.Core.dll')) {
        if ((Get-ColonyHash (Join-ColonyPath $PayloadRoot "Apps/Host/$name")) -ne (Get-ColonyHash (Join-ColonyPath $PayloadRoot "Apps/Manager/$name"))) { throw "Host/Manager runtime mismatch: $name" }
    }
    if(-not $Manifest.Contains('DomainSourceBasis')){throw 'Missing frozen/net8 Domain source basis.'}
    Assert-ColonyHash (Join-ColonyPath $PayloadRoot 'GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll') $Manifest.DomainSourceBasis.FrozenNet472Sha256
    Assert-ColonyHash (Join-ColonyPath $PayloadRoot 'Apps/Host/Expanse.Domain.dll') $Manifest.DomainSourceBasis.Net8Sha256
    $sourceProof=@($Manifest.Provenance|Where-Object Id -eq 'domain-source-proof')
    if($sourceProof.Count -ne 1 -or $sourceProof[0].Sha256 -ne $Manifest.DomainSourceBasis.ProofSha256){throw 'Domain source provenance mismatch.'}
}
function New-ColonyPackage {
    param([string]$InputManifestPath,[string]$ExpectedInputManifestSha256,[string]$OutputRoot)
    $InputManifestPath = Get-ColonyAbsolute $InputManifestPath
    Assert-ColonyHash $InputManifestPath $ExpectedInputManifestSha256
    $input = Read-ColonyJson $InputManifestPath
    if($input.SchemaVersion -ne 1){throw 'Unknown input schema.'}
    if ($input.Status -ne 'candidate') { throw 'Release blocked: native qualification gates are open.' }
    if (($input.Contains('NativeQualified') -and $input.NativeQualified -ne $false) -or ($input.Contains('TemplatesRuntimeCertified') -and $input.TemplatesRuntimeCertified -ne $false)) { throw 'False native certification in input.' }
    $profile = Get-ColonyPayloadProfile $input
    Assert-ColonyExactPaths @($input.Files | ForEach-Object Path) (Get-ColonyPayloadPaths $profile)
    $OutputRoot = Get-ColonyAbsolute $OutputRoot
    if (Test-Path -LiteralPath $OutputRoot) { throw 'Output root must be new.' }
    $null = [IO.Directory]::CreateDirectory($OutputRoot)
    $stage = Join-Path $OutputRoot 'payload'; $null = [IO.Directory]::CreateDirectory($stage)
    $files = [Collections.Generic.List[object]]::new()
    foreach ($file in $input.Files | Sort-Object { $_.Path }) {
        $source = Get-ColonyAbsolute $file.Source
        Assert-ColonyHash $source $file.Sha256
        $destination = Join-ColonyPath $stage $file.Path
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        if ($file.Transform -eq 'PublicDocumentation') {
            [IO.File]::WriteAllText($destination,(Convert-ColonyPublicText ([IO.File]::ReadAllText($source))),[Text.UTF8Encoding]::new($false))
        } elseif ($file.Transform -eq 'None') { [IO.File]::Copy($source,$destination,$false) } else { throw 'Unknown transform.' }
        Assert-ColonyHash $source $file.Sha256 # Concurrent source change invalidates the snapshot.
        $files.Add([ordered]@{Path=$file.Path;Sha256=Get-ColonyHash $destination;Length=(Get-Item -LiteralPath $destination).Length})
    }
    $hashes = [Collections.Generic.List[object]]::new()
    foreach ($evidence in $input.Provenance) {
        $source = Get-ColonyAbsolute $evidence.Source; Assert-ColonyHash $source $evidence.Sha256
        if ($evidence.Id -notmatch '^[a-z0-9-]+$' -or $evidence.Kind -notin @('source','build','evidence','freeze')) { throw 'Unsafe provenance ID/kind.' }
        $hashes.Add([ordered]@{Id=$evidence.Id;Kind=$evidence.Kind;Sha256=$evidence.Sha256.ToUpperInvariant()})
    }
    foreach ($kind in @('source','build','evidence','freeze')) { if (-not @($hashes | Where-Object Kind -eq $kind).Count) { throw "Missing $kind provenance." } }
    $proofEntries=@($input.Provenance|Where-Object Id -eq 'domain-source-proof')
    if($proofEntries.Count -ne 1){throw 'Missing frozen/net8 Domain source coherence proof.'}
    $proof=Read-ColonyJson $proofEntries[0].Source
    if($proof.SchemaVersion -ne 1 -or $proof.SourceChecksumsMatch -ne $true -or $proof.CurrentSourceMatches -ne $true){throw 'Domain source coherence is unproven.'}
    Assert-ColonyHash (Join-ColonyPath $stage 'GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll') $proof.FrozenNet472Sha256
    Assert-ColonyHash (Join-ColonyPath $stage 'Apps/Host/Expanse.Domain.dll') $proof.Net8Sha256
    $appProofEntries=@($input.Provenance|Where-Object Id -eq 'app-source-proof')
    if($appProofEntries.Count -ne 1){throw 'Missing built app source proof.'}
    $appProof=Read-ColonyJson $appProofEntries[0].Source
    if($appProof.SchemaVersion -ne 1 -or $appProof.CurrentCompiledSourceMatches -ne $true){throw 'App source/build coherence is unproven.'}
    Assert-ColonyExactPaths @($appProof.Assemblies|ForEach-Object Path) @('Apps/Host/Expanse.Clock.Host.dll','Apps/Manager/Expanse.Clock.Manager.dll','Apps/Host/Expanse.Clock.Core.dll','Apps/Manager/Expanse.Clock.Core.dll','GameData/ExpanseFoundations/Plugins/ExpanseFoundations.dll')
    foreach($assembly in $appProof.Assemblies){Assert-ColonyHash (Join-ColonyPath $stage $assembly.Path) $assembly.Sha256}
    $manifest = [ordered]@{SchemaVersion=1;ReleaseId=$input.ReleaseId;Status='candidate';QualificationPolicy=$script:QualificationPolicy;NativeQualified=$false;TemplatesRuntimeCertified=$false;Runtime='net472 Bridge/Domain; net8 Windows apps; external .NET Desktop Runtime 8 required';DomainSourceBasis=[ordered]@{FrozenNet472Sha256=$proof.FrozenNet472Sha256;Net8Sha256=$proof.Net8Sha256;ProofSha256=$proofEntries[0].Sha256};Provenance=@($hashes | Sort-Object { $_.Id });Files=@($files)}
    if ($profile -eq 'source15') {
        $optionalProofs=@($input.Provenance|Where-Object Id -eq 'optional-source-proof')
        if($optionalProofs.Count -ne 1){throw 'Missing optional compiled-source proof.'}
        $optionalProof=Read-ColonyJson $optionalProofs[0].Source
        if($optionalProof.SchemaVersion -ne 1 -or $optionalProof.CurrentCompiledSourceMatches -ne $true -or $optionalProof.Path -cne 'GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll'){throw 'Optional source/build coherence is unproven.'}
        Assert-ColonyHash (Join-ColonyPath $stage $optionalProof.Path) $optionalProof.Sha256
        $manifest.PayloadProfile=$profile
        $manifest.Runtime+='; optional BRP adapter net481 requires reviewed installed loader dependencies'
        $manifest.OptionalIntegrationBasis=[ordered]@{AssemblySha256=$optionalProof.Sha256;ProofSha256=$optionalProofs[0].Sha256;MissingDependencyPolicy='stock-loader-exclusion-and-known-provider-absent-integration-hold';ChangedProviderPolicy='preflight-refusal-before-install-or-manual-start';ChangedIntegrationPolicy='preflight-refusal-before-install-or-manual-start'}
    }
    Assert-ColonyCandidate $manifest $stage
    Write-ColonyJson (Join-Path $stage 'package.json') $manifest
    $archive = Join-Path $OutputRoot ($input.ReleaseId + '.zip')
    $stream = [IO.File]::Open($archive,[IO.FileMode]::CreateNew)
    $zip = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
    try {
        foreach ($file in Get-ColonyInventory $stage) {
            $entry = $zip.CreateEntry($file.Path,[IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $target = $entry.Open(); $source = [IO.File]::OpenRead((Join-ColonyPath $stage $file.Path))
            try { $source.CopyTo($target) } finally { $source.Dispose(); $target.Dispose() }
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
    $report = [ordered]@{Archive=$archive;ArchiveSha256=Get-ColonyHash $archive;InputManifest=$InputManifestPath;InputManifestSha256=$ExpectedInputManifestSha256;Sources=$input.Files;Provenance=$input.Provenance}
    Write-ColonyJson (Join-Path $OutputRoot 'local-build-report.json') $report
    [pscustomobject]@{Archive=$archive;Sha256=$report.ArchiveSha256;ManifestSha256=Get-ColonyHash (Join-Path $stage 'package.json');Status='candidate'}
}

function Expand-ColonyVerifiedArchive([string]$Archive,[string]$Expected,[string]$Destination) {
    Assert-ColonyPath $Archive; Assert-ColonyHash $Archive $Expected
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $metadata=@($zip.Entries|Where-Object FullName -ceq 'package.json')
        if($metadata.Count -ne 1 -or $metadata[0].Length -gt 1MB){throw 'Missing, duplicated or oversized package manifest.'}
        $reader=[IO.StreamReader]::new($metadata[0].Open(),[Text.UTF8Encoding]::new($false,$true))
        try{$header=$reader.ReadToEnd()|ConvertFrom-Json -AsHashtable}finally{$reader.Dispose()}
        Assert-ColonyExactPaths @($zip.Entries | ForEach-Object FullName) (@(Get-ColonyPayloadPaths (Get-ColonyPayloadProfile $header)) + 'package.json')
        if (($zip.Entries | Measure-Object Length -Sum).Sum -gt 200MB) { throw 'Oversized archive.' }
        foreach ($entry in $zip.Entries) {
            # Unix symlinks and DOS reparse flags are forbidden even when names look safe.
            if (($entry.ExternalAttributes -band 0x400) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Linked archive entry.' }
            $path = Join-ColonyPath $Destination $entry.FullName
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            $output = [IO.File]::Open($path,[IO.FileMode]::CreateNew); $source = $entry.Open()
            try { $source.CopyTo($output) } finally { $source.Dispose(); $output.Dispose() }
        }
    } finally { $zip.Dispose() }
    $manifest = Read-ColonyJson (Join-ColonyPath $Destination 'package.json')
    Assert-ColonyCandidate $manifest $Destination
    $manifest
}

function Get-ColonyTargetProcesses($Roots) {
    # Private implementation, replaced in fixture module scope. There is no public bypass switch/provider.
    $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop)
    foreach ($process in $processes) { if(Test-ColonyProcessOwnership $process $Roots){$process} }
}
function Get-ColonyDefaultStateRoot { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ExpanseFoundations/Recovery' }
function Test-ColonyHostStateOwnership([string]$Command,$Roots) {
    if(-not $Command){throw 'Cannot determine Host command-line/state ownership.'}
    $matches=[regex]::Matches($Command,'(?:^|\s)--data-dir\s+(?:"(?<value>[^"]+)"|(?<value>\S+))')
    if($matches.Count -gt 1 -or ($Command.Contains('--data-dir') -and $matches.Count -ne 1)){throw 'Cannot determine ambiguous Host --data-dir ownership.'}
    if($matches.Count){$data=$matches[0].Groups['value'].Value;if(-not [IO.Path]::IsPathFullyQualified($data)){throw 'Cannot resolve relative Host data-dir ownership.'};$data=Get-ColonyAbsolute $data}
    else{$data=Get-ColonyDefaultStateRoot}
    (Test-ColonyNested $data $Roots.State) -or (Test-ColonyNested $Roots.State $data)
}
function Test-ColonyProcessOwnership($process,$Roots) {
        $name = [string]$process.Name; $path = [string]$process.ExecutablePath; $command = [string]$process.CommandLine
        if ($name -match '^(KSP(?:_x64)?|Expanse\.Clock\.(Host|Manager))\.exe$') {
            if (-not $path) { throw 'Cannot determine a game/app process path; close it before installation.' }
            foreach ($root in @($Roots.Game,$Roots.App)) { if (Test-ColonyNested $path $root) { return $true } }
            if($name -like 'Expanse.Clock.*'){
                if(-not $command){throw 'Cannot determine named app command-line/state ownership.'}
                foreach($root in @($Roots.State,$Roots.Game,$Roots.App)){if($command.IndexOf($root,[StringComparison]::OrdinalIgnoreCase) -ge 0){return $true}}
                if($name -eq 'Expanse.Clock.Host.exe'){
                    if(Test-ColonyHostStateOwnership $command $Roots){return $true}
                }
            }
        } elseif ($name -eq 'dotnet.exe') {
            if (-not $command) { throw 'Cannot determine dotnet process ownership.' }
            if ($command.IndexOf($Roots.App,[StringComparison]::OrdinalIgnoreCase) -ge 0 -or $command.IndexOf($Roots.State,[StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
            if($command -match '(?:^|[\\/"\s])Expanse\.Clock\.Host\.dll(?:["\s]|$)' -and (Test-ColonyHostStateOwnership $command $Roots)){return $true}
        }
    $false
}
function Assert-ColonyClosed($Roots) { if (@(Get-ColonyTargetProcesses $Roots).Count) { throw 'Target game/apps are running. Close them; this tool never kills or launches processes.' } }
function Assert-ColonyIsolated($Roots,[bool]$Mode) {
    if (-not $Mode) { throw 'Candidate installation refused. IsolatedTestCandidate is restricted to disposable source-workspace fixtures.' }
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    if (-not (Test-Path -LiteralPath (Join-Path $repo 'src/Expanse.Clock.Manager/Expanse.Clock.Manager.csproj'))) { throw 'Isolated candidate mode requires the source workspace tool.' }
    $fixtureBase = Join-Path $repo 'artifacts/colony-release-fixtures'
    $common = [IO.Path]::GetDirectoryName($Roots.Game)
    if ([IO.Path]::GetDirectoryName($common) -ne $fixtureBase -or [IO.Path]::GetFileName($common) -notmatch '^[a-f0-9]{32}$') { throw 'Candidate roots must be a newly named disposable workspace fixture.' }
    foreach ($name in @('Game','App','State','Backup')) {
        if ($Roots[$name] -ne (Join-Path $common $name)) { throw 'Isolated candidate root contract mismatch.' }
    }
    $marker = Join-Path $common 'ISOLATED-COLONY-TEST.json'
    Assert-ColonyPath $marker
    $data = Read-ColonyJson $marker
    if ($data.SchemaVersion -ne 1 -or $data.Id -ne [IO.Path]::GetFileName($common) -or $data.Purpose -ne 'disposable-colony-tooling-test') { throw 'Invalid isolated fixture marker.' }
    foreach ($file in @('KSP_x64.exe','KSP.exe')) {
        $fake = Join-Path $Roots.Game $file
        if (Test-Path -LiteralPath $fake) {
            Assert-ColonyPath $fake
            if ([IO.File]::ReadAllText($fake) -cne "DISPOSABLE-COLONY-TEST $($data.Id)") { throw 'Candidate mode refuses real game executables.' }
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Roots.Game 'KSP_x64.exe'))) { throw 'Fixture fake executable required.' }
}
function Copy-ColonyVerified([string]$Source,[string]$Destination,[string]$Hash) {
    Assert-ColonyPath $Source; Assert-ColonyPath $Destination; Assert-ColonyHash $Source $Hash
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination))
    [IO.File]::Copy($Source,$Destination,$false); Assert-ColonyHash $Destination $Hash; Assert-ColonyHash $Source $Hash
}
function New-ColonyRoots([string]$GameRoot,[string]$AppRoot,[string]$StateRoot,[string]$BackupRoot) {
    $roots = @{Game=Get-ColonyAbsolute $GameRoot;App=Get-ColonyAbsolute $AppRoot;State=Get-ColonyAbsolute $StateRoot;Backup=Get-ColonyAbsolute $BackupRoot}
    Assert-ColonyRoots $roots
    $roots
}
function Get-ColonyScopeInventory($Roots,[string]$SaveDirectory) {
    $inventory = [ordered]@{}
    foreach ($scope in @('GameData/ExpanseWorldBridge','GameData/ExpanseFoundations')) { $inventory[$scope] = @(Get-ColonyInventory (Join-ColonyPath $Roots.Game $scope)) }
    $inventory.App = @(Get-ColonyInventory $Roots.App)
    $inventory.State = @(Get-ColonyInventory $Roots.State)
    $inventory.Save = @(Get-ColonyInventory (Join-ColonyPath $Roots.Game $SaveDirectory))
    $inventory
}
function Assert-ColonyInstallScope($Inventory,$Manifest) {
    foreach ($scope in @('GameData/ExpanseWorldBridge','GameData/ExpanseFoundations')) {
        $allowed = @($Manifest.Files | Where-Object { $_.Path.StartsWith("$scope/") } | ForEach-Object { $_.Path.Substring($scope.Length+1) })
        foreach ($file in $Inventory[$scope]) { if ($file.Path -cnotin $allowed) { throw "Unexpected managed file (including extra DLLs): $scope/$($file.Path)" } }
    }
    foreach ($file in $Inventory.App) {
        if ($file.Path -eq 'current.json') { continue }
        if ($file.Path -notmatch '^versions/([a-z0-9][a-z0-9-]{2,70})/(.+)$') { throw 'Unversioned or unexpected app file.' }
        if ($Matches[2] -cnotin @($Manifest.Files | Where-Object { $_.Path -notlike 'GameData/*' } | ForEach-Object Path)) { throw 'Unexpected app runtime file.' }
    }
}
function Invoke-ColonyInstall {
    param([string]$ArchivePath,[string]$ExpectedArchiveSha256,[string]$GameRoot,[string]$AppRoot,[string]$StateRoot,[string]$BackupRoot,[string]$SelectedSaveRelativePath,[string]$ExpectedSaveSha256,[switch]$IsolatedTestCandidate)
    $roots = New-ColonyRoots $GameRoot $AppRoot $StateRoot $BackupRoot
    Assert-ColonyClosed $roots
    $save = Assert-ColonyRelative $SelectedSaveRelativePath
    if ($save -notmatch '^saves/[^/]+/persistent\.sfs$') { throw 'Select saves/<name>/persistent.sfs explicitly.' }
    $saveDirectory = $save.Substring(0,$save.LastIndexOf('/'))
    Assert-ColonyHash (Join-ColonyPath $roots.Game $save) $ExpectedSaveSha256
    $transaction = Join-ColonyPath $roots.Backup ([guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($transaction)
    $stage = Join-Path $transaction 'stage'
    $manifest = Expand-ColonyVerifiedArchive $ArchivePath $ExpectedArchiveSha256 $stage
    Assert-ColonyIsolated $roots $IsolatedTestCandidate.IsPresent
    if((Get-ColonyPayloadProfile $manifest) -eq 'source15'){Assert-ColonyPrerequisites $roots.Game $manifest.OptionalIntegrationBasis.AssemblySha256 $false | Out-Null}
    $version = "versions/$($manifest.ReleaseId)"
    if (Test-Path -LiteralPath (Join-ColonyPath $roots.App $version)) { throw 'App version already exists; use a new release ID or rollback its receipt.' }
    $before = Get-ColonyScopeInventory $roots $saveDirectory
    Assert-ColonyInstallScope $before $manifest
    foreach ($scope in $before.Keys) {
        $sourceRoot = switch ($scope) { 'App' {$roots.App} 'State' {$roots.State} 'Save' {Join-ColonyPath $roots.Game $saveDirectory} default {Join-ColonyPath $roots.Game $scope} }
        foreach ($file in $before[$scope]) { Copy-ColonyVerified (Join-ColonyPath $sourceRoot $file.Path) (Join-ColonyPath $transaction "before/$scope/$($file.Path)") $file.Sha256 }
        Assert-ColonyInventory $sourceRoot $before[$scope]
    }
    Assert-ColonyHash (Join-ColonyPath $roots.Game $save) $ExpectedSaveSha256
    $pointer = Join-Path $stage 'current.json'
    Write-ColonyJson $pointer ([ordered]@{SchemaVersion=1;ReleaseId=$manifest.ReleaseId;Status='candidate';VersionPath=$version;PackageSha256=$ExpectedArchiveSha256})
    $operations = [Collections.Generic.List[object]]::new()
    foreach ($file in @($manifest.Files) + @(@{Path='current.json';Sha256=Get-ColonyHash $pointer;Length=(Get-Item -LiteralPath $pointer).Length})) {
        $scope = if ($file.Path -like 'GameData/*') { 'Game' } else { 'App' }
        $relative = if ($scope -eq 'Game' -or $file.Path -eq 'current.json') { $file.Path } else { "$version/$($file.Path)" }
        $target = Join-ColonyPath $roots[$scope] $relative
        $oldHash = if (Test-Path -LiteralPath $target) { Get-ColonyHash $target } else { $null }
        $oldBackup = if (-not $oldHash) { $null } elseif ($scope -eq 'App') { "before/App/$relative" } else { "before/$relative" }
        $operations.Add([ordered]@{Scope=$scope;Path=$relative;BeforeSha256=$oldHash;BeforeBackup=$oldBackup;AfterSha256=$file.Sha256;AfterSource="stage/$($file.Path)";TemporaryPath="$relative.expanse-$([IO.Path]::GetFileName($transaction)).tmp"})
    }
    $receipt = [ordered]@{SchemaVersion=1;Policy=$script:QualificationPolicy;ReleaseId=$manifest.ReleaseId;Status='prepared';Roots=$roots;SelectedSave=$save;SelectedSaveSha256=$ExpectedSaveSha256;SaveDirectory=$saveDirectory;ArchiveSha256=$ExpectedArchiveSha256;ManifestSha256=Get-ColonyHash (Join-Path $stage 'package.json');Before=$before;Operations=@($operations)}
    if((Get-ColonyPayloadProfile $manifest) -eq 'source15'){$receipt.PayloadProfile='source15';$receipt.OptionalAssemblySha256=$manifest.OptionalIntegrationBasis.AssemblySha256}
    $receiptPath = Join-Path $transaction 'receipt.json'
    Write-ColonyJson $receiptPath $receipt
    $receiptHash = Get-ColonyHash $receiptPath
    Write-Information "Recovery receipt: $receiptPath SHA256 $receiptHash" -InformationAction Continue
    try {
        Assert-ColonyClosed $roots
        if((Get-ColonyPayloadProfile $manifest) -eq 'source15'){Assert-ColonyPrerequisites $roots.Game $manifest.OptionalIntegrationBasis.AssemblySha256 $false | Out-Null}
        foreach ($operation in $operations) {
            Set-ColonyInstalledFile $roots $transaction $operation
        }
        Assert-ColonyInstalled $receipt $transaction
        if((Get-ColonyPayloadProfile $manifest) -eq 'source15'){Test-ColonyPrerequisites $roots.Game $manifest.OptionalIntegrationBasis.AssemblySha256 | Out-Null}
        Write-ColonyJson (Join-Path $transaction 'status.json') @{Status='installed';ReceiptSha256=$receiptHash}
    } catch {
        $failure = $_.Exception.Message
        try { Restore-ColonyTransaction $receipt $transaction -RecoverIncomplete; Write-ColonyJson (Join-Path $transaction 'status.json') @{Status='failed-restored';ReceiptSha256=$receiptHash;Failure=$failure} }
        catch { throw "Install failed: $failure. Recovery held: $($_.Exception.Message). Immutable receipt: $receiptPath SHA256 $receiptHash" }
        throw "Install failed and known binaries restored: $failure. Immutable receipt: $receiptPath SHA256 $receiptHash"
    }
    [pscustomobject]@{ReceiptPath=$receiptPath;ReceiptSha256=$receiptHash;Status='installed-candidate';ReleaseId=$manifest.ReleaseId}
}

function Set-ColonyInstalledFile($Roots,[string]$Transaction,$Operation) {
    Assert-ColonyClosed $Roots
    $target = Join-ColonyPath $Roots[$Operation.Scope] $Operation.Path
    $temporary = Join-ColonyPath $Roots[$Operation.Scope] $Operation.TemporaryPath
    if (Test-Path -LiteralPath $temporary) { throw 'Transaction temporary already exists.' }
    if ($Operation.BeforeSha256) { Assert-ColonyHash $target $Operation.BeforeSha256 } elseif (Test-Path -LiteralPath $target) { throw 'New target appeared during installation.' }
    Copy-ColonyVerified (Join-ColonyPath $Transaction $Operation.AfterSource) $temporary $Operation.AfterSha256
    Assert-ColonyClosed $Roots
    if ($Operation.BeforeSha256) { Assert-ColonyHash $target $Operation.BeforeSha256 } elseif (Test-Path -LiteralPath $target) { throw 'New target appeared before commit.' }
    Assert-ColonyPath $temporary;Assert-ColonyHash $temporary $Operation.AfterSha256
    [IO.File]::Move($temporary,$target,$true)
    Assert-ColonyHash $target $Operation.AfterSha256
}
function Get-ColonyExpectedInstalled($Receipt) {
    $expected = [ordered]@{}
    foreach ($scope in @('GameData/ExpanseWorldBridge','GameData/ExpanseFoundations')) {
        $expected[$scope] = @($Receipt.Operations | Where-Object { $_.Scope -eq 'Game' -and $_.Path.StartsWith("$scope/") } | ForEach-Object { @{Path=$_.Path.Substring($scope.Length+1);Sha256=$_.AfterSha256;Length=0} })
    }
    $expected.App = @($Receipt.Before.App | Where-Object Path -ne 'current.json') + @($Receipt.Operations | Where-Object Scope -eq 'App' | ForEach-Object { @{Path=$_.Path;Sha256=$_.AfterSha256;Length=0} })
    $expected
}
function Assert-ColonyInstalled($Receipt,[string]$Transaction) {
    $expected = Get-ColonyExpectedInstalled $Receipt
    foreach ($scope in $expected.Keys) {
        $root = if ($scope -eq 'App') { $Receipt.Roots.App } else { Join-ColonyPath $Receipt.Roots.Game $scope }
        $actual = @(Get-ColonyInventory $root)
        Assert-ColonyExactPaths @($actual | ForEach-Object Path) @($expected[$scope] | ForEach-Object Path)
        foreach ($file in $expected[$scope]) { Assert-ColonyHash (Join-ColonyPath $root $file.Path) $file.Sha256 }
    }
}
function Assert-ColonyKnownTemporary([string]$Temporary,[string]$Source) {
    Assert-ColonyPath $Temporary; Assert-ColonyPath $Source
    # A crashed copy may leave a prefix. Only bytes matching the retained, pinned source are removable.
    $a = [IO.File]::ReadAllBytes($Temporary); $b = [IO.File]::ReadAllBytes($Source)
    if ($a.Length -gt $b.Length) { throw 'Unknown temporary bytes.' }
    for ($i=0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { throw 'Unknown temporary bytes.' } }
}
function Assert-ColonyOperationTemporary([string]$Temporary,[string]$Transaction,$Operation) {
    try { Assert-ColonyKnownTemporary $Temporary (Join-ColonyPath $Transaction $Operation.AfterSource); return } catch { $afterFailure=$_ }
    if ($Operation.BeforeSha256) { Assert-ColonyKnownTemporary $Temporary (Join-ColonyPath $Transaction $Operation.BeforeBackup) } else { throw $afterFailure }
}
function Assert-ColonyRecoveryInventory($Receipt,[string]$Transaction) {
    foreach ($scope in @('GameData/ExpanseWorldBridge','GameData/ExpanseFoundations','App')) {
        $root = if ($scope -eq 'App') { $Receipt.Roots.App } else { Join-ColonyPath $Receipt.Roots.Game $scope }
        $allowed = @{}
        foreach ($file in $Receipt.Before[$scope]) { $allowed[$file.Path] = @($file.Sha256) }
        foreach ($operation in $Receipt.Operations) {
            if (($scope -eq 'App' -and $operation.Scope -eq 'App') -or ($operation.Scope -eq 'Game' -and $operation.Path.StartsWith("$scope/"))) {
                $relative = if ($scope -eq 'App') { $operation.Path } else { $operation.Path.Substring($scope.Length+1) }
                $allowed[$relative] = @($operation.AfterSha256,$operation.BeforeSha256)
                $temp = if ($scope -eq 'App') { $operation.TemporaryPath } else { $operation.TemporaryPath.Substring($scope.Length+1) }
                $allowed[$temp] = @('temporary')
            }
        }
        foreach ($file in Get-ColonyInventory $root) {
            if (-not $allowed.ContainsKey($file.Path)) { throw "Unknown recovery file: $scope/$($file.Path)" }
            if ($allowed[$file.Path][0] -eq 'temporary') {
                $op = @($Receipt.Operations | Where-Object { $_.TemporaryPath.EndsWith($file.Path,[StringComparison]::Ordinal) })
                if ($op.Count -ne 1) { throw 'Ambiguous temporary.' }
                Assert-ColonyOperationTemporary (Join-ColonyPath $root $file.Path) $Transaction $op[0]
            } elseif ($file.Sha256 -notin $allowed[$file.Path]) { throw "Unknown recovery bytes: $scope/$($file.Path)" }
        }
        foreach ($file in $Receipt.Before[$scope]) {
            if (-not (Test-Path -LiteralPath (Join-ColonyPath $root $file.Path))) { throw 'Prior inventory file is missing; recovery refuses unexplained deletion.' }
        }
    }
    foreach ($operation in $Receipt.Operations) {
        $target = Join-ColonyPath $Receipt.Roots[$operation.Scope] $operation.Path
        if ($operation.BeforeSha256 -and -not (Test-Path -LiteralPath $target)) { throw 'Previously existing target is missing; recovery refuses unexplained deletion.' }
    }
}
function Restore-ColonyTransaction($Receipt,[string]$Transaction,[switch]$RecoverIncomplete,[switch]$RestoreUnchangedSaveAndState) {
    Assert-ColonyClosed $Receipt.Roots
    if((Get-ColonyPayloadProfile $Receipt) -eq 'source15'){Assert-ColonyPrerequisites $Receipt.Roots.Game $Receipt.OptionalAssemblySha256 $false | Out-Null}
    if ($RecoverIncomplete) { Assert-ColonyRecoveryInventory $Receipt $Transaction } else { Assert-ColonyInstalled $Receipt $Transaction }
    foreach ($scope in $Receipt.Before.Keys) {
        foreach ($file in $Receipt.Before[$scope]) { Assert-ColonyHash (Join-ColonyPath $Transaction "before/$scope/$($file.Path)") $file.Sha256 }
    }
    foreach ($operation in $Receipt.Operations) { Assert-ColonyHash (Join-ColonyPath $Transaction $operation.AfterSource) $operation.AfterSha256 }
    Assert-ColonyInventory $Receipt.Roots.State $Receipt.Before.State
    Assert-ColonyInventory (Join-ColonyPath $Receipt.Roots.Game $Receipt.SaveDirectory) $Receipt.Before.Save
    # Even binaries-only rollback holds advanced/migrated state. Never rewrite it to satisfy this guard.
    foreach ($operation in @($Receipt.Operations)[(@($Receipt.Operations).Count-1)..0]) {
        Assert-ColonyClosed $Receipt.Roots
        $target = Join-ColonyPath $Receipt.Roots[$operation.Scope] $operation.Path
        $temp = Join-ColonyPath $Receipt.Roots[$operation.Scope] $operation.TemporaryPath
        if (Test-Path -LiteralPath $temp) { Assert-ColonyOperationTemporary $temp $Transaction $operation; Remove-Item -LiteralPath $temp }
        if ($operation.BeforeSha256) {
            if ((Get-ColonyHash $target) -eq $operation.BeforeSha256) { continue }
            Assert-ColonyHash $target $operation.AfterSha256
            Copy-ColonyVerified (Join-ColonyPath $Transaction $operation.BeforeBackup) $temp $operation.BeforeSha256
            Assert-ColonyClosed $Receipt.Roots
            Assert-ColonyHash $target $operation.AfterSha256;Assert-ColonyHash $temp $operation.BeforeSha256
            [IO.File]::Move($temp,$target,$true); Assert-ColonyHash $target $operation.BeforeSha256
        } elseif (Test-Path -LiteralPath $target) { Assert-ColonyHash $target $operation.AfterSha256; Remove-Item -LiteralPath $target }
    }
    foreach ($scope in @('GameData/ExpanseWorldBridge','GameData/ExpanseFoundations','App')) {
        $root = if ($scope -eq 'App') { $Receipt.Roots.App } else { Join-ColonyPath $Receipt.Roots.Game $scope }
        Assert-ColonyInventory $root $Receipt.Before[$scope]
    }
    # Remove only empty directories in the new app version. No recursive deletion or backup removal.
    $versionRoot = Join-ColonyPath $Receipt.Roots.App "versions/$($Receipt.ReleaseId)"
    if (Test-Path -LiteralPath $versionRoot) {
        $directories = @(Get-ChildItem -LiteralPath $versionRoot -Directory -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending) + @(Get-Item -LiteralPath $versionRoot)
        foreach ($directory in $directories) { Assert-ColonyPath $directory.FullName; if (-not @(Get-ChildItem -LiteralPath $directory.FullName -Force).Count) { Remove-Item -LiteralPath $directory.FullName } }
    }
}
function Invoke-ColonyRollback {
    param([string]$ReceiptPath,[string]$ExpectedReceiptSha256,[string]$GameRoot,[string]$AppRoot,[string]$StateRoot,[string]$BackupRoot,[switch]$RecoverIncomplete,[switch]$RestoreUnchangedSaveAndState)
    $roots = New-ColonyRoots $GameRoot $AppRoot $StateRoot $BackupRoot
    $ReceiptPath = Get-ColonyAbsolute $ReceiptPath; Assert-ColonyHash $ReceiptPath $ExpectedReceiptSha256
    $receipt = Read-ColonyJson $ReceiptPath
    if ($receipt.SchemaVersion -ne 1 -or $receipt.Policy -ne $script:QualificationPolicy -or $receipt.Status -ne 'prepared') { throw 'Unknown receipt policy.' }
    foreach ($name in $roots.Keys) { if ($roots[$name] -ne $receipt.Roots[$name]) { throw 'Receipt root mismatch.' } }
    $transaction = [IO.Path]::GetDirectoryName($ReceiptPath)
    if ([IO.Path]::GetDirectoryName($transaction) -ne $roots.Backup -or [IO.Path]::GetFileName($transaction) -notmatch '^[a-f0-9]{32}$' -or [IO.Path]::GetFileName($ReceiptPath) -ne 'receipt.json') { throw 'Receipt is outside its matching backup transaction.' }
    Assert-ColonyHash (Join-ColonyPath $transaction 'stage/package.json') $receipt.ManifestSha256
    $manifest = Read-ColonyJson (Join-ColonyPath $transaction 'stage/package.json')
    Assert-ColonyCandidate $manifest (Join-Path $transaction 'stage')
    Restore-ColonyTransaction $receipt $transaction -RecoverIncomplete:$RecoverIncomplete -RestoreUnchangedSaveAndState:$RestoreUnchangedSaveAndState
    Write-ColonyJson (Join-Path $transaction 'status.json') @{Status='rolled-back-binaries';ReceiptSha256=$ExpectedReceiptSha256;SaveAndState='retained-current-bytes'}
    [pscustomobject]@{Status='rolled-back-binaries';SaveAndState='retained-current-bytes';ReceiptPath=$ReceiptPath}
}

Export-ModuleMember -Function New-ColonyPackage,Invoke-ColonyInstall,Invoke-ColonyRollback,Test-ColonyPrerequisites
