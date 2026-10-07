# Colony management UI build

This work adds native WPF management controls to the existing Expanse companion app and a native Unity IMGUI maintenance window to WorldBridge. It does not modify a live installation, create a colony in a save, or replace existing delivery/production/power capabilities.

## Source and integration

- `src/Expanse.Clock.Manager/ColonyManagementView.xaml` and `.xaml.cs`: ten management pages; fixed table headers; selected-row detail; editable founding/charter and operation fields; backend capability actions; pending-operation reconciliation.
- `ColonyManagementPresentation.cs`: immutable display records, command request/response and local editable drafts. No display record owns game inventory, funds, people or buildings.
- `ColonyManagementAdapter.cs`: maps `Expanse.Domain.Colonies.ColonyState` into the ten pages and maps UI requests to `ColonyCommand`. Physical observations join by registered vessel ID. Body/biome coincidence never adopts a vessel. WOLF capacity is labeled shared biome capacity and remains separate from tanks and colony reserves.
- `src/Expanse.WorldBridge/ColonyMaintenanceWindow.cs`: six tabs (Facilities, Machinery, Staffing, Power, Inputs, Service log); fixed headers; measured expanding rows; right-aligned quantities; full-name tooltips; separate scrolling detail and service area. This adds its own toolbar window without changing third-party windows.

`MainWindow.ColonyManagement.cs` now integrates the control, refreshes the selected-save authority independently of the Host, and submits through `ColonyManagementClient`. The separate management pipe is installation scoped; explicit `--colony-pipe` and KSP `-expanseColonyPipe=` overrides support isolated development. Historical development launches with only isolated clock pipes leave this new connection disabled. `__new` is the picker identity for a founding draft. Physical readings remain **last reported** until a common observation epoch proves their branch freshness; matching only WorldId is insufficient after quickload.

Founding field keys are `Name`, `Body`, `Biome`, `Latitude`, `Longitude`, `Purpose`, `Population`, `Budget`, `CashFloor`, `SpendingLimit`, `ResidentLimit`, `VisitorLimit`, `ReserveDays`, `GrowthPolicy`, `AdoptFacilityIds`. Operation fields are explicit per section (`TemplateId`, `PlotId`, `RosterId`, `HomeFacilityId`, `JobFacilityId`, `WorkPartId`, `SupplierId`, `Resource`, `Amount`). The authority validates types, bounds, prerequisites and quote identity before changing state. Growth display values normalize to `approval`, `automatic`, or `disabled`.

In-game integration supplies `ColonyMaintenanceWindow.Capture` and `SubmitService`. The capture returns a context key, revision and per-tab rows. A service row is enabled only when the provider explicitly supplies `CanService` and a submit delegate is installed. `ColonyMaintenanceServiceRequest` carries operation ID, context key, facility ID and expected revision. The default window is read-only active-vessel inspection and explicitly does not assert membership, certified homes, connected power, accessible reserves or service eligibility.

## Drafts and operations

Drafts are keyed by context and selected colony. Initial charter values hydrate once; telemetry refresh does not overwrite edits. A form edit invalidates an existing approval quote. Active actions carry the expected revision and quote ID. An uncertain reply retains the same operation ID, disables new mutation submissions, and exposes **Reconcile pending operation**. The retry submits the exact prior request; only an explicit terminal response clears it. The in-game service window follows the same rule. The backend remains responsible for durable receipts, witness readback, epoch validation and reconciliation across app restarts.

Action capabilities come from the runtime. The view does not fabricate a working action from module presence. Unavailable capabilities carry useful reasons. Empty trade data displays its waiting reason instead of implying automatic dispatch is ready. Unknown throughput, forecasts, distribution reach and staffing/module bonuses remain unknown.

## Verification evidence so far

Manager Release build: passed with zero warnings/errors. WorldBridge Release build: passed after adding the installed Unity TextRenderingModule reference. Later concurrent physical-transfer boundary development emitted four unassigned-delegate warnings; no people/management compile error was present.

