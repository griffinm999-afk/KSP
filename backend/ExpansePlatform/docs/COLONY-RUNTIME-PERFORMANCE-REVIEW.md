# Runtime performance architecture review — 3 October 2026

The original source review below preceded native profiling. Its original broad suite had330 passing checks; newer regression counts and binary boundaries are tracked in the resume record. The review itself did not change the runtime.

## Native13 measured checkpoint — 12:35 UTC

The frozen native13 run now has an actual 167.019-second, 328-active-tick capture at 1x in unpaused Duna flight, with the management app disconnected. It contained one colony, nine unloaded Minmus facilities, zero residents and two loaded Duna vessels. No placement, survey, scene change or warp occurred during the captured window. Evidence: `outputs/colony-placement-audit/native13-profile-duna-idle/profile-review.json`; raw report SHA256 `224F8257BB5A8543996AD344384DAEF2E4FB8F2FEAEC2D1B8EEB28AF4A1BFDC6`.

| Channel | Mean ms | p95 ms | p99 ms |
| --- | ---: | ---: | ---: |
| ActiveTick | 23.073 | 27.929 | 42.548 |
| Environment | 20.369 | 22.720 | 31.864 |
| SimulationAndAcceptance | 1.755 | 2.133 | 5.472 |
| AcceptanceSerialization | 0.633 | 0.728 | 1.779 |
| Whole-game FrameInterval | 16.825 | 17.675 | 34.964 |

Utilities still dominate the environment observation: mean 12.896 ms per observation. UtilityModules has 2,952 samples averaging 0.723 ms each (nine per observation); UtilityInputs averages 0.280 ms per facility, and the observation-local configuration query averages 1.805 ms. Current native performance does **not** meet the intended observer/scheduler budget. The mostly idle FrameCallback p95 of 0.0055 ms is not a passing active-tick result. Frame channels retained 4,096 of 9,922 observations; 5,826 were overwritten, while all 328 active ticks were retained. Channels overlap and must not be summed.

The earlier native12 run below used a different save/window, so this comparison is not a measured optimization speedup. The 100-facility/200-resident and matched baseline gates remain open. Next optimization should target measured module/input/config inspection while keeping current resource, activation, background ownership and command preflight observations authoritative.

## Native12 evidence and source13 change

The retained Duna cold-housing profile (`outputs/colony-placement-audit/native12-profile-duna-cold`) covers32.152seconds,63active ticks,64environment observations, one colony/nine registered facilities/zero residents, with all registered facilities unloaded. ActiveTick mean33.593ms/p9540.282ms; Environment mean31.021ms/p9538.124ms; Utilities mean24.847ms/p9530.470ms. This is diagnosis, not a100-facility/200-resident pass. It is not matched to the older Minmus loaded-facility sample, so the difference cannot be credited as a measured optimization speedup.

Inspection of the installed GameDatabase implementation showed that GetConfigNodes delegates to a recursive traversal of all configuration files. Frozen source13 selects BACKGROUND_CONVERTER nodes once per complete utility observation/commissioning preflight, including distribution peers, instead of once for each facility. References are observation-local; subsequent observations query again and current node values/provider state are still checked. Nested timing channels distinguish the configuration query, native catch-up, adapter validation and recipe inspection. The source clean-build and independent review are complete; the native13 measurement above establishes current cost, while a matched comparison is still required. Frozen native12 remains unchanged.

## Likely costs and safe options

`ColonyRuntime.UpdateCore` schedules a full environment build every half second, then may build it again in planning and physical procurement policies, effect preparation/readback, construction qualification, and management snapshots. Environment and acceptance samples are inclusive and overlap other channels. Compare Environment observed count with ActiveTick count to reveal repeated observation work; this ratio also includes management requests, so capture with and without the app.

