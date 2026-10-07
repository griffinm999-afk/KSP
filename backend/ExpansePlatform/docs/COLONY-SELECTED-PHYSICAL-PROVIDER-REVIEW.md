# Selected physical provider boundary — 2026-10-03

This source review implements the scoped physical boundary required by `COLONY-PRODUCTION-INVENTORY-INTEGRATION.md`. It does not grant native KSP acceptance or change the frozen native12 artifacts.

## Authority and conservation

`PhysicalInventoryTransfers` observes and prepares one exact endpoint through `CreateSelectedEffectRegistrySnapshot(depotId)`. It retains the selected game and registry instances, registry world, selected snapshot hash/version, and transient mutation revision. Existing Services and LocalStocks include colony-owned endpoints only for their exact colony/facility; legacy endpoints retain their existing path. The normal legacy snapshot and mirror still use the separate legacy-only API.

Selected observations, preflights and commit boundaries recheck the retained native context. The loaded provider retains the real part/resource references, loaded/packed state, proto instance, processor, exact flight mapping and optional owned BRP mirrors. The remote provider retains the proto part/resource objects, proto instance, selected processor, exact flight mapping and current inventory/snapshot references. Initial remote resolution counts each vessel once: actual loaded parts or unloaded proto parts. A stale loaded proto cannot hide a duplicate physical PID or create a false duplicate.

The colony caller prepares its held/complete/rollback states before commit. The held reference is accepted before native writes, so a provider-triggered save observes the reservation. This uses the existing selected-save guarantee: durable on the next actual save; it is not an independent fsync or transaction across KSP and colony files.

The optional `InventoryMutationPlan.AuthorityIsCurrent` callback adds save/ledger authority to selected callers. The loaded and remote loops recheck authority and exact retained provider context before and after each callback-bearing row operation and baseline/readback. Rollback also stops between explicit ConfigNode calls if a native callback changes context. Loss after any mutation retains the partial effect as uncertain, with no colony credit, refund, later-row write, or rollback into a new owner/world. A known provider change before commit refuses before the held-state transition. Legacy callers with a null callback keep their prior row-loop behavior.

Locked destinations can receive exact positive credits through this provider. This is needed for reviewed local Pu-238 service; it does not enable resource flow, fabricate fuel, restart a generator, or qualify an Engineer/service route. Those permissions remain in the caller's exact reviewed service and paid mapping.

## Source and checks

Owned changes: `PhysicalInventoryTransfers.cs`, `InventoryGateway.cs`, `LoadedBrpInventoryGateway.cs`, `RemoteBrpInventoryGateway.cs`; selected snapshot/ownership callsites in `ColonyRuntime.Services.cs` and `ColonyRuntime.PhysicalProcurement.cs`; source-linked adapter stubs and `SelectedRegistryChecks.cs`.

`dev/Expanse.Logistics.Adapter.Tests` passes 58/58. The harness compiles the real provider/commit source with explicit KSP/BRP/registry doubles. Coverage includes selected-snapshot isolation, packed and remote conservation, revisions/world/owner hashes, duplicate loaded PID ownership, catch-up and observation provider replacement, exact local locked credit in three contexts, baseline callback loss, partial first-row mutations, processor/inventory/proto/flight/topology transitions, and rollback transitions. These are adversarial algorithm checks, not real registry migration or native persistence certificates.

Retained results, relative to workspace root:

- `outputs/colony-runtime-tests/selected-provider-validation/adapter-58.txt`
- `outputs/colony-runtime-tests/selected-provider-validation/bridge-build.txt` — Bridge Release, zero warnings/errors.
- Previous accepted generator results: `outputs/colony-runtime-tests/generator13-logistics-validation/main-434.trx`, `focused-29.trx`, `installed-api-16.txt`, `artifact-sha256.json`.

Root's observation-local utility optimization was reviewed: the current `BACKGROUND_CONVERTER` ConfigNode references are selected once within each utility environment/commissioning observation, including loaded and PDU peer paths. The witness still reads node values and provider state live; there is no persistent authority cache or change to qualification return checks.

## Remaining native proof and schema review

Acceptance still needs actual schema migration with unchanged legacy hashes/mirrors; new paid endpoint registration without active-vessel changes or overlapping members; exact intake/refill receipts from real physical stocks; loaded, anchored packed and BRP unloaded owner/readback; cold-save/load and interruption reconciliation. Placement/root retain exclusive native control. No game process, private installation, save, or fixture was changed by this slice.

The app agent owns registry serialization and registration. Independent review confirmed scoped endpoint/member totals, duplicate owner scalars, owner/spec membership hashes and corrupt-state gates. A further schema3-only strict parsing recommendation was sent to that owner: reject duplicate registry nodes and duplicate schema/world/depot/anchor/revision/member-id authority scalars rather than selecting their first ConfigNode value. Also fail closed on an unavailable unloaded proto-part list in the global registration scan. Those registry edits/tests are separate from this provider slice.
