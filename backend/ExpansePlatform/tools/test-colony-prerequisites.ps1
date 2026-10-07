[CmdletBinding()]
param([Parameter(Mandatory)][string]$GameRoot,[Parameter(Mandatory)][string]$ExpectedOptionalAssemblySha256)
$ErrorActionPreference='Stop'
# Read-only metadata and SHA256 inspection. This does not load assemblies,
# install files, certify a candidate, or start a game/application.
Import-Module (Join-Path $PSScriptRoot 'Expanse.Colony.Release.psm1') -Force
Test-ColonyPrerequisites @PSBoundParameters
