# Depot M2 development smoke fixture

This is a one-shot, dev-only KSP harness for the exact `C:\Users\griff\Documents\KSP-RMM-Dev` installation. It creates one uniquely named save after validating and consuming `ExpanseDepotM2Smoke.request`; it does not load, overwrite, or edit another save. The M1 clock harness remains separate and unchanged.

Build from the workspace root:

```powershell
dotnet build ExpansePlatform/dev/Expanse.Depot.M2.SmokeHarness/Expanse.Depot.M2.SmokeHarness.csproj -c Release
dotnet build ExpansePlatform/dev/Expanse.Depot.Observer/Expanse.Depot.Observer.csproj -c Release
```

The harness compiles against the KSP managed assemblies and bridge DLL already in the exact dev install. Review its output at `ExpansePlatform/dev/Expanse.Depot.M2.SmokeHarness/bin/Release/net472/Expanse.Depot.M2.SmokeHarness.dll`. Stage that reviewed DLL manually at `C:\Users\griff\Documents\KSP-RMM-Dev\GameData\ExpanseDepotM2Smoke\Plugins\Expanse.Depot.M2.SmokeHarness.dll`; preserve a backup of any preexisting destination. Never stage it in production or distribution trees.

Run `prepare-depot-m2-smoke.ps1` only after staging. It enforces the exact dev root, checks path ancestors for reparse points, backs up and mutes the six audio settings, and creates a fresh request. A running KSP process is allowed only if its executable is under the explicitly configured known production root (default `C:\Kerbal Space Program`); a dev-root or unknown KSP process blocks request creation. The script does not launch KSP.

Use isolated pipe names so the production Host and publisher remain untouched. In one terminal, start the Release Host with both named endpoints:

```powershell
$token = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$publisherPipe = "ExpanseFoundations.Clock.Publisher.dev.$([Environment]::UserName).depotm2-$token"
$viewPipe = "ExpanseFoundations.Clock.View.dev.$([Environment]::UserName).depotm2-$token"
dotnet .\ExpansePlatform\src\Expanse.Clock.Host\bin\Release\net8.0\Expanse.Clock.Host.dll --publisher-pipe $publisherPipe --view-pipe $viewPipe
```

In another terminal, start the bounded observer on the isolated view endpoint, then launch the exact dev KSP via the dev-only launcher so the same publisher endpoint is passed into KSP:

```powershell
dotnet .\ExpansePlatform\dev\Expanse.Depot.Observer\bin\Release\net8.0\Expanse.Depot.Observer.dll --duration-seconds 240 --view-pipe $viewPipe --output (Join-Path $env:TEMP ("ExpanseDepotM2-observer-$token.jsonl"))
```

```powershell
.\ExpansePlatform\tools\launch-dev-muted.ps1 -PublisherPipeName $publisherPipe
```

The addon has a ten-minute global watchdog. It launches the installed Making History `Muna 1` stock craft into a new disposable world, chooses two loaded unpacked resource-bearing parts, records every resource amount and capacity on them, and calls public `DepotRegistryModule.Register(label, anchorPersistentId, ids)`. It logs a third unselected resource-bearing candidate and verifies the bridge sample matches the two selected parts alone. It programmatically renames only its disposable vessel and verifies `currentVesselName`, performs a normal save and same-save reload, then verifies world/depot identity, membership, rename, and stock values persisted. Immediately before saving, it captures the active owned Flight world's bridge `loadEpoch`; bounded reload readiness requires `onGameStateLoad`, Flight GUI-ready or flight-ready, stable Flight/current-game state and owned save folder, plus a different bridge `loadEpoch` read directly from the addon's private field. It does not rely on the transient pending sample slot or `onGameStatePostLoad`. The fixture does not modify resources. It proves exclusion for this loaded single-vessel candidate but does not construct or test an actual docked visitor. On PASS or FAIL it restores warp index 0/unpaused state, logs, and exits KSP. The fixture calls `DepotRegistryModule.CloneView` by reflection for in-game assertions; the external observer separately records the actual typed Host `ClockView` and `DepotView`.

The text log and retained save are named `ExpanseDepotM2Smoke-<token>.log` and `saves/ExpanseDepotM2Smoke-<token>`. Keep the save for inspection; remove only that exact unique folder after the dev game has closed and logs are reviewed.
