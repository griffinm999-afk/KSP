[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReceiptPath,[Parameter(Mandatory)][string]$ExpectedReceiptSha256,
    [Parameter(Mandatory)][string]$GameRoot,[Parameter(Mandatory)][string]$AppRoot,
    [Parameter(Mandatory)][string]$StateRoot,[Parameter(Mandatory)][string]$BackupRoot,
    [switch]$RecoverIncomplete,[switch]$RestoreUnchangedSaveAndState
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Expanse.Colony.Release.psm1') -Force
Invoke-ColonyRollback @PSBoundParameters
