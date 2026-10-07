# Modeled Ore export and Kerbin recovery

October 1, 2026. Source implementation under ExpansePlatform/AGENTS.md; no production deployment or game fixture performed.

## Accepted behavior

An enabled source-stock order exports one configured batch whenever fresh authoritative registered unloaded source tanks can supply it. New routes carry 1 to 1,000,000 whole physical Ore units at the fixed price of **100 funds/unit**, default quantity 1,000 and default compensation 100,000 funds. Dispatch debits those tanks and accepts immutable in-transit cargo. After three six-hour Kerbin days (64,800 game seconds), the app models the trip, landing and recovery and credits the immutable cargo compensation to the authoritative Career account. No vessel is spawned, flown or recovered; no ordinary vessel recovery proceeds or bonus are added. Travel duration is configurable per immutable route version, default 3:00:00.

The latest configurable quantity/100-unit-price request supersedes the earlier fixed 1,000 Ore/500-unit-price setup. Already departed historical 500/unit cargo retains its 500,000-funds payment; new 500/unit configuration and dispatch are blocked, including durable unapplied commands prepared before the upgrade. Save/receipt reconstruction still accepts historical terms. No canonical wire format change or automatic save rewrite accompanies this pricing revision.

The Manager chooses the source through its stable registered depot identity and membership hash. The audited Minmus Mining registration is fa516623-27a6-4093-97f4-55a1b05bc495; the implementation does not identify depots by label or auto-enable that registration.

The read-only save audit at 15:31:31 EDT found 24 enrolled source members but none of the vessel's 16 Ore tanks enrolled. A depot label therefore does not establish export readiness. Explicitly register the intended Ore tanks before enabling the source order; the implementation must hold rather than debit unenrolled tanks. Evidence is in outputs/colony-residency-audit-20261001.json.

## Source and transport boundary

RouteVersionRecord has DestinationKind physicalDepot (legacy default) or virtualKerbinRecovery. The buyer ID is kerbin-recovery with no fake membership or physical depot. A virtual route carries one validated whole-Ore quantity and its fixed price. ActiveShipmentRecord copies those economics; editing a route creates a new version and cannot reprice departed cargo. exportStock rule batch quantity must exactly match its immutable route.

exportStock rules trigger on source stock, using TargetMicroUnits as optional source reserve. The requested UI configures zero reserve. LogisticsPlanner.PlanExportStock checks the exact batch threshold. Actual dispatch remains subject to fresh gateway preflight and exact physical readback, source membership, provider capability, bounds and writer fencing. WOLF abstract commodities and stale display projections never authorize a physical Ore debit. BRP 0.2.7 selected unloaded tank inventories remain the supported production physical delivery path. All virtual dispatch paths hold when economicRecovery.v1 is absent.

## Financial transaction and recovery

The Host durably prepares one recoverySale operation before offering it. EconomicRecoveryIntent contains ShipmentId and checked FundsDelta computed from immutable whole Ore quantity times shipment FundsPerUnit. The reducer independently proves that intent and success/fault witness match the shipment compensation. Historical receipt/result validation permits its own strictly positive exact economic delta, never replacing it with the current default price. The game thread captures funds before the mutation; Host telemetry is not money authority. FundsSuccessWitness persists BeforeFunds, IntendedDeltaFunds, IntendedAfterFunds and ObservedAfterFunds. Both exact addition and exact after-minus-before are required so double rounding cannot alter compensation.

The Bridge builds success and terminal uncertain fault capsules before mutating funds. Because Funding.SetFunds invokes other mods synchronously, it commits the conservative economic fault capsule before SetFunds(after, VesselRecovery). A callback save therefore retains blocked uncertain evidence, preventing automatic duplicate payout. Exact readback and the same Game/Funding/Recovery/world/run/epoch/capsule context are required before the prebuilt success capsule replaces the fault. A callback load never gets overwritten. Uncertain outcomes retain the shipment and globally block logistics; balances alone are not proof to replay or resolve a payment.

