# Colony operator guide — draft

**Status:** Source-grounded operator guide. It describes the current workflow; live end-to-end acceptance remains in progress.

Founding has two separate approvals: first register the colony charter and site, then review and approve the startup bill. The colony runs approved purchases, arrivals, construction work, and routine policies in KSP, including while the Manager is closed. You still need to load the site in KSP for physical building placement and anchoring.

The Manager plans colony work in the selected save. The in-game maintenance window shows facilities, people, power, resources, and service for the currently available game context.

## Normal Manager workflow

1. **Load the intended save.** Open **Colony management** in the Manager and wait until it says **Connected to this KSP save**. If it is disconnected, follow the message shown there before using colony actions.

2. **Choose a colony or start a new one.** Use the colony picker at the top of the Manager. For a new colony, choose **Establish a new colony…**.

3. **Register the charter and site.** In **Founding & charter**, choose **Step 1: Register charter**. Choose an **Observed facility site** (or select a listed facility and choose **Use this facility’s site**), then fill in **Colony name**, **Purpose / charter**, **Founding residents**, and **Founding budget (funds)**. **Optional: assets to adopt**, **Advanced site coordinates**, and **Advanced charter policy — defaults** start collapsed. Review the charter and register it; this records the colony and site but does not approve startup purchases. To add a facility to an existing colony, choose **Adopt an existing facility** and review that adoption separately.

4. **Review and approve startup separately.** Choose **Step 2: Review and approve startup**. The normal fields are **Resident selection**, **New paid arrivals**, **Passenger service**, and **Supplies production strategy**. **Advanced startup settings — defaults** starts collapsed; leave it closed unless you need to change those options. Choose the residents and import or production approach, then select **Review step 2: startup bill**. Check the quoted people, buildings, purchases, policies, total cost, and budget limits. Adjust and review again if needed, then approve the displayed bill. Only items in that approved quote are authorized.

   In a full-startup quote that includes them, approval schedules the two named paid arrivals, assigns the listed bootstrap workers, queues construction of the 11 listed buildings, and applies the approved reserve and refill policies. These tasks still take time and wait for their requirements; arrivals are not instant and a queued building is not yet placed or operating.

5. **Let the colony handle routine work and visit the site for placement.** Approved construction and routine policies advance in KSP when funds, supplies, workers, providers, and other requirements are available. If work is held, read the reason shown with the item. For physical placement, load the colony site in KSP and let the game run at normal 1× speed so buildings can be placed and anchored. A plan, completed construction timer, or map marker alone does not mean a building is operational.

If the connection drops after a scene change or quickload, wait for it to reconnect before acting. If the Manager says an operation is pending, use **Reconcile pending operation**; do not submit the same work again as a new request.

## Startup and production choices

The startup task puts residents, support, maintenance, procurement, and production in one reviewed bill. Its normal controls are **Resident selection**, **New paid arrivals**, **Passenger service**, and **Supplies production strategy**. Review shows the selected names, route, fares, homes, jobs, and recurring policies before the separate startup approval. Other policy settings remain under **Advanced startup settings — defaults**.

For Supplies, choose **Import Supplies — no new production investment**, **Compare cultivation with imports**, or **Invest in native cultivation and raw-feed hoppers**. Compare is a review choice; it does not commit production. A local investment asks for the package, count, and comparison horizon, then exposes the operation choices:

- **Manage the new production tanks** registers the exact endpoints for paid packages. Declining leaves their physical stock unmanaged and declines the operating buffers below.
- **Collect native output into colony stock** records verified physical output transfers. Declining keeps output in physical tanks.
- **Refill native production inputs** enables bounded refills for the exact paid tanks. Declining also declines the recurring input purchases. Physical tank capacity or a nominal recipe rate does not create stock or guarantee output.
- Per-package rows expose extra initial Fertilizer, Machinery, and Plutonium-238 buffers, reorder points and targets, and separate recurring-purchase choices. Zero declines the extra buffer; disabling a resource's recurring purchase declines only that recurring purchase. The paid initial tank contents are itemized separately from extra colony-owned buffers.
- The current editable offer starts at 100 extra Fertilizer with a 100-to-200 reorder band, 100 extra Machinery with a 100-to-100 band, and 1 extra Plutonium-238 with a 1-to-2 band, reviewed every one Kerbin day. These are draft values, not free inventory or an authorization to buy; confirm every amount, enabled/declined row, cadence, import bill, cash floor, supplier, and freight limit in the quote.

