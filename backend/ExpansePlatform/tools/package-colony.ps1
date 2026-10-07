[CmdletBinding()]
param([Parameter(Mandatory)][string]$InputManifestPath,[Parameter(Mandatory)][string]$ExpectedInputManifestSha256,[Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Expanse.Colony.Release.psm1') -Force
New-ColonyPackage @PSBoundParameters