`dev/Expanse.Colony.UiSmoke` is an isolated WPF rendering/interaction harness. It opens only an offscreen window and makes no game, Host, pipe or inventory connection. It renders all ten pages at outer window sizes 860×680, 1180×930 and 960×540 with 100 long-name/large-quantity rows, plus disconnected and empty-trade blocker cases. Screenshots under `outputs/colony-ui-build/synthetic-wpf` are clearly synthetic UI fixtures. They prove native control layout, not runtime colony acceptance. Checks also cover preserved drafts, useful empty-order reasons, disabled unavailable actions and same-operation reconciliation after a nonterminal reply.

The first run exposed a founding page whose form/footer consumed its table viewport. The form now scrolls within 150 px and detail/actions within 170 px; the corrected matrix passes a minimum table viewport check. WPF dispatcher layout is pumped before rendering so star columns and headers are actually realized. Long values have full tooltips and the selected-row detail includes untruncated quantities.

Actual runtime screenshots, in-game scale/resolution checks, service effects and an end-to-end founding demonstration remain root integration/runtime acceptance work. No synthetic fixture image is claimed as runtime evidence.

## Direct management IPC and bounded checks

`ColonyManagementWire.cs` defines a versioned 8 MiB frame (saved state remains 4 MiB) with bounded package, adoption, people and capability catalogs. `ColonyManagementEndpoint.cs` uses four bounded local workers, a same-user ACL denying network logons, at most 32 queued requests, read/write deadlines and an explicit queued-versus-started timeout result. Unity callbacks run only in the runtime's `Pump()` on the owning main thread. The response is encoded there before transport workers receive bytes; mutable observer records cannot change during serialization. Main-thread serialization latency still needs measurement in KSP. Pending started requests retain operation IDs for reconciliation.

The isolated `.NET472` endpoint smoke validates callback thread ownership, accepted/same-ID operations and cancellation before dispatch. `Expanse.Colony.ManagementClientSmoke` connects the actual .NET8 Manager client to that fixture and passes snapshot, accepted request, duplicate retry, stale context rejection and exactly-one colony checks. These are cross-runtime transport checks, not Unity/KSP/save acceptance. Seven wire tests cover framing bounds, context/revision envelopes, installation hash isolation, unavailable snapshots, command smuggling rejection, accepted-result state authority and bounded people catalog roundtrips.

```powershell
dotnet build ExpansePlatform/dev/Expanse.Colony.ManagementIpcSmoke -c Release
dotnet run --project ExpansePlatform/dev/Expanse.Colony.ManagementClientSmoke -c Release -- <absolute net472 smoke executable path>
dotnet test ExpansePlatform/tests/Expanse.Clock.Tests.csproj -c Release --filter FullyQualifiedName~ColonyManagementWireTests
dotnet run --project ExpansePlatform/dev/Expanse.Colony.UiSmoke -c Release -- outputs/colony-ui-build/synthetic-wpf
```

For a verified isolated installation, use the same explicit endpoint on KSP and Manager:

```text
KSP: -expanseColonyPipe=<generated-unique-dev-pipe>
Manager: --view-pipe <isolated view pipe> --command-pipe <isolated command pipe> --colony-pipe <generated-unique-dev-pipe>
```

`dev/Expanse.Colony.RuntimeDriver` accepts only an explicit `ExpanseFoundations.Colonies.dev.` pipe. A snapshot command is read-only; mutation requires a current-context JSON command file with stable GUID operation ID and `--allow-development-mutation`. Retry the exact file after an uncertain result.

```powershell
dotnet run --project ExpansePlatform/dev/Expanse.Colony.RuntimeDriver -c Release -- --pipe <generated-unique-dev-pipe> --output outputs/colony-runtime-snapshot.json
```

## Current product layout and remaining acceptance

Overview now uses actual saved population, certified housing, policy support-day estimates, observed career funds/commitments and construction blockers as navigable metrics. People, trade and finance have specialized columns. Surveyed plots render relative geometry using real saved coordinates and published body radius. Founding adoption uses actual candidate checkboxes, retains stale selections visibly, and server submission revalidates the live source witness. Named people, actual home parts and configured passenger routes have real selectors and reviewed fare terms. Existing work seats cannot become housing by their seat count alone.

