# Expanse Foundations clock, depot view and delivery

The installed application has an independent KSP 1.12 bridge, a separate local Host, and a Windows Manager window. It shows KSP universal time, save/session identity, connection freshness, and resources of explicitly selected depot parts. There is no RMM dependency.

The installed recovery and delivery runtime uses the selected KSP save as authoritative accepted state; Host mirrors it and keeps a SQLite prepare/receipt journal. Physical debit and credit effects are supported for registered selected tanks on unloaded vessels through Background Resource Processing 0.2.7 when delivery readiness passes. Loaded vessels are not supported for physical delivery effects. Historical M3–M5 development fixture evidence remains separate from the production runtime. See [recovery and delivery usage](docs/RECOVERY-DELIVERY-USAGE.md), [package inventory](docs/RECOVERY-DELIVERY-PACKAGE.md), and [work evidence](docs/RECOVERY-DELIVERY-WORK-EVIDENCE.md) for scope and verification.

The shipping bridge is `Expanse.WorldBridge.dll` under `GameData/ExpanseWorldBridge/Plugins`. It is separate from the older `ExpanseFoundations` anchoring plugin and the kOS/MechJeb `ExpanseBridge` helper. Neither of those directories is replaced.

## Build and launch

On this Windows workstation, .NET SDK 8 and the .NET 8 Windows Desktop runtime are installed. The bridge builds for .NET Framework 4.7.2 against local KSP 1.12.5 managed references; game binaries are never distributed.

The existing [Launch-Expanse-Clock.cmd](Launch-Expanse-Clock.cmd) starts or reuses the installed Host and Manager; it does not start KSP. Development fixture runs use unique alternate pipes and a disposable Host data directory. From the workspace root, the build and guarded dev-game tools are:

```powershell
.\ExpansePlatform\tools\build.ps1
.\ExpansePlatform\tools\deploy-dev.ps1
.\ExpansePlatform\tools\launch-manager-host.ps1
.\ExpansePlatform\tools\launch-dev-muted.ps1
```

The second command deploys the bridge only to `C:\Users\griff\Documents\KSP-RMM-Dev`, backing up prior dev DLLs. The third starts or reuses the installed Manager and Host; development fixture runs use the alternate pipes and data directory described in the recovery/delivery usage document. The fourth launches only the approved dev KSP installation after backing up and muting its six audio volume settings. Both dev scripts reject aliases and production paths.

The development-only smoke harness and observer are in `dev/` and are absent from the shipping bridge. See [dev/README-clock-smoke.md](dev/README-clock-smoke.md) to reproduce a disposable-save pause, warp, and reload run. It requires an explicit one-shot request file and uses no desktop input.

Run `.\ExpansePlatform\tools\package-production-delivery.ps1` after Release builds to create a matching production Bridge/Domain/Host/Manager ZIP in `ExpansePlatform/artifacts`. It includes SQLite's native dependency and excludes KSP managed assemblies, dev fixtures, and saves. Packaging does not deploy or launch anything.

## Behavior

The KSP addon samples about twice per real second on Unity's main thread and atomically replaces one latest-sample slot. A worker sends bounded framed JSON over a current-user-only Windows named pipe. The Host validates and owns one publisher plus concurrent Manager readers. The Manager consumes the Host's typed view and never extrapolates UT: a paused game still sends the same UT with fresh receipt times, and high warp shows the latest value received. About three seconds without a new sample marks it stale; a main-menu/no-world sample clears the live clock. A new KSP process uses a fresh session ID. A load epoch identifies an observed KSP game-state-load or revert generation, and backward UT also rotates it. The dev run showed KSP fires game-state-load when moving from Space Center to Flight, so an ordinary scene transition can conservatively rotate the epoch in this version. Install path, save folder and load epoch are read-only clock context, not durable world or transaction identity.

`docs/CLOCK-MVP-DESIGN.md` is the architecture and acceptance contract. `docs/CLOCK-MVP-INTERFACE.md` records the framed protocol. `docs/WORK-EVIDENCE.md` separates synthetic and real-game results, along with observed rework.

## Depot view

The in-game registration window has a documented [UI redesign item](docs/UI-REDESIGN-BACKLOG.md) based on the first user screenshot. It is a future UI change; the current M2 registration behavior is described below.

In Flight, open the Expanse depot registration window from the stock toolbar. Resource-bearing parts start unchecked. Select the parts that belong to the depot, choose an anchor among them, review the preview, and confirm registration. A visiting ship's tanks stay excluded unless you deliberately select them. One depot is supported per save. Renaming the vessel does not change registration because the save stores part IDs. Unregistering removes only the Expanse depot metadata.

The Manager shows every resource present on the selected parts, with amount and capacity. If the depot or any selected part is unavailable, the stock view says so instead of presenting a partial total. Stock has its own freshness age; a fresh clock heartbeat does not make old stock fresh. The bridge observes loaded, unpacked Flight vessels and registered unloaded BRP 0.2.7 vessels for stock display; physical delivery effects require the unloaded BRP path and passing readiness. Docking and manual toolbar clicks were not exercised in the M2 game fixture; that fixture tested explicit exclusion of an unselected resource-bearing part on a single vessel. See `docs/DEPOT-VIEW-DESIGN.md` and `docs/DEPOT-VIEW-WORK-EVIDENCE.md` for its historical evidence.

The Manager rounds displayed resource amounts and capacities to whole units and groups thousands for readability. This does not round the inventory received from KSP or change any stored value.

## Colony inspector

The Manager's **Colony** tab groups read-only MKS/WOLF vessel observations by body and biome, separating physical tanks from an explicitly unconnected WOLF virtual ledger. It highlights missing receiving warehouse buffers and other inspectable converter setup problems. See [Colony inspector scope and installation](docs/COLONY-INSPECTOR.md) for observation limits, freshness, and verification status.
