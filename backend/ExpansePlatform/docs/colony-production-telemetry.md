# Colony production telemetry (optional clock v1 extension)

Wire location: `colony.vessels[].production`. Existing `converters`, tanks,
power, clock context, controls and commissioning behavior are unchanged.
Old frames omit the field; new readers accept its absence. An old typed Host
will discard it, so deploying later requires updated WorldBridge and Host/Core.
The Site relay forwards full frames unchanged. Nothing in this change starts,
stops, swaps, connects, commissions or saves a module.

Exact JSON field names and types:

```text
production: null | {
  status: "partial" | "unavailable" | "truncated",
  inventoryStatus: "complete-supported" | "partial",
  reason: string, observedUt: number, modules: Module[]
}
Module = {
  partId: uint32, moduleId: uint32|null, moduleIndex: integer,
  moduleType: string, partName: string,
  bayIndex: integer|null, selectedLoadout: integer|null,
  recipe: string, recipeHash: string[64], enabled: boolean|null,
  activated: boolean|null,
  basis: "loaded-broker" | "background-model" | "proto-config",
  configured: Vector,
  prepared: null | {
    sampleUt: number, efficiencyMultiplier: number,
    requirementMultiplier: number, rates: Vector
  },
  achieved: null | {
    captureSequence: positive int64,
    sampleUt: number, intervalGameSeconds: number, timeFactor: number,
    inputs: Rate[], outputs: Rate[]
  },
  background: null | {
    sampleUt: number, constraintState: string,
    inputs: Rate[], outputs: Rate[]
  },
  nativeStatus: string|null, reason: string,
  harvester: null | {
    resource: string, efficiency: number, harvestThreshold: number,
    harvesterType: integer
  },
  hopper: null | {
    hopperId: string, connected: boolean|null, body: string, biome: string,
    allocationPoints: { resource: string, points: integer }[]
  }
}
Vector = { inputs: Rate[], outputs: Rate[], requirements: Requirement[] }
Rate = {
  resource: string, unitsPerSecond: number,
  flowMode: string, dumpExcess: boolean
}
Requirement = { resource: string, amount: number }
```

`moduleIndex` is the zero-based index in the part's complete module list.
`bayIndex` and `selectedLoadout` are zero-based native indices; show Bay 1
for `bayIndex=0`. Nonzero existing module IDs are read through `PersistentId`;
the monitor never calls `GetPersistentId`, which would allocate an ID.
When moduleId is null, identify a row by vesselId + partId + moduleIndex,
within the envelope session/loadEpoch. Hashes bind recipe vectors and selection;
the loaded freshness key also includes the machinery difficulty setting.

## Rate meaning

- **configured**: loaded selected converter lists or installed selected proto
  option, before native operating multipliers. They do not prove delivery.
- **prepared**: a copy taken at the actual stock ProcessRecipe boundary, after
  USI swap/addon recipe changes. Rates include the received efficiency multiplier
  and the stock requirement fraction; inputs and output capacity may reduce them.
  The monitor never calls PrepareRecipe or GetEfficiencyMultiplier to inspect.
- **achieved**: actual returned RequestResource consumption and sign-normalized
  StoreResource credit, divided by that callback's game seconds. It is the latest
  completed callback, not a wall-time average or inferred tank delta. Prepared
  resources get explicit zero rows when a completed callback delivers nothing.
  Without a current completed callback, achieved is null, never invented zero.
- **background**: BRP Inputs/Outputs ratios multiplied by each mapped converter's
  Rate, combined per native module. This is simulation output, not measured broker
  delivery. Ratios can already contain efficiency; do not multiply it again.
- **requirements**: signed native requirement quantities/thresholds, not
  consumption rates. **allocationPoints** are WOLF ledger reservations, not
  physical units per second.

Only exact stock ModuleResourceConverter, USI_Converter, WOLF_HopperModule,
ModuleResourceHarvester and USI_Harvester
implementations with units-based configuration qualify for loaded achieved
telemetry. Other BaseConverter rows are identified but their configured/achieved
rates are withheld. Direct resource writes, engines, other non-converter owners
and external crew support are outside this coverage. Status remains partial;
sum observed rows only as a measured subtotal, not complete colony gross totals.
USI life-support core is absent and Expanse support is uncommissioned; this change
does not enable either. The requested USI 10.8 Supplies/person/21600 seconds
(0.0005/person/second) is a separate planning assumption.

For harvesters, configured.outputs is empty because output depends on native
resource abundance, situation and efficiency; harvester.resource identifies the
producer. harvester.efficiency is the native coefficient, never units/second.
Prepared outputs are copied from the callback after native abundance/USI addons.
Achieved uses the same accepted broker ledger as converters, including input and
storage constraints. All four installed Atlas small/large regular/_a variants
use USI_Harvester and USI_HarvesterSwapOption and are covered by this API.
Saved bay indices follow the native controller's complete non-standalone
ISwappableConverter order; loadouts follow its complete swap-option order.

Site consumers should preserve and validate this optional production layer and
include it in revision/render decisions. Use vesselId/partId/moduleIndex as a
stable row key (moduleId is supplementary), with sample sessionId/loadEpoch and
sequence guarding old frames. Modules carry callback sampleUt and game interval;
production.observedUt is publication observation time. Missing production is
unknown, never zero. Empty/partial/truncated rows cannot verify colony-wide
absence of producers or consumption. A known harvester with null achieved is
an identified producer whose delivered rate is unavailable.

achieved.captureSequence increments only on completed valid native callbacks,
retains its value on repeated publication and remains monotonic across a local
UT/context reset. Deduplicate using clock sessionId/loadEpoch plus this marker,
not the clock heartbeat sequence. sampleUt and intervalGameSeconds describe the
callback's game-time observation, not the wall-clock publication interval.
inventoryStatus=complete-supported requires a complete loaded BaseConverter
inventory with only exact supported implementations and no truncation or error.
Unsupported implementations remain identified/unclassified and force partial;
proto/background inventory always remains partial. An empty complete-supported
inventory verifies absence only of supported converter/harvester modules;
it never verifies no whole-vessel/colony production or demand. Overall status
remains partial because engines, direct writes and other owners are outside scope.

No callback survives a context/UT reset, recipe/difficulty key change, inactive
module or age greater than ten game seconds. Native status can be empty when
the part UI is closed; empty status does not prove there are no constraints.
Never treat OnResourceConverterOutput or TimeFactor as credited physical output.

## Bounds and verification

At most 32 modules per vessel, 16 rows per input/output/requirement/allocation
vector, and 512 cached loaded samples. Production JSON is budgeted to 24,000
UTF-8 bytes across settlement vessels. Omitted rows set status=truncated and
explicit incomplete-total reason. Publisher and Host have a second fallback
that omits production rows before dropping existing vessel observations at
the 65,536-byte frame limit.

Targeted tests: `dotnet test dev/Expanse.ProductionTelemetry.Tests`.
They exercise broker signs and partial returns, nested/unobserved attribution,
exceptions, zero versus unknown, invalid callback intervals, epochs/UT resets,
recipe/difficulty keys, multiple bays, old/new protocol, background provenance,
stale/impossible rates and payload fallback. The native serializer smoke script
uses synthetic DTOs in a separate process and verifies the installed callback
signature, plus eight detached Gypsum/Substrate saved-recipe cases across the
four installed Atlas configs; it neither calls flight methods nor patches a
running game.

No live qualification or performance measurement was performed. Before later
deployment, close KSP, install the reviewed DLLs, restart KSP and Host, then verify
loaded broker callbacks and unloaded BRP estimates with fresh game telemetry.
No relay restart is inherently required.
