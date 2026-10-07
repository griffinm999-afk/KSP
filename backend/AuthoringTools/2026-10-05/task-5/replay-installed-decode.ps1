$ErrorActionPreference='Stop'
$install=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'clock256-install-result.json') -Raw | ConvertFrom-Json
$dll=Get-ChildItem -LiteralPath $install.versionRoot -Recurse -Filter 'Expanse.Clock.Core.dll' | Where-Object {$_.Directory.Name -eq 'Host'} | Select-Object -First 1
if(!$dll){$dll=Get-ChildItem -LiteralPath $install.versionRoot -Recurse -Filter 'Expanse.Clock.Core.dll' | Select-Object -First 1}
[Reflection.Assembly]::LoadFrom($dll.FullName)|Out-Null
$raw=[IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'rotation-unpruned-publisher.json'))
$sample=[Expanse.Clock.Core.ClockProtocol]::DecodeClockSample([ReadOnlyMemory[byte]]::new($raw))
[pscustomobject]@{pipeline='exact installed DecodeClockSample';assemblySha256=(Get-FileHash -LiteralPath $dll.FullName).Hash;inputBytes=$raw.Length;colonyStatus=$sample.Colony.Status;colonyReason=$sample.Colony.Reason;vesselCount=$sample.Colony.Vessels.Length}|ConvertTo-Json
