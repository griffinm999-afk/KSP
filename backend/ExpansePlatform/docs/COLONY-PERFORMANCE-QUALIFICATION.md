# Colony performance qualification

Status: instrumentation is implemented; the latest native49 active-tick profile averaged 450.53 ms with p95 592.56 ms, missing the 1 ms p95 target by a wide margin. This is a single-scene measurement with no matched baseline; no native50 performance result exists. Matched-baseline and 100-facility/200-resident qualification remain pending. See [the measured runtime review](COLONY-RUNTIME-PERFORMANCE-REVIEW.md) for the earlier 167-second capture and its limits.

Launch the isolated development copy with `tools/prepare-colony-development.ps1 -Profile` after creating a coherent, reviewed binary snapshot. Profiling is disabled unless `-expanseColonyProfile` is present. Do not replace binaries in a running game. The development controller's `profileReset` and `profileReport` commands reset/read diagnostic counters only; they do not advance or edit the economic ledger. A report is written as `colony-profile-<operation>.cfg` inside the isolated root.

Each channel has a fixed 4,096-sample ring with observed, retained and overwritten counts, retained-window mean and nearest-rank p50/p95/p99, retained maximum and lifetime maximum since reset. No allocation is performed when adding a sample. Report sorting/allocation is outside the measured callback. Reset between cases; archive reports with their exact binary hashes, scene, save, warp rate and foreground/minimized state.

- `FrameCallback`: total ColonyRuntime.Update callback, including management endpoint work.
- `ActiveTick`: the scheduled simulation/observation/policy tick, excluding idle and paused callbacks. Do not use idle callbacks to claim the active tick budget passes.
- `ManagementEndpoint`: native main-thread request processing, including snapshot generation/serialization and any command.
- `Discovery`: bounded vessel discovery slice.
- `Environment`: native environment/provider observation for every caller, including UI requests and policies.
- `SimulationAndAcceptance`: deterministic engine advance and authoritative serialized acceptance.
- `AcceptanceSerialization`: serialization/hash before accepted state swap.
- `FrameInterval`: Unity's unscaled frame interval for the whole game, including unrelated mods and game work. It cannot be attributed to the colony runtime alone.
- `UtilityCatchUp`: the actual unloaded native BRP catch-up call and its validation; nested inside `UtilitySources`.
- `UtilityBackgroundAdapters`: installed adapter/provider configuration lookup; nested inside `UtilityBackground`.
- `UtilityBackgroundRecipes`: current native background recipe inspection; nested inside `UtilityBackground`.
- `UtilityConfigurationQuery`: the once-per-utility-observation installed adapter query, inside `EnvironmentUtilities` during environment builds. No persistent configuration cache is used.

Channels overlap and must not be added as independent costs. Independent rings cover different time spans when sample cadences differ. Compare counts and reset all channels together for a bounded matched capture. Passive external callbacks and exceptional placement need separate measurements; total frame comparison includes them, but ColonyRuntime.Update alone does not.

Acceptance targets from the architecture remain targets: p95 <= 1 ms and p99 <= 3 ms ordinary incremental observer/scheduler cost at 100 facilities and 200 residents, plus <= 10% total frame-time regression in the same scene and warp. Report actual evidence when a target is missed. A desktop .NET fixture is useful for finding algorithmic costs but does not establish Mono/KSP performance. Placement must separately record worst assembly/settling/anchoring hitches and cannot be hidden inside a routine percentile.

Required captures: ordinary loaded colony at 1x with app disconnected; same scene with app refreshing; anchored-packed facilities; Space Center/unloaded BRP; bounded catch-up and pause; model-scale 100/200 fixture; matched existing-scene baseline. An unrelated minimized/background state is not a valid foreground baseline comparison.

## Detached state copies (3 October source checkpoint)

`ColonyStateCodec.Copy` now uses generated typed detached copies instead of an encode/parse/decode round trip. Every mutable record/list is copied. The same validation and migrations still run. A fused counter checks the exact serialized UTF-8 size (including property keys, delimiters, escapes and surrogate pairs), 250,000-node bound and depth 64. A one-time reflected shape comparison fails closed when a persisted DTO is added without regenerating the copier.

Regenerate after persisted-record changes with `dotnet run --project dev/Expanse.Colony.CopyGenerator -- <absolute path to src/Expanse.Domain/Colonies/ColonyStateDetachedCopy.Generated.cs>`, then rebuild and run `ColonyDetachedCopyTests`. The generator does not call the runtime copier. The prospective native13 snapshot covers64 record types and720 properties, including production inventory claims;15 detached-copy/performance/scale checks passed at this shape. This does not update the earlier timing measurement below or establish native performance.

Eight independent copy cases plus the 100-facility/200-resident scale test passed. Tests populate every reflected persisted property, compare both exact JSON and an independent parser, assert complete mutable detachment, reject malformed UTF-16 and node overflow, and check the exact 4 MiB boundary. The 100-step fixture took 766.745 ms in this run versus 1,532.507 ms before the change; payload remained 149,356 bytes and fractional support consumption remained exact. These separate runs are indicative desktop measurements, not a controlled Mono benchmark or a passed runtime performance target. The typical step still needs native measurement and further optimization if necessary.
