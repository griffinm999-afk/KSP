# ExpansePlatform review context

## Architecture

1. The C# KSP WorldBridge captures Unity-owned state on the game thread. Resource observations retain actual accepted transfers and their original identity and interval.
2. A bounded latest-snapshot worker performs grouping, sorting, deduplication, budget/census/power calculations, and serialization on immutable captured data. Review thread ownership and mutable-object escape.
3. The Host serves cached observations over the protected local transport; the Manager is optional. Relevant source names include `Clock.Core`, `ColonyObservation`, `ProductionFrameBudget`, `ProductionObserver`, `ProductionBackground`, `ColonyManagementWire`, `ColonyManagementEndpoint`, and `ColonyRuntime.Management`.
4. The installed hidden SiteRelay publishes telemetry to the owner's private Colony Site. WOLF receipt/claim/recovery paths are disabled in that telemetry-only runtime. Historical Site relay code can support WOLF operations and must not replace the installed relay.
5. The Site uses plain JavaScript, a Cloudflare Worker, and D1. Worker/server modules qualify telemetry, track observations, and manage assignments and lifecycle history. `dist/index.html` is canonical HTML/CSS source despite the directory name. `build.mjs` embeds modules/data/assets into the built Worker.
6. Production UI is split across observation normalization, the supply-chain model, flow totals, Sankey rendering, and UI modules. v67 adds `demand-observation.js` and `demand-flow.js` for passive demand and completed-minute power handling.

## Highest-priority invariants

### Preserve measured meaning

Actual throughput comes from accepted transfers, never inventory deltas, nominal recipe coefficients, population estimates, or prepared potential. Measured zero, missing/null, and configured/gross intent are distinct. Preserve original sample/capture identity, context epoch, module/recipe identity, ordering, and interval. Repeated transport heartbeats must not renew old samples.

Native loaded/packed support must invalidate at both transition events before the packed flag changes. Do not label a loaded-vessel background-processing cache as Actual. Reset across world/session/load changes and game-time rewind.

### Passive USI life support

Read the included `USI-EC-WIRE-CONTRACT.md`, `USI-CONTRACT-VALIDATOR-DETAILS.md`, and backend handoff documentation for exact field types, accepted statuses, boundary tolerances, source locations, and commands. The additive per-vessel `lifeSupport` contract has independent supply and crew-electricity streams. Accepted Supplies consumption, stored Mulch production, and crew ElectricCharge consumption are the actual quantities; divide by the original game interval for a physical rate. Stored Mulch excludes dumped excess.

Review owner association, capture order, catch-up intervals, shortage handling, and per-resource completeness. Catch-up intervals ending before the sample time are historical. Gross/configured intent stays separate. Do not apply recycling twice or reduce crew electricity demand by a Supplies recycler multiplier. Exact USI DLL compatibility and unsupported/unloaded/EVA/direct-write coverage still require careful qualification; do not infer complete coverage from passing automated tests.

### Completed minute-power windows

The producer accumulates accepted transfers over at least 60 seconds of monotonic real time. A downstream latest-value snapshot cannot recover overwritten callbacks.

- Physical rate denominators use covered game time, with a common denominator for overlapping/concurrent modules.
- Unknown coverage gaps are not measured zeros.
- Completed windows have immutable identity and bounds. Repeating a window must not redate, recompute, or recount it.
- Aggregate vessels only when window identities and bounds are compatible. Preserve partial-coverage labels.
- Completed minute windows expire after 120 real seconds. Instantaneous samples use separate 10-game-second validity; never route a minute window through instantaneous expiry.
- Minute-window consumption already includes crew EC. Do not add crew EC again.
- Convert to units per six-hour Kerbin day with 21,600 game seconds exactly once; do not apply another warp/time factor.

### Site flow and lifecycle behavior

Crew Supplies/EC sinks and Mulch sources must be consistent between totals and Sankey rows. Preserve unknown remainders and the Measured subtotal label: crew coverage does not prove complete colony coverage. Nominal per-Kerbal planning demand must stay outside Actual.

Preserve dynamic resource coverage and saved/current recipe deduplication. Suppress a saved duplicate only on a unique current part/recipe/bay/output match; retain genuinely missing or ambiguous entries. Known branches remain visible even when other branches are unknown. Review v67's same-UT reconnect invalidation alongside sequence ordering, context changes, and retained history.

### Bounds and side effects

The entire UTF-8 ClockView response body is bounded at 262,144 bytes; tests cover 262,144 accepted and 262,145 rejected. WOLF/Effects remain bounded at 65,536 bytes. Check whole-frame accounting, truncation/completeness signaling, malformed input, census retirement, and disabled gameplay paths. Observers must not mutate resources, settings, commissioning, or game progression.

## Recorded verification and remaining acceptance

These are prior project results. Read the packaged component evidence for exact commands and scope; they are not new checks run by Claude.

- Native-packed installation: 40 runtime hashes verified; only the Bridge game DLL changed; 127 saves unchanged.
- Native loaded/packed callbacks advanced for three active harvesters, six Agriculture bays, and Duna, without modeled substitution. A screenshot verified Duna plus Agriculture Supplies Actual at 1,439.61/day. Atlas Gypsum 2.194179/second was verified upstream; an end-to-end Gypsum Actual screenshot remained outstanding at that checkpoint.
- The staged USI/minute-power work reported 625 Clock, 65 telemetry/producer/USI/averaging, 26 detached-converter, 29 qualification/epoch, 5 hook-registration, and 10 wire-replay checks, plus roster/census/capture and archive hashes. Primary integration and final source verification subsequently passed.
- The primary integration changed 23 approved source files and preserved 364 unrelated `.cs`/`.csproj` files. Recorded final source hashes cover 387 files. An older archive README saying primary source was untouched is superseded by the integration record. Rebuilt primary DLL hashes differ from staged binaries because of build location; matching source hashes establish source identity.
- Site v67 passed 26 test files plus SVG/DOM/Worker checks. Full authenticated live browser acceptance is distinct from those checks.
- The new backend update remains uninstalled; live USI/minute-power acceptance is pending. Accounting transport/initialization is separately unfinished and must not be conflated with accepted production telemetry.

## Next test proposals

Prioritize repeated/out-of-order captures; exact epoch changes; game-time rewind; pause/unload; both packed transitions; zero versus missing/malformed fields; one missing life-support resource; catch-up intervals; recycler and crew-EC double counting; overlapping callbacks; unknown coverage gaps; incompatible minute windows; real-time window expiry; same-UT reconnects; exact UTF-8 bounds; ambiguous saved recipes; and crowded flow graphs with more than five nodes.

The initial task is review-only. Propose these tests and specify their prerequisites. Do not build, install, run the game, invoke a live endpoint, deploy, or write files without a separate instruction.
