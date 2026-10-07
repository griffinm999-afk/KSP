# Native reactor commissioning and bounded background continuation

Source contract as of 2026-10-03. Native acceptance remains pending. No production save or installation was edited for this implementation.

## Ownership and initial activation

The installed SystemHeat 0.9.1.0 modules remain the only loaded reactor fuel consumer, waste producer, electricity producer and thermal simulator. BRP 0.2.7.0 remains the only unloaded resource producer. Its installed `SelectFirst` reactor adapter supports real fuel/waste and EC conversion, and its radiator adapter accounts native operating resources. It does **not** simulate SystemHeat thermal loops while unloaded.

`PumpConstruction` first verifies the actual complete paid craft, marker-to-persistent-part mapping, anchored Base vessel and Foundation lineage. A new `constructionActivation` child then binds that exact order, placement operation, craft hash, escrow, facility and physical membership. Both before and intended settings witnesses are bounded and serialized before the first native action. The applying state becomes the selected-save authority before native events. This uses the existing scenario save guarantees: the authoritative state is captured by the next KSP save, rather than an independent disk fsync.

Native API signatures and PAW controls are checked before any mutation. Initial commissioning activates actual extended radiator modules, requests native manual control, sets the public persisted `CurrentReactorThrottle` PAW control to 100, and invokes `EnableReactor`. It never sets simulated `CurrentThrottle`, heat, core temperature, integrity, fuel, waste or EC amounts. These settings permit a genuine full-load test even when batteries are full; BRP's manual branch dumps excess EC as its installed policy specifies.

Native settings readback completes the child. A saved applying/held child can reconcile only when its exact target settings are observed. Before or partial state remains held without repeating native events. Completed activation authority is retained during terminal-effect compaction. A later user shutdown is respected and never causes automatic reactivation. Successful activation is separate from successful utility/thermal qualification.

## Loaded proof

Actual native reactor time, successful fuel check, full-throttle curves, core integrity and thresholds, native resource flow paths, local waste room and shared stock endurance are observed. Unequal overlapping fuel paths and other active recipes sharing scarce fuel/waste are held unless a separate endurance allocator is available.

Each actual heat loop must contain only accounted producers/rejection hardware. Active physically extended same-loop radiator capacity at nominal temperature must exceed full-load heat by at least 10%. Observed radiative rejection must cover full heat; no convection bonus is credited. Core shutdown/damage margin must be at least 100 K, and loop operating margin at least 10 K.

Passive final native `HeatLoop.Simulate` and radiator `FixedUpdate` callbacks establish freshness. Re-reading static packed fields cannot accrue a proof. The exact installed `SystemHeatVessel.FixedUpdate` has no packed exclusion, but native active-vessel initialization is still required and must be verified in acceptance. Every proof requires 30 continuous UT seconds at 1x, genuine manual 100% output and fresh native callbacks. Every positive core and loop interval trend is recorded; an average cannot hide a recent heat-up. Context, configuration, time reversal, observation gaps and native failures invalidate the in-memory observation.

The saved nullable qualification proof seals actual part/module IDs, installed part configuration, thermal loop IDs, settings, deployment, capacities/flow flags, variants, provider configuration and actual recipe. Changing resource amounts and measured temperatures are excluded from the hardware seal; current stock and current loaded thermal evidence are checked separately.

## Offline conditional estimate

The proof explicitly describes a **modeled, conditional thermal continuation**, not fresh temperature measurement or offline heat simulation. Its maximum horizon is bounded by actual full-load fuel/waste endurance, a finite model limit of the greater of twice the charter reserve or seven Kerbin days, and at most 365 Kerbin days. Positive trends further bound it by `(core margin - 100 K) / core rise` and `loop operating margin / loop rise`. The allowed duration is conservatively floored before adding UT. A proof with a small positive trend cannot justify six days merely because its trend is below 0.01 K/s. The entire current requested reserve must fit the remaining horizon. Loading and obtaining a new actual stable window can renew the saved proof; the runtime accepts renewal at most once per 60 UT seconds. A fresh failure or unsafe/unproven full-power observation in the exact loaded vessel/context revokes its saved proof, including while unrelated effects are held. Missing, unloaded, stale, future or other-context observations cannot revoke another proof. Stable regrant remains blocked by unresolved effects and detaches the observation object before acceptance.

Before unloaded qualification, the one native BRP processor catches up. Exact actual FlightId/ModuleId recipe identity, full-rate native constraints, real Pull/Push indices and synchronized provider-owned physical proto tanks are verified. Virtual inventories are excluded. Fuel amounts and local waste room must cover the current reserve. Unequal overlapping paths and competing native consumers hold. The same saved hardware/provider/recipe hashes and selected-world identity must match. The adapter neither generates stock nor duplicates BRP conversion.

## Verification and remaining native evidence

Sixteen pure tests cover remaining-horizon boundaries and nonfinite time, actual stock depletion, changed providers/hardware, trend-limited expiry, rejection/headroom, continuous/gapped/changed-world observations, applying/partial/lost-ack readback, exact paid lineage, conservation, unrelated uncertainty, terminal compaction, scoped fresh revocation and detached saved-proof copying/roundtrip. Forty-two read-only installed utility API/config checks include the actual public `PersistentId` getter, native activation methods, persisted PAW throttle and packed thermal call path. These establish code/API contracts, not a game certificate.

The isolated integrated acceptance must still verify: genuine paid power placement and startup content debit; native reactor/radiator activation readback; no partial-save retry; actual fresh anchored-packed thermal callbacks at stable full output; saved proof after pause/save/reload; loaded/packed/unloaded fuel, waste and EC quantities under the native providers; expiry and settings-change holds; and the startup residents/support loop with the current exact power package. Test-harness-only activation cannot stand in for a paid product commissioning receipt.

Primary source review used the exact [SystemHeat 0.9.1 commit](https://github.com/KSPModStewards/SystemHeat/tree/03192e329ab8944b66a5c004c03b1b7596b0b6ad), checked against installed DLL members and the installed BRP `Config/SystemHeat.cfg`. Cached read-only source material is under `run/systemheat-source-review`.
