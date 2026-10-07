param([string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
$platformRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $workspaceRoot 'outputs/colony-runtime-tests/power-operator14-validation' }
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$evidenceJson = & 'C:/Users/griff/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' (Join-Path $PSScriptRoot 'ReadInstalledEvidence.py')
if ($LASTEXITCODE -ne 0) { throw 'Installed metadata reader failed' }
$evidence = ($evidenceJson -join "`n") | ConvertFrom-Json -AsHashtable
Add-Type -LiteralPath (Join-Path $platformRoot 'dev/Expanse.Colony.UtilityContracts/bin/Release/net8.0/Mono.Cecil.dll')
$heatPath = 'C:/Kerbal Space Program/GameData/SystemHeat/Plugin/SystemHeat.dll'
$kspPath = 'C:/Kerbal Space Program/KSP_x64_Data/Managed/Assembly-CSharp.dll'
$usiPath = 'C:/Kerbal Space Program/GameData/000_USITools/USITools.dll'
$heatAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($heatPath)
$kspAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($kspPath)
$usiAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($usiPath)
$reactor = $heatAssembly.MainModule.Types | Where-Object FullName -eq 'SystemHeat.ModuleSystemHeatFissionReactor'
$heat = $heatAssembly.MainModule.Types | Where-Object FullName -eq 'SystemHeat.ModuleSystemHeat'
$loop = $heatAssembly.MainModule.Types | Where-Object FullName -eq 'SystemHeat.HeatLoop'
$partModule = $kspAssembly.MainModule.Types | Where-Object Name -eq 'PartModule'
$converter = $kspAssembly.MainModule.Types | Where-Object Name -eq 'BaseConverter'
$usiConverter = $usiAssembly.MainModule.Types | Where-Object FullName -eq 'USITools.USI_Converter'
$checks = [System.Collections.Generic.List[string]]::new()
function Assert-Contract([bool]$Condition,[string]$Name) { if (-not $Condition) { throw $Name }; $checks.Add($Name); Write-Output "PASS $Name" }
Assert-Contract ($evidence.TemplateId -eq 'power-duna-v1' -and $evidence.DeclaredWorkers -eq 1 -and $evidence.DeclaredTrait -eq 'Engineer') 'Reviewed power package explicitly purchases one Engineer workplace'
Assert-Contract ($evidence.ActualCraftSha256 -eq $evidence.ReviewedCraftSha256) 'Reviewed exact craft SHA matches source bytes'
Assert-Contract ($evidence.ActualCraftPartCount -eq $evidence.ExpectedPartCount -and $evidence.OperatorCraftMappingCount -eq 1) 'Complete reviewed craft maps Duna.PDU only at craft104'
Assert-Contract ($evidence.NativeCrewCapacity -eq '2') 'Exact installed PDU has two actual cabin seats'
Assert-Contract (@($evidence.InstalledModules | Where-Object Name -eq 'ModuleSystemHeatFissionReactor').Count -eq 1 -and @($evidence.InstalledModules | Where-Object Name -eq 'ModuleSystemHeat').Count -eq 1) 'Exact installed PDU has one genuine reactor and heat module'
Assert-Contract (@($evidence.InstalledModules | Where-Object Name -eq 'ModuleAutoRepairer').Count -eq 0) 'Native PDU does not claim an AutoRepairer industrial workplace'
Assert-Contract ($heatAssembly.Name.Version.ToString() -eq '0.9.1.0' -and $reactor.BaseType.FullName -eq 'PartModule' -and $heat.BaseType.FullName -eq 'PartModule') 'Exact SystemHeat 0.9.1 reactor/heat providers are actual PartModules'
$heatField = $reactor.Fields | Where-Object Name -eq 'heatModule'
Assert-Contract ($heatField.FieldType.FullName -eq $heat.FullName -and $heatField.IsFamily) 'Native protected reactor heatModule binds the exact same-part heat provider'
$loopProperty = $heat.Properties | Where-Object Name -eq 'Loop'
$membersProperty = $loop.Properties | Where-Object Name -eq 'LoopModules'
Assert-Contract ($loopProperty.GetMethod.IsPublic -and $loopProperty.PropertyType.FullName -eq $loop.FullName -and $membersProperty.GetMethod.IsPublic -and $membersProperty.PropertyType.FullName -eq 'System.Collections.Generic.List`1<SystemHeat.ModuleSystemHeat>') 'Public actual heat loop and member identities support read-only exact membership'
$identity = $partModule.Properties | Where-Object Name -eq 'PersistentId'
Assert-Contract ($identity.GetMethod.IsPublic -and $identity.PropertyType.FullName -eq 'System.UInt32') 'Native PartModule exposes exact persistent UInt32 identity'
$specialistWrites = @($converter.Methods | Where-Object Name -eq '.ctor' | ForEach-Object { $_.Body.Instructions } | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'UseSpecialistBonus' })
$usiSpecialistWrites = @($usiConverter.Methods | Where-Object Name -in @('.ctor','OnStart') | ForEach-Object { $_.Body.Instructions } | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'UseSpecialistBonus' })
Assert-Contract ($specialistWrites.Count -eq 0 -and $usiSpecialistWrites.Count -eq 0 -and @($evidence.InstalledModules | Where-Object { $_.Values.UseSpecialistBonus -contains 'true' -or $_.Values.UseSpecialistBonus -contains 'True' }).Count -eq 0) 'Modeled duty is separate from the actual native default false specialist configuration'
$evidence['InstalledAssemblyEvidence'] = [ordered]@{ SystemHeatVersion=$heatAssembly.Name.Version.ToString(); ReactorBase=$reactor.BaseType.FullName; HeatModuleField=$heatField.FieldType.FullName; HeatLoopType=$loopProperty.PropertyType.FullName; ModulePersistentIdType=$identity.PropertyType.FullName }
foreach ($path in @($heatPath,$kspPath,$usiPath)) { $evidence.Sha256[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
$evidence['PassedContracts'] = $checks.ToArray()
$evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'power-operator-installed-provenance.json') -Encoding utf8
Write-Output "$($checks.Count)/$($checks.Count) installed read-only API/config contracts passed; no native game proof."
