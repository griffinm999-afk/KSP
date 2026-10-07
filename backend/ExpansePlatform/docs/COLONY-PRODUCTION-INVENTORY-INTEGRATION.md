# Reviewed production inventory integration

Source boundary: 2026-10-03, prepared for coherent13. This contract is implemented in source and independently tested. Native12 remains immutable. Paid endpoint registration, actual production intake/refill and inhabited founding continuation still require native acceptance; source compilation does not migrate a save or authorize a native command.

## Authority and capacity

Nullable `ColonyProductionIntent.Operations` carries the explicitly reviewed registration, output intake, input refill and resource operating terms. Null preserves the earlier approval hash and supplies no new operating authority. The product presents editable enabled defaults, exact ordinary-unit quantities and every enabled or declined recurring purchase before approval. Import-only carries no operations. Declining registration removes all operating buffers and automation; declining refill preserves explicitly reviewed initial buffers but disables recurring purchases.

The registry retains eight legacy endpoints and adds a separate bounded scope of 128 paid-colony endpoints, with 4,096 total registered members and at most 64 members per colony endpoint. Existing legacy IDs, schema2 membership hash bytes, mirrors, normal pipe and manual registration contracts remain unchanged. Pending reviewed plans reserve colony endpoint/member capacity; a later conflicting registration produces an honest hold rather than deleting an existing endpoint. Quote authority binds the selected registry world, revision, witness and actual free capacity. Registry and colony save identities are distinct.

Each investment creates two exact endpoint specifications:

| Endpoint | Craft anchor | Reviewed members |
|---|---:|---|
| Cultivation | 100 | 100 Machinery service, 101 Water, 104 local Plutonium-238 fuel, 106 Supplies output, 107 Substrate, 108 Fertilizer |
| Raw feed | 106 | 106 Substrate output, 119 Water output |

Craft IDs resolve through exact paid placement markers to actual persistent members on the reviewed vessel/site. Gypsum103, Recyclables109, batteries and arbitrary existing tanks are excluded. Machinery100 and fuel104 are installed service destinations, not warehouse reserves. Template, recipe, marker, ownership and endpoint specification hashes bind the reviewed mapping.

Schema3 migration treats absent old owner metadata as legacy and preserves legacy membership encoding. Strict schema3 parsing rejects duplicate authority scalars, ambiguous roots, oversized colony endpoints and cross-scope membership overlap. Corrupt/unsupported source nodes are retained and quarantined rather than repaired by guessing. An older build quarantines schema3: rollback requires the matching pre-migration save backup. No save has been migrated by these source checks.

## Durable registration and selected providers

`DepotRegistry.Colonies.cs` adds an explicit-vessel, deterministic registration path. It does not change ActiveVessel or automatically adopt existing tanks. Before registration, the colony state accepts an applying effect and exact endpoint/member intent. Native preflight rechecks the current selected game/epoch/scenario/registry instance, registry world/revision, actual paid markers, complete registered facility mapping, site, resource-bearing members, loaded/qualified Foundation-anchored eligibility, global persistent-ID uniqueness, capacity and overlap.

An exact existing deterministic endpoint reconciles by readback. Missing or mismatched applying state remains held; it never blindly repeats. Applied registrations are not recreated after manual deletion. Only exact owner/spec/member readback completes a receipt. Referenced registration effects remain retained through effect compaction.

The legacy snapshot continues to expose only legacy entries. A selected-endpoint snapshot exposes one exact legacy or colony-owned endpoint, at most 64 members, through the existing conserved physical transfer providers. Colony LocalStocks and service targets require exact matching colony/facility ownership. Provider preflight and every mutation/readback/rollback row fence selected membership hash/version, registry instance/revision/world and actual loaded or BRP provider identities. Context loss after partial native writes holds without credit, refund or rollback into a different world.

## Stock conservation and reviewed operating terms

Initial additional owned buffers join the existing plan material claims, finite suppliers, shared freight and cash reservation exactly once. They release only after durable actual commissioning/readback, before the plan waits for first output. Reviewed recurring policies then activate before production starts. Declined or conflicting separately reviewed policies are not silently replaced. Full Supplies import downside remains explicit and funded.

