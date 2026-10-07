$ErrorActionPreference = 'Stop'

$managed = 'C:\Kerbal Space Program\KSP_x64_Data\Managed'
[System.Reflection.Assembly]::LoadFrom((Join-Path $managed 'UnityEngine.CoreModule.dll')) | Out-Null
$bridgePath = Join-Path $PSScriptRoot '..\src\Expanse.WorldBridge\bin\Debug\net472\Expanse.WorldBridge.dll'
$bridge = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $bridgePath))

function New-BridgeObject([string]$name) {
    [Activator]::CreateInstance($bridge.GetType("Expanse.WorldBridge.$name", $true), $true)
}

function Set-BridgeField($item, [string]$name, $value) {
    $item.GetType().GetField($name).SetValue($item, $value)
}

function New-Sample([int]$depots) {
    $sample = New-BridgeObject 'ClockSample'
    $colony = New-BridgeObject 'ColonySnapshot'
    $wolf = New-BridgeObject 'WolfSnapshot'
    Set-BridgeField $sample 'Sequence' ([long]1)
    Set-BridgeField $sample 'Colony' $colony
    Set-BridgeField $colony 'Status' 'observed'
    Set-BridgeField $colony 'Wolf' $wolf
    Set-BridgeField $wolf 'Status' 'observed'
    Set-BridgeField $wolf 'AllowedResources' ([string[]]@('Power'))

    $vessel = New-BridgeObject 'ColonyVessel'
    Set-BridgeField $vessel 'VesselId' 'a3a450be-d32e-4c03-9ed1-681382d90f75'
    Set-BridgeField $vessel 'Name' 'Minmus Base'
    Set-BridgeField $vessel 'Body' 'Minmus'
    Set-BridgeField $vessel 'Biome' 'Greater Flats'
    Set-BridgeField $vessel 'ObservationBasis' 'snapshot'
    $colony.Vessels.Add($vessel)

    for ($i = 0; $i -lt $depots; $i++) {
        $depot = New-BridgeObject 'WolfDepotRow'
        Set-BridgeField $depot 'Body' 'Minmus'
        Set-BridgeField $depot 'Biome' ("Biome $i")
        Set-BridgeField $depot 'Established' $true
        for ($j = 0; $j -lt 80; $j++) {
            $row = New-BridgeObject 'WolfResourceRow'
            Set-BridgeField $row 'Name' ("Resource-$j-" + ('X' * 40))
            Set-BridgeField $row 'Incoming' 100
            Set-BridgeField $row 'Outgoing' 20
            Set-BridgeField $row 'Available' 80
            $depot.Resources.Add($row)
        }
        $wolf.Depots.Add($depot)
    }
    return $sample
}

$jsonMethod = $bridge.GetType('Expanse.WorldBridge.WorldBridgeAddon').GetMethod('Json',
    [System.Reflection.BindingFlags]'NonPublic, Static')

foreach ($case in @(
    @{ Depots = 3; ExpectedStatus = 'observed' },
    @{ Depots = 12; ExpectedStatus = 'truncated' }
)) {
    $sample = New-Sample $case.Depots
    $json = [string]$jsonMethod.Invoke($null, @($sample))
    $bytes = [System.Text.Encoding]::UTF8.GetByteCount($json)
    if ($bytes -gt 65536) { throw "Frame exceeds 64 KiB: $bytes bytes" }
    $payload = ConvertFrom-Json -InputObject $json
    if ($payload.colony.vessels.Count -ne 1) { throw 'Physical vessel was lost.' }
    if ($payload.colony.wolf.status -ne $case.ExpectedStatus) {
        throw "Expected WOLF status $($case.ExpectedStatus); got $($payload.colony.wolf.status)."
    }
    if ($payload.colony.wolf.depots.Count -ne $case.Depots) { throw 'WOLF depot identity was lost.' }
    $rows = @($payload.colony.wolf.depots | ForEach-Object { $_.resources.Count } | Measure-Object -Sum)[0].Sum
    if ($rows -le 0) { throw 'WOLF resource rows were lost.' }
    if ($case.ExpectedStatus -eq 'truncated' -and $rows -ge ($case.Depots * 80)) {
        throw 'Oversized ledger was not clipped.'
    }
    Write-Output "PASS: $($case.Depots) depots, $rows rows, $bytes bytes, WOLF $($payload.colony.wolf.status)"
}
