Review-only handoff: passive USI Life Support and minute power telemetry

Primary source is `C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform`. It is a plain source directory, not a Git checkout (both `git status` and `git rev-parse` report no repository). The approved 23 source files were integrated using per-file original SHA256 guards and backups. All 364 unrelated pre-existing .cs/.csproj files retain their before-integration hashes. No binaries were installed, processes stopped, or game/save/settings/financial state changed.

Evidence workspace is `C:\Users\griff\Documents\Codex\2026-10-05\task-5`:

- Staged full source: `usils-work`.
- Immutable patch/package: `ExpansePlatform-passive-usils-minute-power-20261006`, with `Source/changes.json` listing original and candidate SHA256 for every affected file. `Source/` contains final candidate files; these match integrated primary exactly.
- Original changed files: `usils-source-backup-20261006-181822`. New files have null original hashes in changes.json.
- Integration record: `usils-source-integration-result.json`.
- All 387 final primary .cs/.csproj hashes: `usils-primary-final-source-hashes.json`.
- Contract: `USI-EC-WIRE-CONTRACT.md`; precise validator and paused/unloaded clarification: `USI-CONTRACT-VALIDATOR-DETAILS.md`.
- Archive: `ExpansePlatform-passive-usils-minute-power-20261006.zip`, SHA256 `aeb41ff8471ef1200d75feeabdc864824903339cf0eb2cf4ff54c71a8a13ff40`. ZIP CRC and all 77 manifest hashes verified during integration. Archive remains unchanged/coherent, including staged Bridge and identical Host/Manager Core assemblies. Source integration does not change archive metadata documenting original staging state.

Review scope and priorities

1. Verify passivity and provenance in `ColonyRuntime.LifeSupportObserver.cs`, `ColonyLifeSupportTelemetry.cs`, `LifeSupportTelemetryMath.cs`, and shared `ColonyProductionTelemetry.cs`. Exact native getter result references are one-use bound to owner/converter/part/broker/context/mode; null resModule is tested by ReferenceEquals. No observer invokes lazy USI getters or mutates native arguments; accepted broker transfers are measured. Supply and EC are independent, accepted stored Mulch excludes dumped excess, zero remains distinct from unknown. Native exceptions and flow must be preserved.
2. Inspect `PowerAverageTelemetryMath.cs`, `ColonyPowerTelemetry.cs`, producer/crew feed points and mode transitions. Every supported accepted callback contributes once; original historical processed endpoints are retained. Parallel intervals are unioned before duration. Reporting cadence uses monotonic real time >=60 seconds, mean denominator is covered game time, packed owner coverage stays partial. Check mode/load/context rollback invalidation, interruption/gap semantics, bounded caches, and paused historical display. Untransmitted interval sets prevent reconstructing common partial coverage across vessels.
3. Check optional wire validation in Core `ColonyLifeSupportTelemetryProtocol.cs`, `ColonyProtocol.cs`, `ClockProtocol.cs`, Bridge serialization/snapshot ownership and 256 KiB fallback. Preserve legacy frames, census, native producers, worker isolation and accounting. Crew EC already contributes to total observed consumption and must not be added twice downstream.

Verification commands (run from primary; run sequentially to avoid shared Core build-output file locks)

```powershell
dotnet build src/Expanse.WorldBridge/Expanse.WorldBridge.csproj -c Release
dotnet build src/Expanse.Clock.Host/Expanse.Clock.Host.csproj -c Release
dotnet build src/Expanse.Clock.Manager/Expanse.Clock.Manager.csproj -c Release
dotnet test tests/Expanse.Clock.Tests.csproj -c Release
dotnet test dev/Expanse.ProductionTelemetry.Tests/Expanse.ProductionTelemetry.Tests.csproj -c Release
```

Final primary results: Bridge, Host and Manager builds pass; Clock 625/625 and producer/USI/averaging 65/65 pass. Initial concurrently run Clock build encountered a shared Core file lock; sequential rerun passed. Existing nullable warnings remain in test-linked source, with no Bridge build warnings/errors.

From evidence workspace, `verify-usils-primary.ps1` builds Host/Manager and runs existing roster/performance checks plus detached native tests and Framework hook registration. `verify-usils-primary-wire.ps1` replays ten synthetic DTO cases through actual primary Bridge serialization and Core decode/view/frame validation. Logs use `usils-primary-*`; staged original logs and counts are in `USI-BACKEND-UPDATE-README.md`. Detached native tests cover 26 copied ProcessRecipe IL cases and 29 actual Bridge qualification/mode/endpoint checks; Framework hook registration has five checks. No fixtures attach to the running game.

Known limits: live native USI callback behavior and runtime cost remain unverified until installation/relaunch. Exact installed USI assembly hash is guarded; another version disables observation. EVA, unloaded BRP, unsupported owners and direct resource writes remain unknown. Supply observations expire after 10 game seconds; completed windows have independent real age, maximum 120 seconds. Configured demand is intent, accepted quantities are actual observed transfers. No fixed global crew population or custom consumption rate is assumed. Harmony registration under .NET 8 failed its detour runtime self-test in an earlier detached attempt; Framework registration passed, and .NET 8 data tests do not initialize detours.

Installation readiness: coherent staged package is ready for review. KSP must fully exit before replacing BridgeDLL; Host and any Manager using Core must stop before replacing the coherent rebuilt set. Only changed Bridge plugin is required inside GameData. Existing telemetry-only relay can reconnect; no WOLF commands or credential changes are part of this update. Installation/restart is pending separately. Please review local source and report findings with file/line and consequence; do not deploy, mutate saves/resources/settings, or transmit private runtime fixtures.