Editable offers are Fertilizer initial100 / reorder100 to200, Machinery initial100 / reorder100 to100 and Plutonium-238 initial1 / reorder1 to2 units per package, with a one-Kerbin-day purchase cadence. These are user-selected offers, not measured consumption or inferred requirements. The new variant separately bills its own 20-unit native startup fuel; the additional owned reserve does not change that manifest. Existing original 2-unit packages retain their original identity and billed contents.

Output intake debits exact warehouse Supplies, Water or Substrate and credits owned stock only after the conserved physical receipt. Native recipe capacity, allocated WOLF points and output observations alone create no stock. Input delivery debits owned stock into exact native Water/Substrate/Fertilizer destinations, bounded to 50% capacity and 1,000 units per physical batch. Imported startup contents retain their paid origin in metadata; conservative native-movement provenance prevents them becoming fabricated local production. Provenance stamping changes no physical or owned amount.

Approved paid-production endpoints use modeled internal delivery to and from this owned account. The current applied endpoint must still match its reviewed colony, paid operational building, exact members and registry witness, and remain inside the existing charter and approved plot. This access does not depend on distance to the abstract charter centre within that boundary. It is not a native USI consumer-to-warehouse transfer: adopted or legacy warehouse access keeps the installed 150m scavenging rule, and native USI behavior is unchanged. Provider authority, player flow/warehouse locks, native input buffers and conserved before/after receipts remain required. This source correction still requires native intake/refill qualification.

Machinery uses the existing installed-maintenance target and current actual Engineer/service reach. Local Ranger fuel uses that same conserved service path with the exact paid 20-unit Plutonium-238 tank and qualified worker. It is a modeled stock delivery, not a native Replenish event or warehouse operation, and it never restarts a stopped generator. `NO_FLOW` is the resource definition's native routing mode. A separate false `flowState` is a player lock: the automatic fuel-target adapter respects that lock and does not expose a writable target, even when the provider can technically perform positive exact credits. Locked local fuel therefore remains an outage until explicitly unlocked.

## Bounded scheduling and evidence retention

One operating item is reserved per pump. Physical intake/refill and Machinery/fuel service share a derived round-robin order across eligible facilities/plans. The last `productionTankScheduled` event supplies the cursor; deterministic ordering resumes after it. This prevents continuously ready early tanks or a five-second cooldown from starving later facilities and maintenance. The cursor survives save/load while its event remains in the bounded 2,048-event journal. After unrelated journal compaction it restarts deterministically; it is not a resource receipt or replay authority.

A full pending physical ledger, service ledger or effect budget defers automatic work instead of causing a global hold. Terminal physical receipts carrying PlanId are preserved because material substitution validation depends on them. Referenced registration receipts likewise cannot be evicted. Old null operating scopes never acquire automation through these schedulers.

## Validation and remaining acceptance

Root's coherent main suite passed 449/449 with no skips; Bridge Release passed with zero warnings/errors. The new inventory suite includes exact central bill/capacity/stale authority, null-scope old-hash preservation, declined terms, durable registration reconciliation, buffer release, selected intake/readback, owned fuel escrow, terminal receipt retention, full pending capacity, imported-origin provenance and fairness. The fairness case uses 12 continuously producing tanks across four packages plus due Machinery service over 26 half-second pumps; it is a domain/provider fixture, not native production evidence. Logistics' selected-provider source-linked adversarial suite passed 58/58.

The actual WPF harness passed layout, retained edits, exact six-decimal quantity conversion and explicit decline semantics. It caught and repaired a task field whitelist that initially hid the new operating choices. Synthetic screenshots are under `artifacts/colony-ui-production-operations`; they establish product controls, not game behavior.

Native acceptance still needs actual schema3 migration and unchanged legacy hashes, newly paid endpoints and membership readback, warehouse output intake, finite input/Machinery/fuel refill, player-lock and user-shutdown preservation, loaded/packed/unloaded and cold-load continuation, and the complete named-resident founding/growth sequence. A 100-facility registry/provider performance check remains necessary. Normal state4MiB, management8MiB, selected provider bounds and legacy64KiB pipe limits remain enforced. Final compact rotated streets must use safe reviewed deployment/access clearance; sparse diagnostic terrain spacing is not final appearance acceptance.