The synthetic adapter matrix covers these layouts at the three harness sizes. All ten pages remain subject to finished-product runtime content, interaction, resolution/DPI, error-state and principal drill-down acceptance. The compact maintenance window likewise needs actual six-tab scaling and provider action evidence. Unknown utility/service/forecast/throughput data remains explicit; rendered synthetic rows do not establish a working colony or certified adapter.

The service/provider extension is documented in COLONY-SERVICES-BUILD.md. Maintenance now has installed-tank selectors, exact owned-stock review, current worker/utility witnesses and saved recurring policy controls. Founding includes a reviewed finite paid staging contract; Site includes actual terrain survey/resurvey; Inventory includes costed installed WOLF dependency quotes and same-paid-package replanning. All review hashes are bound to snapshot context/revision/target and editable inputs. These workflows remain subject to actual isolated-game provider and visual acceptance.

Unity Mono IPC repair: ColonyNativePipe creates a blocking Win32 duplex byte pipe with a protected current-process-user SID ACL, NETWORK deny and PIPE_REJECT_REMOTE_CLIENTS. First-instance creation prevents joining a foreign precreated endpoint. The actual bundled Mono implementation incorrectly couples Asynchronous options to PIPE_NOWAIT/ConnectNamedPipe without OVERLAPPED; the native handle path bypasses that incomplete implementation without weakening access. Bounded worker errors are queued to main-thread diagnostics. Independent NET472 fixture checks native ACL readback, callbacks, replay and notStarted cancellation; Root confirmed actual isolated Unity management readback in outputs/colony-runtime-tests/snapshot-06.json. The independent NET8 client-to-NET472 native endpoint smoke also passed snapshot, accepted command, exact-ID retry and stale-epoch rejection. This is transport evidence, not colony/provider completion.

Product terminology: ordinary selectors use named packages, actual facility/cabin/tank labels and supplier/provider terms; resource amounts use units and costs use funds. Stable IDs/hashes remain behind expandable diagnostics. Form controls retain identity across refresh; bounded shared form scrolling preserves the table/footer at 960x540. Current utility reports show unknown readings explicitly instead of substituting zero. Startup/growth reviews call the typed authoritative pure planner and show preserved assets, buildings, BOM, finite imports, support reserve and downside cash. Approvals carry only exact quote identity and save revision; reorder fields convert ordinary units to exact counters internally. Declared survey actions obtain real runtime surveys. All ten pages passed the current synthetic layout/draft/reconciliation matrix; actual screenshots and runtime interactions remain pending.

## Worker bootstrap and resumed UI boundary

`transferColonyWorker` moves a currently witnessed ordinary visitor or resident into a real registered commissioning/operational workplace. It does not designate a visitor as a resident, consume a passenger fare, invent a home or create a job bonus by a label. Pure `QuoteWorkerTransfer` binds source/destination roster and exact cabin occupants; both current loaded landed cabins must allow stock crew transfer and lie inside the same colony, at most 200 metres apart. Loaded packed parts are supported at normal time. Protected astronauts require explicit consent, default false.

The additive saved worker operation stores the exact source/destination vessel/part, reviewed hash and both before-occupant lists. Native code accepts an applying hold before actual Part.RemoveCrewmember/AddCrewmemberAt, emits installed crew transfer notifications, and verifies exact source-minus-one, destination-plus-one and unique actual roster membership before completion. Unknown partial transfers remain held across save/load and cannot teleport or refund the crew through cancellation. Only an unstarted transfer can be cancelled. Five independent domain tests establish these stated invariants; real packed crew/trait/save/reload acceptance remains pending.