Host receipt handling matches EconomicEffectResult against the accepted witness or EconomicFaultRecord and reconstructs the complete deterministic resulting state. Lost receipts reconcile from the selected save capsule after Host restart. Stable operation identity, sequence, payload hash, compaction watermark and preserved faults prevent retry/reload payout duplication. The Host database mirrors selected-save authority. This source-level and synthetic verification does not establish in-game persistence behavior; that remains a separate isolated fixture gate.

Recovery request identity includes world, active run, shipment ID and shipment revision. A quickload that restores an earlier dispatched shipment and its earlier funds account can settle that cargo in the new run even when newer Host history records its later payment. That historical row cannot strand restored payable cargo. Repeated offers within one run retain the same identity; a selected economic fault remains blocked. Host tests exercise this restoration after both normal receipt delivery and lost-receipt reconciliation.

## Implemented files and ownership

- Domain/Luna: src/Expanse.Domain/AcceptedState.cs, AcceptedStateV2Draft.cs, EffectMessages.cs, RecoveryV2MessagesDraft.cs, LogisticsPlanner.cs; new OreExport.cs and OreExportCodec.cs.
- Host/Sol: src/Expanse.Clock.Host/RecoveryCoordinator.cs.
- Bridge/root: src/Expanse.WorldBridge/EffectBridge.cs; new EconomicRecoveryEffects.cs.
- Manager/Sol: src/Expanse.Clock.Manager/MainWindow.xaml and MainWindow.xaml.cs.
- Synthetic verification: tests/OreExportTests.cs and tests/RecoveryTests.cs; root adds source-linked Bridge callback verification separately.
- UI verification: dev/Expanse.Manager.UiSmoke/Program.cs uses only uniquely named dev pipes, suppresses polling, and renders mock data offscreen.

## Canonical compatibility and verification

The configurable revision verified actual pre-price EXS4 bytes independently of a newly encoded fixture: outputs/ore-payment-live-state.json revision 12 roundtrips as the same 7,536 decoded bytes and SHA256 15428b36416897370ed07fc50a3a89cf511deebca29d5949d4086070a760182b. Its historical 500/unit route remains readable unchanged. The final integrated suite passed 131/131, including configurable quantities, historical settlement, stale endpoint pause, unapplied historical command holding, exact payout validation, and retry/reload tests. The isolated Manager smoke passed editable quantity, fractional rejection, payout preview, order enabled/inactive status, and paused existing order migration to a newer route version. Its 860x680 screenshots are in artifacts/ore-configurable-ui-smoke-20261001. Root separately checked the actual shipping Bridge cancel parser and payload hash without game objects or production pipes.

EXS4 adds bounded economic fields and witnesses; EXS1/2/3 retain their prior bytes and hashes until an accepted export transition needs the extension. Capsule size, receipt tail and collection limits remain enforced. A receipt is compacted only under existing exact-prefix rules. Unresolved faults cannot disappear through compaction.

The final coordinated synthetic suite passed 117/117, including six source-linked Bridge callback tests. Bridge and Manager Release builds passed with zero warnings and errors. Tests cover short source stock, exact batch debit, 64,800-second due UT, missing funds capability, malformed financial result, success/fault receipt verification, lost receipt and Host restart, canonical legacy roundtrip, duplicate and compacted replay rejection, exact funds representability and terminal fault retention. An earlier full run had one existing clock-publisher timing failure; its focused retry and the final coordinated run passed. Avoid concurrent testhost runs against shared build outputs.

UI smoke passed at 860x680. Screenshots: artifacts/ore-export-ui-smoke-20261001/ore-export-setup-860x680.png and ore-export-activity-860x680.png. These are mocked display evidence, not live mission or payment evidence.

Stage a source snapshot/package with hashes because this workspace lacks Git history. Review staging before any production installation. An isolated game fixture must verify Funding callbacks, save/reload consistency and physical BRP debit; no live game/deployment results are claimed here.

## Session usage record

Recorded per-response model usage was measured from local session records, deduplicated by response ID. See outputs/ore-export-token-usage.md and .json for the root turn and descendants; cached input is included within input. The report is an in-progress task snapshot, not a billing statement.