## Routine people, supplies, and support

Approved startup worker assignments and paid arrivals are handled by the plan. The **People & jobs** page shows who is present, where they live and work, and whether their home and work places are available. Separate recruitment and worker-shift tasks are available for later changes. A person needs a real available cabin and work seat; a job label alone does not create one.

The startup plan reserves Supplies for resident support and commissions it when the required startup work is ready. Ordinary support consumption continues as game time advances, including while the site is unloaded. Keep the approved Supplies reserve funded; a shortage pauses immigration and growth, and a later delivery does not create a charge for the past shortage. Arrivals and worker moves remain pending until their real route and cabin changes are confirmed.

## Keep stock, WOLF, and power separate

**Inventory & WOLF** shows three distinct things:

- Colony-owned stock, including reservations, incoming reservations, support floors, and paid-import provenance.
- Physical part tanks and their observed amounts, flow/warehouse state, provider, and observation basis. A physical tank is not automatically accessible colony stock; a transfer requires an eligible endpoint, provider, current access, and conserved before/after readback.
- WOLF resource points, which are shared biome capacity allocations. WOLF points are not physical tank contents or exclusively owned colony stock. A WOLF quote lists its paid module dependencies and balance policy; approving capacity does not credit resource inventory.

**Power** reports the evidence it actually has: observed ElectricCharge tanks, generation/consumption windows, continuous-source rating, demand bounds, temperature margins, and qualification reasons. Nominal estimates and summed batteries do not establish a connected grid, nighttime margin, autonomy, or distribution reach. Unknown values stay unknown; **held** means the evidence or prerequisite is insufficient, not zero power.

## Shipments, service, growth, and holds

The approved startup plan applies its included reserve-purchase rules automatically. **Trade** shows supplier orders and cargo in transit. Extra imports or changes to reserve rules are reviewed against the available funds, supplier stock, receiving capacity, and freight. Cargo becomes available only when it arrives.

The approved startup plan also applies its included refill and tank-service rules. Routine refills continue only when the listed stock, a suitable worker, and a working provider are available; otherwise the Manager holds the task and shows why. Tank service does not restart a generator.

**Overview** and its expansion task show actual housing pressure, open jobs, support, available funds, commitments, and the selected proposal. Review the exact expansion bill before approval. A proposal can be deferred with a reason and duration, rejected, reconsidered as a separate review, or approved after its selected target is re-quoted. Automatic growth remains constrained by the charter and current evidence.

When an action is unavailable or work pauses, read the reason shown on the page. Resolve the stated requirement and refresh the view before continuing. For an uncertain result, use **Reconcile pending operation** rather than repeating the action.

## Troubleshooting and technical notes

The Manager may require a fresh review if the site, selected facility, people, budget, or plan changes. If an operation is still pending, reconcile that same operation before submitting more work. These safeguards prevent outdated quotes or a delayed reply from causing duplicate spending. Technical references and detailed acceptance limits are in the implementation documents linked below.

## Current interface acceptance checklist

The Manager has ten source-defined areas. The offscreen WPF harness exercises layouts, edited drafts, disabled reasons, and operation reconciliation at 860×680, 1180×930, and 960×540. A separate disconnected adoption/charter fixture exercises the named existing-colony task, exact reviewed target/context/revision, missing or changed candidates, retained drafts and selection, guarded command execution, unknown replies and duplicate replay. It also checks read-only registered identity/site, editable reviewed charter terms and separate usable table/action regions in all ten areas at those three logical sizes. These are fixture checks using synthetic values, without a game, Host, or colony pipe. Logical layout fixtures do not establish actual OS DPI scaling. Each area still needs real runtime content and interactions at supported resolutions/DPI, including error and drill-down states.

