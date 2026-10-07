[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,[Parameter(Mandatory)][string]$ExpectedArchiveSha256,
    [Parameter(Mandatory)][string]$GameRoot,[Parameter(Mandatory)][string]$AppRoot,
    [Parameter(Mandatory)][string]$StateRoot,[Parameter(Mandatory)][string]$BackupRoot,
    [Parameter(Mandatory)][string]$SelectedSaveRelativePath,[Parameter(Mandatory)][string]$ExpectedSaveSha256,
    [switch]$IsolatedTestCandidate
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Expanse.Colony.Release.psm1') -Force
Invoke-ColonyInstall @PSBoundParameters
