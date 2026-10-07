$ErrorActionPreference='Stop'
$release=Join-Path $PSScriptRoot 'manager-ui-release-20261005-001323'
$build=Join-Path $PSScriptRoot 'manager-ui-work/src/Expanse.Clock.Manager/bin/Release/net8.0-windows'
$version=Join-Path $release 'version'
$manager=Join-Path $version 'Manager'
$only=Join-Path $release 'manager-only'
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function WriteJson([string]$path,$data){[IO.File]::WriteAllText($path,($data|ConvertTo-Json -Depth 12)+"`n",[Text.UTF8Encoding]::new($false))}
function ZipAndCheck([string]$folder,[string]$archive){
    if(Test-Path -LiteralPath $archive){Remove-Item -LiteralPath $archive}
    [IO.Compression.ZipFile]::CreateFromDirectory($folder,$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try{
        $files=@(Get-ChildItem -LiteralPath $folder -Recurse -File)
        if($zip.Entries.Count -ne $files.Count){throw 'Archive inventory mismatch'}
        foreach($file in $files){
            $relative=[IO.Path]::GetRelativePath($folder,$file.FullName).Replace('\','/')
            $entry=$zip.GetEntry($relative)
            if(!$entry){throw "Missing archive entry: $relative"}
            $stream=$entry.Open()
            try{$entryHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}
            finally{$stream.Dispose()}
            if($entryHash -ne (Hash $file.FullName)){throw "Archive content mismatch: $relative"}
        }
    }finally{$zip.Dispose()}
}
$onlyReceiptPath=Join-Path $release 'MANAGER-ONLY-RECEIPT.json'
$onlyReceipt=Get-Content -LiteralPath $onlyReceiptPath -Raw|ConvertFrom-Json
if($onlyReceipt.installed -or (Test-Path -LiteralPath (Join-Path $release 'manager-only-rollback'))){throw 'Package is installed or rollback exists'}
foreach($dependency in @('Expanse.Clock.Core.dll','Expanse.Domain.dll')){
    if((Hash (Join-Path $build $dependency)) -ne (Hash (Join-Path $manager $dependency))){throw "Unexpected dependency change: $dependency"}
}
$name='Expanse.Clock.Manager.dll'
if((Hash (Join-Path $manager $name)) -ne (Hash (Join-Path $only $name))){throw 'Existing package DLLs differ'}
Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $manager $name) -Force
Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $only $name) -Force
$fullManifestPath=Join-Path $version 'MANIFEST.json'
$fullManifest=Get-Content -LiteralPath $fullManifestPath -Raw|ConvertFrom-Json
$entry=@($fullManifest.Files|Where-Object Path -eq "Apps/Manager/$name")
if($entry.Count -ne 1){throw 'Manager manifest row missing'}
$entry[0].Sha256=Hash (Join-Path $manager $name)
WriteJson $fullManifestPath $fullManifest
foreach($row in $fullManifest.Files){
    $relative=$row.Path -replace '^Apps/',''
    if((Hash (Join-Path $version $relative)) -ne $row.Sha256){throw "Full package file mismatch: $relative"}
}
$onlyManifestPath=Join-Path $only 'MANAGER-MANIFEST.json'
$onlyManifest=Get-Content -LiteralPath $onlyManifestPath -Raw|ConvertFrom-Json
$onlyManifest.files.$name=Hash (Join-Path $only $name)
WriteJson $onlyManifestPath $onlyManifest
foreach($file in $onlyManifest.files.PSObject.Properties){
    if((Hash (Join-Path $only $file.Name)) -ne $file.Value){throw "Manager file mismatch: $($file.Name)"}
}
$fullArchive=Join-Path $release 'expanse-manager-ui-20261005-001323.zip'
$onlyArchive=Join-Path $release 'expanse-manager-only-20261005-001323.zip'
ZipAndCheck $version $fullArchive
ZipAndCheck $only $onlyArchive
$fullReceiptPath=Join-Path $release 'RECEIPT.json'
$fullReceipt=Get-Content -LiteralPath $fullReceiptPath -Raw|ConvertFrom-Json
$fullReceipt.archiveSha256=Hash $fullArchive
$fullReceipt.manifestSha256=Hash $fullManifestPath
$fullReceipt.changedFiles."Manager/$name"=Hash (Join-Path $manager $name)
WriteJson $fullReceiptPath $fullReceipt
$onlyReceipt.archiveSha256=Hash $onlyArchive
$onlyReceipt.manifestSha256=Hash $onlyManifestPath
WriteJson $onlyReceiptPath $onlyReceipt
[pscustomobject]@{ManagerDllSha256=(Hash (Join-Path $manager $name));ManagerOnlyArchiveSha256=$onlyReceipt.archiveSha256;ManagerOnlyManifestSha256=$onlyReceipt.manifestSha256}|ConvertTo-Json