| Source cost | Bounded candidate change | Required invalidation / correctness condition |
| --- | --- | --- |
| Repeated vessel searches for each registered facility | Build an exact vessel-ID index once in each environment observation | Detect duplicate IDs and preserve missing/ambiguous holds; discard after this observation. |
| Repeated crew, part and native workshop scans | Share a complete, bounded world membership/part index within one observation | Count every loaded and proto membership; incomplete enumeration must keep presence and staffing unqualified. Rebuild after every actual crew callback. |
| Repeated reflection and configuration inspection | Cache reviewed member metadata, immutable curve/config bounds, and exact assembly identities | Metadata may persist only for the reviewed type/version. Physical module refs, activation, bay selection, recipe, fuel, resource flow, core state and thermal observations must remain current. |
| PDU peers repeatedly observed for each source | Share base utility reports for exact vessel refs in one observation | Never treat a remotely promoted receiver as a new continuous producer. Revalidate whole source/receiver demand and reach budget. |
| Policy pumps build environments before determining whether anything is due | Add cheap ledger-only due/work guards before observation | Account for enabled automatic local plan shortages as well as explicit policies; guards cannot skip an event, reservation release, or physical reconciliation. |
| Catalog/config/tech lookup | Cache immutable installed catalog and configuration, with explicit lifecycle invalidation | Reset on selected world/catalog change; unlocked tech is dynamic and requires an event or fresh check before spending. |
| Full serialization on each accepted advance | Profile this channel before changing cadence or representation | Durable hold bytes/hash must exist before any physical/funds callback. Deferring unknown-effect persistence would weaken recovery and is not an acceptable optimization. |

Physical procurement already uses observation-local depot and recipe dictionaries. Extending that pattern is safer than a general time-based authority cache. A display-only cached snapshot can carry its observation time and stale status, but every command and physical preflight still needs fresh authority. Rotating partial observations must not publish complete presence, full demand, or global uniqueness until the entire bounded observation is finished.

## Next meaningful native benchmark

Use a coherent reviewed development snapshot with `-expanseColonyProfile`. Placement owns native commands. Warm up the same populated scene, then reset and capture at least 120 active ticks (60 seconds at normal tick cadence). Collect matched cases with the Manager disconnected and refreshing, ordinary loaded flight, anchored-packed facilities, and actual unloaded BRP catch-up. Keep scene, camera, warp, foreground/minimized state, save, binaries and unrelated mod configuration constant for baseline comparisons. A paused or minimized idle sample cannot stand in for an active tick result.

Report p95/p99/max for ActiveTick, Environment, AcceptanceSerialization and FrameCallback, their observed/retained counts, and Environment calls per active tick. Compare whole-game FrameInterval against a matched baseline, rather than attributing its complete cost to the colony runtime. The 4,096-frame ring retains about 68 seconds at 60 FPS; longer cases overwrite frame samples while slower tick channels cover a different interval. Archive those counts explicitly.

Profile-report construction sorts/allocates outside the diagnostic recording path, but it is called through the management callback and can perturb the containing final callback. End the measurement window before requesting the report, and identify that exceptional reporting frame. Passive recipe/PDU hooks and placement assembly hitches require separate evidence because Update measurements do not include every external callback.

The 100-facility/200-resident desktop fixture identifies algorithmic cost only. The reported 100-step advance time of 766.745 ms is neither a Mono result nor a passed 1 ms p95 / 3 ms p99 runtime target. Do not substitute fabricated physical memberships for a native scale test. First measure actual environment/serialization channels, then choose an optimization with a measurable affected channel and repeat the same case.

## Housing power setting check

Read-only installed kOS inspection shows `SafeHouse.Config.InstructionsPerUpdate` delegates to `kOSCustomParameters.InstructionsPerUpdate`. Both current isolated and production saved custom parameters explicitly contain 200; the installed constructor default is also 200. The setter permits 50–2,000.

The housing package has two kOS processors at 0.000004 EC/instruction and zero EC/byte/second. At a 0.02 s physics interval, their configured upper bound is 0.08 EC/s. Known command/wheel demand is 0.18 EC/s, giving 0.26 EC/s before actual active lab/converter/transmitter demand. The existing stock RTG supplies 0.75 EC/s; this partial known bound does not justify an extra RTG at the saved operating setting. At 2,000 instructions the same known bound is 0.98 EC/s and needs at least one extra RTG for the 10% margin, if that maximum setting is intended as a supported package condition.

The frozen native08 housing witness records PowerReliable=false but omits detailed demand/rejection text. Its earlier utility implementation also rejected now-reviewed passive modules. A fresh coherent native utility report is required before concluding whether an actual deficit remains. No craft, catalog, installed save, or certification evidence was changed by this setting check.
