# Recovery and delivery usage

## Modeled Ore export (staged source, October 1)

The new Ore export feature is implemented in workspace source and synthetic tests; this section does not imply it is installed in the live game. In Delivery setup, choose the registered Minmus Mining source in **Ore exports to Kerbin**, retain travel time **3:00:00** (three six-hour Kerbin days, 64,800 game seconds), and save the export route. Once the route is accepted in the game, select it under Saved route and save an automatic order. This order exports whenever a fresh supported source inventory can supply one complete configured Ore batch. Activity supports pause/resume/cancel and shows cargo in transit.

To change an existing export order's quantity or move it from the historical price, save the new version under the same export route name, select Edit on the existing order, choose that newer saved route version, and save the order. Its enabled or paused status is preserved. Previously dispatched cargo is unchanged.

Choose **Ore per trip** as 1 to 1,000,000 whole units; the default is 1,000. New routes pay the fixed **100 funds/unit**, and the form previews the total compensation (default 100,000 funds). Saving a route configures terms; exports remain inactive until its automatic order is enabled. The app models travel, landing and recovery without spawning a vessel. Dispatch removes actual source Ore; funds change only at due modeled recovery. This is the complete cargo compensation; no ordinary vessel recovery value is added. Career Funding capability and the supported unloaded BRP inventory are required. If funds settlement capability is unavailable, source Ore is not debited for a new export. Travel and quantity changes create a new route version; departed cargo retains its original route and price. Historical 500/unit shipments already in transit still pay 500,000 funds for their 1,000 Ore; no new dispatch uses those historical terms.

The export card shows the latest retained payment witness. This is the balance observed after that payment, not a current funds balance or complete lifetime earnings total. Receipts may be compacted. An uncertain funds outcome is a blocking Activity issue; it preserves cargo evidence and must be reconciled instead of automatically paying again. Quickload restores the selected save's resources, funds and accepted state. Host history does not override it. See `ORE-EXPORT-DESIGN.md` for the callback, retry and persistence boundary and remaining in-game gate.

Expanse Foundations is installed with recovery and delivery support. Background Resource Processing 0.2.7 provides physical debit and credit effects for registered selected tanks on unloaded vessels when delivery readiness passes. Loaded endpoints are not supported for physical delivery effects. The earlier M3a–M5 fixture runs are historical development evidence, separate from the installed runtime.

The selected KSP save is authoritative. Host schedules work and keeps a SQLite prepare/receipt journal and mirror of accepted state; its database does not replace the selected save. Start the installed Manager and Host with `Launch-Expanse-Clock.cmd`; it does not start KSP. For an isolated development run, start Host on four unique per-user `dev` pipes with a unique `--data-dir`, then point Manager and the guarded dev KSP launcher at the matching names. For example, from the unpacked development archive:

```powershell
$token = [guid]::NewGuid().ToString('N')
$userName = [Environment]::UserName
$publisher = "ExpanseFoundations.Clock.Publisher.dev.$userName.$token"
$view = "ExpanseFoundations.Clock.View.dev.$userName.$token"
$effects = "ExpanseFoundations.Effects.dev.$userName.$token"
$commands = "ExpanseFoundations.Commands.dev.$userName.$token"
$dataDir = Join-Path $PWD "run\$token"
$archiveRoot = (Get-Location).Path
$hostDir = Join-Path $archiveRoot 'Host'
$managerDir = Join-Path $archiveRoot 'Manager'
$quotedDataDir = '"' + $dataDir + '"'
Start-Process (Join-Path $hostDir 'Expanse.Clock.Host.exe') -WorkingDirectory $hostDir -WindowStyle Hidden -ArgumentList @('--publisher-pipe',$publisher,'--view-pipe',$view,'--effects-pipe',$effects,'--command-pipe',$commands,'--data-dir',$quotedDataDir)
Start-Process (Join-Path $managerDir 'Expanse.Clock.Manager.exe') -WorkingDirectory $managerDir -ArgumentList @('--view-pipe',$view,'--command-pipe',$commands)
```

The guarded source-workspace `tools/launch-dev-muted.ps1` takes the matching `-PublisherPipeName` and `-EffectsPipeName`. It only launches the approved disposable dev install; the one-shot physical-effect fixture must be separately and explicitly activated for writing tests. Do not use the normal pipe names or default Host data directory for development.

The Manager is read-only when no matching live Bridge has attached. Route and rule edits are requests to the Host and become accepted only after a matching Bridge returns a verified capsule. `pending` means prepared and awaiting Bridge application; `accepted` means the full resulting state was verified. A send-once retry reuses its original request ID after a lost response; do not create a new request to retry an uncertain operation. Stale or mismatched world/run context is unavailable for editing. The M5 loaded development run accepted one keep-stock and two repeat dispatches, disabled both rules, credited three arrivals, and reloaded the selected Flight save with matching capsule and stock rows. Physical delivery support for unloaded endpoints requires BRP 0.2.7 and a passing readiness check; loaded endpoints are unsupported.

No delivery vessel appeared in the logged effect windows; newly observed SpaceObject asteroid/comet IDs were separate. `GoOnRails` remained `loaded=true, packed=true`, which is not an unloaded transition.

For source verification, run the synthetic suite and build the shared Domain, Host, Manager and KSP Bridge Release outputs:

```powershell
dotnet test .\tests\Expanse.Clock.Tests.csproj -c Release
dotnet build .\src\Expanse.Domain\Expanse.Domain.csproj -c Release
dotnet build .\src\Expanse.Clock.Host\Expanse.Clock.Host.csproj -c Release
dotnet build .\src\Expanse.Clock.Manager\Expanse.Clock.Manager.csproj -c Release
dotnet build .\src\Expanse.WorldBridge\Expanse.WorldBridge.csproj -c Release -p:KspManagedDir='C:\Kerbal Space Program\KSP_x64_Data\Managed'
```

`--enable-dev-counter` is accepted only with the full isolated Host configuration above and is unnecessary for ordinary observation. Development KSP launch/deploy scripts enforce the approved dev install, mute audio, back up the dev settings file, and reject unresolved or same-dev-game processes; use them only for a coordinated disposable fixture. The runtime gate evidence distinguishes synthetic tests from in-game observations.

See [runtime package and backup manifest](RECOVERY-DELIVERY-PACKAGE.md) for the files needed at runtime, dev-fixture removal, and installation backup records.