Manager task selectors now expose related fields for startup/charter, building/survey, support/recruitment/assignment/worker shift/departure, import/reorder, physical transfer/WOLF and manual/recurring service. Ordinary amounts are resource units/funds; IDs and hashes remain diagnostics. Task switching and polling retain the same edit models. Approval requires a reviewed quote of the matching action kind, and subsequent edits disable it. Physical tank presentation is a pure shared renderer so recorded snapshots use the same adapter as MainWindow. Historical actual KSP page renders are stored in `outputs/colony-ui-build/task-recorded-native-07`; commands remain disabled and these images are observation/layout evidence, not finished-product interaction acceptance. The synthetic layout/draft/reconciliation matrix passes at 860x680, 1180x930 and 960x540.

## Named founding approval and growth decisions — 2026-10-03

Founding review now carries a bounded typed `FoundingIntent`: editable target-fill preference, explicit new-arrival count, preferred ordinary existing visitors/applicants, exact paid passenger route and startup policy terms. The bill freezes actual names, current source cabins, promised home building/slot, fare/travel, bootstrap worker exclusions and every enabled or declined recurring reserve row. Missing previously selected people stay visibly unavailable rather than silently becoming replacements. The approval includes that exact reviewed intent; the older statement that every approval carries only a quote ID does not describe founding intents. Ordinary resource fields convert to exact micro-unit counters without rounding.

Growth reviews expose real exact-item approve, defer, reject and reconsider commands. Deferral stores its reason and expiry and suppresses replacement automatic reviews until expiry. Rejection permanently closes that reviewed item; a later cadence may generate an independent item. Explicit reconsideration preserves rejected history while creating a separate review. Approvals require the matching proposal target and fresh authoritative full bill, and preserve deterministic plan lineage. Newly surveyed plots can make the same still-unapproved review actionable within its current cadence.

The compact layout uses four condensed actual metric cards, bounded form scrolling, a usable table viewport and separate bottom actions. Details and expanded evidence scroll above those actions. The synthetic exact-item interaction harness verifies quote-target mismatch rejection, typed deferral fields, retained named resident inputs and an unclipped compact decision button. Latest synthetic screenshots are `artifacts/colony-ui-proposal-review/growth-proposal-review-960x540.png` and `founding-resident-draft-960x540.png`. They demonstrate native WPF control behavior, not an inhabited KSP colony.

Focused planning/resident/proposal tests passed 40/40; the root's rebuilt main test assembly passed 362/362 after actual-seat forecast corrections. Manager Release and the WPF layout/draft/reconciliation smoke passed. Actual in-game decision, arrival, service, production and complete founding/growth acceptance remain independently owned runtime proof work.

## Reviewed production operating controls — coherent13 source

Startup now exposes the typed production strategy, package/count/horizon, paid-tank registration, output intake, input refill and explicit Fertilizer/Machinery/Plutonium-238 initial reserve, reorder point/target, enable choice and cadence. These fields are included in the actual startup task whitelist; hidden parser defaults are not the interaction evidence. The review lists two paid endpoints per investment and every exact operating row with initial buffers included in the finite import bill. Register decline removes operating authority/buffers. Refill decline retains selected initial buffers and removes recurring purchases. Import-only carries no local operations.

The WPF smoke checks edited 1.234567-unit buffer conversion, retained selector identity, explicit declined fuel purchases, the two broader decline paths, and import-only null operations. All checks passed after the whitelist omission was repaired. The offscreen Manager/UiSmoke build passed with zero warnings/errors. Regenerated `artifacts/colony-ui-production-operations/founding-production-selectors-960x540.png` visibly contains editable fuel/cadence choices while keeping table and approval footer usable. All images remain marked synthetic, disconnected fixtures.

Fuel controls distinguish local NO_FLOW resource routing from the player's separate tank lock. Automatic service respects a false flowState and does not qualify that locked tank as writable; exact provider support for positive credits does not override the lock. Service does not invoke native Replenish or restart a generator.

The coherent main suite passed449/449; actual owner-scoped registration, native output/intake/refill, compact in-game screens and the inhabited one-approval founding/growth loop remain native acceptance work. Source/UI checks are not a completed-colony claim. See COLONY-PRODUCTION-INVENTORY-INTEGRATION.md for conservation, schema3 rollback and bounded fairness details.
