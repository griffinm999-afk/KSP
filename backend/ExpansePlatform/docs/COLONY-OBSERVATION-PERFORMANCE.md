# Colony observation payload and diagnostics candidate - 2026-10-06

Status: revised candidate STAGED after isolated validation; runtime package UNINSTALLED.
This candidate includes the restored installed crew roster/physical seat observations,
the staged vessel census, production telemetry (including USI harvester provenance),
and the settlement journal. It supersedes the earlier telemetry-only candidate.
No game restart, installation, save/settings edit, or live profiling was performed.

## Thread boundary

The existing main-thread clock sampling interval and three-second colony capture
interval remain unchanged. The existing publisher thread and single latest clock
sample slot are reused. No extra queue, task per capture, or worker thread was added.

Main-thread capture copies vessel/part/module identity and primitive values from
Unity/KSP into a private `ColonyCapture` graph. Lists, arrays and DTOs are exclusively
owned after publication and are treated as immutable: the producer never revisits
them, and derivation creates its own mutable list containers. Leaf DTOs shared with
the derived result are only read by serializers. Broker-owned prepared production
vectors and cached WOLF resource-name arrays are copied before handoff. The capture
contains no Vessel, Part, PartModule, ConfigNode, reflection object, or delegate.

Moved to the publisher worker:

- Tank validation, grouping by full resource identity and accessibility flags,
  sums, stable sorting and the 24-state output cap.
- Converter input/output name deduplication and 16-name output caps.
- Longitude normalization, crew roster byte/count budgets, and production JSON
  byte-budget reduction.
- Census identity formatting, sorting, agreement, stability and membership checks.
- Observed EC totals-to-rates arithmetic and nominal active-converter EC sums,
  finite/positive checks, contributing-module limits and overflow checks.
- Once-per-interval timing formatting and log writes. Final JSON serialization
  and pipe writes already ran on this worker and remain there.

Remaining on the game thread:

- All Unity/KSP reads, scene/world checks, part/module walks, reflection and
  saved configuration access, biome lookup and loaded/proto crew validation.
- Native production callback accounting, recipe identity/freshness checks,
  configured/prepared/achieved provenance capture and required defensive copies.
- Power callback accumulation and window/eligibility checks.
- WOLF registry enumeration, its existing bounded selection/ordering, and
  materialization of primitive observations. These were not broadened into this
  refactor.

Live vessel ordering uses stable two-pass Minmus priority selection instead of
sorting live objects. Existing observation budgets and wire limits are retained.
Additional raw-capture limits are 4096 tank rows per vessel, 128 names per converter
vector, 512 active power modules with 128 entries per recipe vector, and 513 copied
census IDs (the sentinel over the existing 512-ID maximum). Excess tank/name data
marks the observation truncated; an oversized power vector withholds its estimate.

## Replacement and reset behavior

The single pending clock sample is atomically replaced; a blocked or disconnected
consumer cannot accumulate an unbounded backlog. The worker also retains one
derived capture/result cache. Heartbeats reuse it without repeating census attempts
or production truncation. Main-thread capture attempts have explicit sequence IDs;
a skipped capture breaks the two-consecutive-observation census proof.

Session, load epoch, world/save, scene, activity and capture-availability transitions
advance a generation fence, even when intermediate samples are replaced before
consumption. A stale generation is checked before derivation, serialization and
transport. Returning from an unavailable capture requires a fresh main-thread
capture. The worker invalidates prior census stability on transitions. Each frame
retains its own epoch; a transport already in progress is not rewritten or cancelled
by a later transition.

## Timings

The worker writes `Observation performance (~60s)` to the existing bridge diagnostic
log. Fields contain count, total milliseconds and maximum milliseconds for
`mainSample`, `mainCapture`, `workerDerivation`, `workerJson`, `workerTransport`, and
`powerCallbackSampled1in64`. Additional totals count power callbacks, inspected
parts/vessels and replaced clock samples.

