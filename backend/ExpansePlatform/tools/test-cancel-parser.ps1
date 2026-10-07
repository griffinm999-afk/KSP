[CmdletBinding()]
param([string]$KspManagedDir = 'C:\Kerbal Space Program\KSP_x64_Data\Managed')
$ErrorActionPreference = 'Stop'
$platform = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$bridgeDir = Join-Path $platform 'src\Expanse.WorldBridge\bin\Release\net472'
# Pure managed parser/hash check. No game process, Unity object, save or pipe is used.
[Reflection.Assembly]::LoadFrom((Join-Path $KspManagedDir 'UnityEngine.CoreModule.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $KspManagedDir 'UnityEngine.dll')) | Out-Null
Add-Type -Path (Join-Path $bridgeDir 'Expanse.Domain.dll')
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $bridgeDir 'Expanse.WorldBridge.dll'))
$type = $assembly.GetType('Expanse.WorldBridge.WorldBridgeAddon', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$payload = [Collections.Generic.Dictionary[string,object]]::new()
$payload['kind'] = 'ruleCancel'
$payload['ruleId'] = 'Automatic Ore exports'
$parsed = $type.GetMethod('ParseDelivery', $flags).Invoke($null, @($payload))
if ($parsed.RuleId -ne 'Automatic Ore exports') { throw 'Cancellation parser dropped RuleId.' }
$proposal = [Expanse.Domain.EffectProposal]::new()
$proposal.OperationKind = 'ruleCancel'
$proposal.Delivery = $parsed
$hash = $type.GetMethod('ExpectedPayloadHash', $flags).Invoke($null, @($proposal))
if ($hash -ne [Expanse.Domain.OperationIdentity]::RuleCancelPayloadHash('Automatic Ore exports')) {
    throw 'Cancellation payload hash does not preserve the parsed rule identity.'
}
[pscustomobject]@{ ShippingBridgeParserRegressionPassed = $true; RuleId = $parsed.RuleId; PayloadHash = $hash }
