# Depot view wire and work-package contract

This extends `CLOCK-MVP-INTERFACE.md` under the architecture in `DEPOT-VIEW-DESIGN.md`. Sol owns this boundary; Luna workers own the implementation packages below. All new fields are optional additions to the existing protocol-version-1 messages and pipe names. A missing field means the older component lacks depot support; it never means an empty registry.

## Wire types

Add nullable `depot` to `clockSample`. An M2 bridge emits it on every sample, including main menu and unresolved loads. An M1 bridge omits it. The nested keys are camel case:

```
depot: {
  schemaVersion: 1,
  registryState: "noWorld" | "loading" | "none" | "registered" | "unavailable",
  reason: string | null,
  worldId: GUID | null,
  depotId: GUID | null,
  membershipRevision: positive integer | null,
  label: string | null,
  anchorPartId: positive UInt32 | null,
  memberCount: integer 1..4096 | null,
  currentVesselName: string | null,
  observationState: "none" | "collecting" | "complete" | "unavailable",
  observationReason: string | null,
  snapshot: StockSnapshot | null
}

StockSnapshot: {
  sessionId: GUID,
  loadEpoch: GUID,
  worldId: GUID,
  depotId: GUID,
  membershipRevision: positive integer,
  revision: positive Int64,
  startedUt: finite number,
  completedUt: finite number,
  ageSeconds: finite nonnegative number,
  resources: ResourceRow[1..128]
}

ResourceRow: {
  name: nonempty internal resource name,
  displayName: string | null,
  amount: finite nonnegative KSP units,
  maxAmount: finite nonnegative KSP units
}
```

The registry states have strict combinations. `noWorld` has no world/depot identity or stock. `loading` means the save's scenario has not finished loading and also has no depot identity or stock. `none` has a world GUID but no depot. `registered` has world/depot GUIDs, membership revision, label, anchor and member count. `unavailable` means a corrupt or unsupported saved registry; preserve that raw scenario node and send no depot identity or stock. A registered depot may retain a completed snapshot while observation is `collecting` or `unavailable`, but only from the *same* session, load epoch, world, depot and membership revision. `complete` requires a snapshot. An unavailable or absent snapshot is never represented as zero. `reason` and `observationReason` are bounded explanatory text, not commands.

The Host must validate unique resource internal names and no amount above max capacity, with a small floating-point tolerance for KSP arithmetic. Reject non-finite, negative, duplicate or contradictory rows. Limit each text field to 1024 UTF-8 bytes, resource names/display names to 128 bytes, resource rows to 128, membership count to 4096, and the entire frame to 65,536 bytes. The bridge uses a compact `registered`/`unavailable` observation with `snapshot=null` and an overflow reason if a snapshot cannot fit; the clock sample itself still sends. A corrupt registry is represented by registry state `unavailable`, never silently replaced with `none`.

Add nullable `depotView` to `clockView`; an M1 Host omits it. The M2 Host always emits it. Its fields are:

```
depotView: {
  status: "waitingForKsp" | "updateBridge" | "noWorld" | "loading" |
          "noDepot" | "unavailable" | "live" | "lastObserved",
  reason: string | null,
  worldId: GUID | null,
  depotId: GUID | null,
  label: string | null,
  currentVesselName: string | null,
  memberCount: integer | null,
  snapshotRevision: positive Int64 | null,
  stockAgeSeconds: finite nonnegative number | null,
  resources: ResourceRow[]
}
```

The Host derives this view from validated data. If there is no clock sample, status is `waitingForKsp`; if the clock sample lacks `depot`, `updateBridge`. A fresh no-world sample yields `noWorld`. `none` yields `noDepot`. `loading` and registry `unavailable` remain distinct. For a registered depot, a completed matching snapshot is `live` only while the publisher is connected, the outer clock is live/paused, the bridge observation state is `complete`, and effective stock age is under 6 seconds. Effective age is at least the sample's transmitted `snapshot.ageSeconds` plus monotonic elapsed time since Host receipt. The Host also remembers the first receive time and age for each `(session, load epoch, world, depot, membership revision, snapshot revision)` key, so repeated heartbeats with the same revision cannot reset age even if a sender repeats the same age value. A retained same-context snapshot is `lastObserved` when unavailable, collecting, disconnected or older than 6 seconds. With no valid snapshot, status is `unavailable` and `resources=[]`. A new session, observed-load epoch, world, depot or membership revision, and a no-world sample must discard the prior stock context; a retired session cannot restore it. The Manager consumes `depotView` and does not independently infer stock freshness. If `depotView` itself is omitted by an old Host, the Manager says to update the Host.

## Source ownership

Package A, Luna Host: `src/Expanse.Clock.Core/`, `src/Expanse.Clock.Host/`, `src/Expanse.Clock.Manager/`, and `tests/` only. Implement typed wire additions, validation, Host freshness/context logic, Manager card/table, and synthetic tests including old bridge/Host compatibility and oversize frames. Preserve existing clock status, view-client bounds and default pipe names. Support an explicit alternate Manager view-pipe argument for the isolated dev run. Do not edit KSP plugin or ScenarioModule. Send Sol any required bridge encoder changes as interface notes.

Package B, Luna KSP: `src/Expanse.WorldBridge/` and `dev/` only. Implement the new versioned ScenarioModule, explicit unchecked Flight resource-part registration UI, immutable bounded stock observations and bridge encoding to this schema. Use the existing main-thread sampler and worker pipe. Support an explicit validated publisher-pipe argument from the dev KSP command line, leaving the normal account-name endpoint unchanged by default. No resource writes or RMM dependency. Design fixture integration inside `dev/`, and do not launch/deploy until Sol coordinates isolated endpoints and the one bounded dev-game run. Do not edit Core/Host/Manager/tests.

Sol owns docs, build/deployment integration, package, acceptance evidence and any changes crossing the fixed boundary. Each worker reports code, actual tests, unresolved issues and exact files touched. The M1 source backup is `artifacts/source-backups/ExpansePlatform-M1-source-20260926-021527.zip` (SHA256 `D0FDE9F5D18F1B2D6427890293A3BBEC9DDDC0B2BB6096E0A7F24938ED96F5A2`).