The power hook counts every callback and times one in 64, including early-return
paths. Its sampled total is not the full callback cost and its maximum is only the
sampled maximum. The hot path uses fixed counters/Stopwatch with no new formatting,
allocation or I/O. Interval counters drain independently, so a concurrent record
can straddle two reporting windows. Existing lifetime sample counters are retained.
Reporting is approximately once per minute; a blocked pipe write can delay it until
the existing worker resumes. Part/vessel counts cover completed capture scans.

## Validation and limits

- Five Release builds: WorldBridge, Host, Manager, BrpColony and TrackingStation;
  zero build warnings/errors.
- Clock regression suite: 611 passed; production suite: 32 passed, including the
  combined roster/census/production 64 KiB fallback regression.
- Recovered roster wire, physical seats, census lifecycle and frame-limit checks passed.
- Pure worker checks passed on another thread: primitive-only capture graph,
  non-mutating derivation, full resource identity, normalization, power arithmetic
  and limits, skipped attempts, scene invalidation and timing count/total/max.
- Detached tests against the actual candidate DLL passed: 10,000-publication bounded
  slot, concurrent publication/drain, heartbeat cache, skipped captures, overwritten
  scene/unavailable transitions, load-epoch rejection/reset and broker-vector copy.
- Native serializer/truncation/callback checks and eight detached configured
  recipe/bay cases passed. Tracking Station offline QA passed.

Tests use isolated Host pipe names/data directories or detached primitive fixtures.
The handoff test uses reflection solely as offline test scaffolding to call the
candidate's private methods; production worker derivation uses no reflection.

No live speedup or frame-time improvement is claimed. A later explicitly authorized
installation/run is needed to collect comparable timing intervals and assess whether
remaining main-thread capture, native callbacks, or unrelated mods dominate cost.

The runtime archive is an overlay for the existing installation, containing matching
KSP plugin DLLs and complete framework-dependent Host/Manager build outputs. It does
not replace game settings, saves, templates or user databases. A full source snapshot,
test evidence, source change manifest and SHA-256 manifest accompany the runtimes.
Do not combine individual DLLs from the older telemetry-only package with this set.

## Payload and diagnostic correction

The observed Host view was 49,245 bytes with all production rows omitted. It carried
44,609 bytes of colony data, including 6,104 bytes of WOLF data and 2,491 bytes of
62-ID census data. Derivation previously spent a fixed 24,000-byte production
budget in vessel order; publisher and Host fallbacks could then drop all rows.

Derivation now retains the bounded capture. Publisher and Host independently fit
whole module rows against their actual remaining 65,536-byte frame space. Delivered
and current background observations take priority, followed by prepared potential,
activated configuration and other rows. Equal-priority vessels share round-robin
allocation; the census capture sequence rotates the starting vessel even when UT
does not advance. No rate, resource vector or distinct bay is merged or rewritten.
An omitted inventory stays explicitly truncated/partial. Crowded frames can still
omit individual rows; rotation provides coverage rather than asserting complete
totals. Existing roster, census, clock and physical-observation fallbacks remain.

Offline replay with the additive omission metadata retained at least 12 whole rows per frame, covered all 18 observed
facilities over 18 captures and kept the census/roster flags. Maximum body size was
65,253 bytes. Replay production values were synthetic, not observed game rates.

Worker file diagnostics now resolve to Logs/ExpanseFoundations/ClockBridge/worker.log
under the KSP installation. Once-per-minute timing summaries, including the resolved
path, also pass through a fixed slot to main-thread KSP logging. File failures use
another fixed slot, rate limited to once per minute. Detached tests verify the real
writer/reporter against workspace paths and a deliberately invalid directory.
No existing live game, Host/Manager installation, relay, ledger or credentials were
modified. Live rate qualification and timing collection require the next authorized
closed-game installation. The prior logger's actual failure/path remains unobserved.

The coordinated additive omission contract is specified in
PRODUCTION-BUDGET-CONTRACT.md. The earlier archive without those fields is superseded
and not install-ready. The Site's independent 24,000-byte production gate must be
replaced by full-frame bounds before coordinated packaging. Historical module
retention does not confer current-rate eligibility. Rotation cannot guarantee fresh
simultaneous facility coverage, especially under warp.
