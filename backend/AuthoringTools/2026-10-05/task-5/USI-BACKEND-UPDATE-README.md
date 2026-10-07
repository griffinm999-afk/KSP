Staged passive USI-LS and minute power update

The backend now reports accepted Supplies consumed, accepted Mulch stored, and crew EC consumed from independent native USI callbacks. It binds the exact native getter result to the owner, converter, crew part, broker, context and mode epoch. Observer code reads populated backing fields; it never invokes USI settings/state/recipe getters, changes native arguments, or uses time-left fields or inventory deltas as counters. Native calls and exceptions retain their original behavior.

Minute power reporting accumulates every supported accepted EC transfer between completed reports. Unpacked vessels use the existing fulfilled Part-request observer's complete original intervals; packed vessels use qualified producer and crew callbacks and always report partial owner coverage. Original processed endpoints are retained for stock producers and USI catch-up. Parallel intervals are merged before computing duration. Complete windows have a shared identity and bounds; gaps remain partial. The means cover observed game time, while reporting uses at least 60 monotonic real seconds. The existing instantaneous fields remain for compatibility. Core validates the new optional fields and retains the existing invalid-colony fallback and 256 KiB frame limit.

See USI-EC-WIRE-CONTRACT.md for exact fields, units, expiry, aggregation and frontend display rules. Mulch produced here means accepted physical Mulch stored; dumped excess is excluded. Crew EC is already included in the power window consumption, so the Site must not add it again. No assumed 51-crew total: per-stream crew count describes qualified coverage. Unloaded BRP, EVA and unsupported owners remain unknown.

Validation

- 65 producer/USI/averaging tests: all 41 original producer tests plus 24 new provenance, completion, shortage, interval, averaging, gap, expiry and reset checks.
- 625 full Clock/domain/Host tests: all 621 original tests plus four full sample/view, fallback and crowded-frame cases.
- Detached copied installed ProcessRecipe IL: 14 prior producer cases and 12 USI shortage/storage/EC cases, with a synthetic broker. These execute no game lifecycle or live resource operation.
- 29 detached checks against actual staged Bridge qualification and interval/mode helpers, installed USI metadata and fully initialized synthetic native recipes. Fixtures never attach to KSP.
- Five passive hook registration/cleanup checks under .NET Framework. An initial registration attempt under .NET 8 failed in the installed Harmony detour self-test; the separate Framework registration test passed. Data/accounting tests remain under .NET 8 without detour initialization.
- Ten synthetic actual Bridge ColonyJson -> Core sample decoder -> full view/frame cases.
- Existing roster/census/seat/fallback and capture ownership/tank/census/power/timing checks passed.
- Bridge, Host, Manager, BrpColony and TrackingStation built successfully. Build logs and SHA256 manifest accompany the package.

Installation requirements

This is a tested staged candidate, not a live qualification. No installed binaries, running processes, game resources, saves, USI settings, financial state or relay settings were changed.

1. Fully exit KSP before replacing Expanse.WorldBridge.dll. Do not hot-swap a loaded Mono plugin.
2. Stop Host and any Manager using the shared Core assembly, install the coherent packaged Host/Manager files, then start Host. Core's optional record additions are wire-compatible but compiled callers should use the rebuilt set.
3. Integrate Source changes against the recorded original hashes. The primary repository remains untouched by this staging task. Only the Bridge plugin changes are required inside GameData; unchanged plugins and Domain need no replacement.
4. The existing telemetry-only relay can reconnect to the restarted Host; this update neither configures relay credentials nor enables WOLF commands. No Site deployment or relay transmission was performed here.
5. After KSP is relaunched, verify a qualified USI callback and one completed reporting window with read-only telemetry. Confirm independent supply/EC coverage, configured versus actual quantities, recycler parameter, original catch-up endpoint, mode invalidation and zero versus unknown. Live callback behavior and runtime cost remain to be checked; detached registration is not proof of in-game execution.

Cross-thread delivery of the early contract was rejected by automatic approval review for missing direct human authorization to message another task. No alternative messaging route was used. The contract and checkpoint are delivered as local artifacts and in the final delegated result.