| Manager area | Live acceptance still needs |
|---|---|
| **Overview** | Actual save/colony metrics, commitments, blockers, and growth proposals; correct drill-down to the selected area. |
| **Founding & charter** | Actual site/adoption candidates, editable charter, named residents and production terms, full quote, one reviewed approval, and honest holds. |
| **Site & construction** | Real terrain survey/resurvey, package/plot review, funded queue, normal-speed placement, anchors, and truthful commissioning state. |
| **People & jobs** | Actual roster/cabin readback for visitors, residents, arrivals, assignments, and worker shifts. |
| **Production** | Actual installed recipe, qualified worker/input/power/fuel evidence, paid endpoint registration, conserved intake/refill, and output receipt. |
| **Power** | Connected demand and supply observations, thermal/fuel evidence, scene/load status, and held reasons. |
| **Inventory & WOLF** | Real owned-stock ledger, physical provider reads and conserved transfers, plus separately witnessed WOLF allocations. |
| **Trade** | Actual supplier, cash, freight, dispatch, arrival, settlement, and reorder behavior including interruptions. |
| **Maintenance** | Real qualified worker/provider, exact tank effect, held and retry cases, and recurring-service behavior. |
| **Finance & policy** | Actual funds/ledger/commitments, cash floor, quote invalidation, and approval/automatic/paused growth decisions. |

The in-game **Expanse colony maintenance** window has six full tab names. At narrow widths, the toolbar uses compact aliases shown in parentheses.

| Tab | Live acceptance still needs |
|---|---|
| **Facilities** (*Sites*) | Save-authoritative site/facility membership and state; active-vessel fallback is inspection only. |
| **Machinery** (*Machine*) | Actual installed tank/part readings, selected provider, and an exact service effect or honest held reason. |
| **Staffing** (*Crew*) | Actual physical occupant, trait, and workplace evidence; the fallback does not infer resident or job status. |
| **Power** | Actual connected EC, generation/consumption window, and thermal/qualification reason. |
| **Inputs** | Actual physical amounts/flow, accessible owned reserve, and verified transfer or refill evidence. |
| **Service log** (*History*) | Persisted operation history, before/after witnesses, replay/reconciliation, and an honest empty state. |

The service button is enabled only when a context and `SubmitService` adapter exist and either a request is pending or the selected row explicitly allows service. Otherwise the visible reason includes **Choose a facility**, **A qualified service adapter is unavailable**, the row's `ServiceReason`, or the snapshot's reason. Without the runtime `Capture` adapter, the window says **Read-only inspection** and reports that membership and service eligibility are not inferred. Source and fixture coverage do not close the actual in-game scaling, provider, service-effect, or colony-membership acceptance gaps.

## Implementation and evidence references

- [Manager UI and source handoff](COLONY-UI-BUILD.md) — ten areas, draft/review behavior, disabled-action reasons, synthetic layout limits, and remaining runtime acceptance.
- [Coherent13 app-source handoff](COLONY-APP-SOURCE-HANDOFF-13.md) — named founding intent, startup operating controls and declines, fairness fixture limits, and source-only status.
- [Native end-to-end acceptance sequence](COLONY-NATIVE-ACCEPTANCE-SEQUENCE.md) — actual save, provider, placement, paid startup, support, production, growth, and cold-load evidence gates. It is a procedure, not a record that acceptance passed.
- [Manager view and actions](../src/Expanse.Clock.Manager/ColonyManagementView.xaml), [workflow definitions](../src/Expanse.Clock.Manager/ColonyManagementView.Workflows.cs), and [view behavior](../src/Expanse.Clock.Manager/ColonyManagementView.xaml.cs).
- [Colony data adapter](../src/Expanse.Clock.Manager/ColonyManagementAdapter.cs), [reviewed command handling](../src/Expanse.Clock.Manager/MainWindow.ColonyManagement.cs), and [production quote presentation](../src/Expanse.Clock.Manager/MainWindow.ColonyProduction.cs).
- [Production intent mapping](../src/Expanse.Clock.Manager/ColonyManagementView.Production.cs) and [founding resident selections](../src/Expanse.Clock.Manager/ColonyManagementView.FoundingResidents.cs).
- [Native maintenance window](../src/Expanse.WorldBridge/ColonyMaintenanceWindow.cs) — six tabs, active-vessel fallback, service-enable rules, and visible reasons.
