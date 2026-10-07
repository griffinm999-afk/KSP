# Expanse Foundations milestone 2: small world view

Architecture handoff, 26 September 2026. Build using root/Astra design and review, Sol specification/integration, and Luna majority implementation/testing. Root waits after handoff except design escalations and milestone review.

## Outcome

Register one deliberately selected depot in the current KSP save and display its observed resource amounts/capacities in the standalone Windows Manager, beside the working clock. Track the depot across vessel renames and docking/undocking through saved member part identities. Clearly distinguish current, old and unavailable data. No resource/funds/ship changes or trade commands in this milestone. Registration metadata is new saved state and is the only intended save addition.

Work inside existing ExpansePlatform. Back up source before edits. Preserve the working clock, normal launcher, existing anchoring plugin and RMM. Production KSP is running and must remain untouched during development. Use muted isolated dev KSP and separate Host endpoints when testing alongside the live production publisher; never let a dev publisher evict production. Existing current-user pipe security and bounded framing remain.

## Ownership: explicit static membership

Each depot has a random immutable depot GUID, an anchor member part persistent ID, a membership revision and an explicit finite set of member persistent IDs. Verify KSP Part.persistentId in local assemblies, reject zero/duplicate/ambiguous IDs. Vessel IDs and display names are observations only; they never grant membership. No automatic enrollment of newly docked or constructed parts.

MVP membership can be the deliberately selected resource-bearing parts rather than all structural parts. Include every resource on each selected member, including electricity and mod resources. A registration preview must show human part labels, current amount/capacity, part count and total selected resources. Persist the selected part IDs, initial descriptive label and anchor; later display the current vessel name so renames are visible.

The bounded user workflow is a small in-game Expanse Foundations window accessed from a clear stock toolbar button in Flight:

1. At a loaded, unpacked active vessel, open Depots.
2. Select which resource-bearing parts belong to the depot, inspect totals, choose Register selected tanks.
3. The window shows registration status and the Manager displays its inventory.

Initial checkboxes start empty. Do not silently include currently docked visiting ships. A plain resource-part checklist with human-readable part titles and capacities is the reliable minimum. Grouping by verified docking components is optional only if it reduces ambiguity without delaying this milestone. An explicit select-all control must say it includes docked ships; it must not be the default. Duplicate part labels need distinguishing resource totals and a small stable identifier; brief part highlighting is optional. Choose the anchor from selected members, visibly identify it in the confirmation. Registration requires at least one resource member and cannot select an EVA or invalid vessel. Static one-depot-per-save limit should have a clear message. Provide an explicit unregister action with a small confirmation; never replace an existing registration by accident. Membership editing/re-enrollment can follow later; users can unregister and deliberately register a changed depot now.

Do not classify depot membership using resource sizes, vessel name, vessel type, a docked craft's root, or RMM port records. A new tanker can merge into the same vessel; only saved members count. If a registered member separates from the anchor vessel, flag an incomplete depot and do not publish partial quantities as complete. Missing/destroyed/duplicated anchor or member identity is an explicit unavailable reason.

## Save state

Use a new versioned ScenarioModule for Expanse depot registration, added to new and existing games and appropriate Flight/SpaceCenter/TrackingStation scenes after verifying declarations. Store a generated world GUID inside this scenario node alongside depot metadata. It survives normal save/load and copying a save copies its world identity; it is not a full branch/recovery system. Use existing process session and observed-load epoch to fence live observations. World GUID changes for a fresh save, not every scene.

Save module holds at most one registration with bounded member count (e.g. 4096) and bounded strings. Registration does not force-save or manually rewrite persistent.sfs. Normal game saves persist the metadata. Quickload restores membership from that selected save, including absence of a depot if registration had not yet been saved. Never restore registry from an external Host cache. OnLoad replaces in-memory metadata atomically and clears inventory cache. Unknown schema or corrupt registration data is preserved raw and reported as unavailable; do not replace it with a new empty registration on save. No module instance from the previous save may be used while the new registry is not ready.

## Observation scope and trust

Initial authoritative source is the loaded, unpacked active vessel containing the saved anchor and every member exactly once. If another vessel is active, the depot is unloaded, the game is in Space Center, or the vessel is packed, report why fresh stock cannot be read. Do not scrape proto resources or claim BackgroundResourceProcessing integration here. Later adapters can supply trustworthy unloaded data through their own rules.

Resolve saved IDs to actual parts only on relevant vessel/topology transitions or when needed. Docking may change vessel identity; resolve against saved parts again. Membership must not expand. A structure-change event, vessel reference/count change, or lost member invalidates in-progress aggregation. Verify topology change signals locally; conservatively revalidate at an observation boundary when uncertain. Do not scan every vessel every frame.

Aggregate all PartResource internal names with display labels, amount and maxAmount from selected members. Reject non-finite/negative quantities and contradictory resource identities; explain invalid snapshots instead of silently clamping them. Resource units are KSP units; no mass conversions or invented available-to-transfer amounts. Stored means physical contents including locked tanks, with that meaning stated; flow settings do not imply cargo availability yet. Do not include tanks on a visitor that is not in the member set.

Observe roughly every two REAL seconds, independent of UT. Cached snapshots can ride the existing 0.5-second clock messages without resetting their own observed age. Prefer cached part mapping and bounded incremental reads; cap work per Update (e.g. 64 members or 1 ms budget), publish only a completed coherent aggregation, discard work if relevant topology/context changed. Capture start/end observation UT when readings span frames; they are an observation window, not an atomic point-in-time accounting guarantee. A counter records each completed snapshot revision, scoped to session/load epoch plus depot identity and membership revision. Do not claim transaction-grade snapshots.

A zero amount is valid only in a fresh completed snapshot that actually read that resource. Missing stock is never converted to zero. No tanks selected means no registration, not an empty depot. Changes to save/session/depot/membership invalidate old observations immediately. Within the same context, retain the last successful quantities for reference when unavailable, explicitly marked last observed.

## Wire and shared Host rules

Sol fixes the precise typed schema before parallel implementation. Extend the existing v1 clock sample/view with OPTIONAL additive depot fields so an old clock-only publisher remains a working clock. If no depot capability field exists, show 'Update bridge for depot view', not 'No depot registered'. Do not break the normal production clock during development. Prefer separate dev/test pipe endpoint option; keep production v1 endpoints interoperable. Old Manager may ignore additive fields. Host derives status/freshness once; Manager and future in-game views consume the same semantics rather than rebuilding rules.

Depot data includes capability/schema version, registry readiness/status/reason, saved world GUID, depot GUID, membership revision, current display name, anchor/member summary, snapshot revision, observation window UT, observation age at publication, resource rows and a clear observation state. Include only required data; no full part tree per clock tick. Host age adds elapsed real time since reception to transmitted observation age, and must not refresh the stock age merely because a repeated clock heartbeat arrived. Mark otherwise-live stock stale after a modest threshold (e.g. 6 seconds) or lost publisher; paused stock still refreshes. No-world and world/epoch changes clear cached depot data immediately even if a later message omits depot fields. Retired samples cannot restore stale stock from another game.

Stay below the 64 KiB wire bound. Bound aggregate resource rows (e.g. 128), text lengths, and member IDs transmitted (a count suffices). Oversize/incomplete depot data produces a compact unavailable reason while the clock keeps working; do not disconnect the entire bridge because a depot observation is too large. Parsing/serialization remain on worker/external threads. Clone immutable completed snapshots before publishing to worker. Preserve existing view-client backpressure limits and worker-owned teardown cleanup.

## Windows and in-game UI

Keep a readable light UI. Add one depot card/table to the clock app: current depot name, clear Live/Last observed/Unavailable state, resource name, amount, capacity and optionally a simple fill bar. Show an understandable reason such as 'Visit this depot to refresh stock', 'Vessel is packed during warp', 'A registered tank is no longer connected', or 'No depot registered—open Expanse Foundations in Flight'. Freshness is specific to inventory; a live clock does not imply live stock. Do not bury useful data in GUIDs; keep identifiers in a compact diagnostic area. Unknown values display an em dash, never a fabricated 0.

In-game registration window must have a visible toolbar entry with a readable tooltip; avoid requiring the user to discover an undocumented key or button color. Bound and scroll part lists. Input locks are acquired only while pointer is inside a visible window and always released on close/scene change/destruction, or omit locks if not needed. Past RMM input-lock problems must not recur. Changes to registration should be visible immediately in the next valid sample.

## Verification and delivery

Sol writes exact independent Luna packages: (A) Core/Host/Manager/schema tests, (B) KSP registry/registration UI/observer, with remaining fixture work reassigned to whichever Luna finishes first. Sol handles integration, interface decisions, evidence and straightforward review; root reviews ownership/freshness/save boundaries. Most code/tests remain Luna work. Ordinary build decisions are Sol's; escalate ambiguous architecture instead of spending many worker cycles guessing.

Required meaningful tests:

- Persist/restore registration, missing-new-node behavior, old quicksave clearing, corrupt/unknown schema preserved.
- Renamed vessel resolves same depot; adding a visitor's resource parts cannot increase depot amount/capacity; missing member/anchor gives unavailable.
- Correct aggregate of several tanks and mixed/mod resources, zero vs unknown, invalid fields, bounded/oversized observations.
- Stock freshness independent of heartbeat; same-UT new epoch and different world discard old data; old bridge clock compatibility.
- Existing clock protocol/Host tests remain passing; build against local KSP references.
- One bounded muted disposable dev-game run with registered known test tanks, known quantities, observation sent through real Host, rename and save/reload. Prove visitor exclusion using actual docking if a simple reliable fixture exists; otherwise exercise production aggregation against an explicitly described expanded member candidate set and label actual docking untested. No invented flight-test claims.
- Verify normal in-game toolbar registration window and Manager UI launch as feasible without desktop control. A direct fixture call into the same registration method tests behavior, not mouse interaction; disclose this difference.

Do not repeatedly run KSP for optional coverage once material gates pass. Record exact source/artifact hashes for real runs and later narrow fixes. Preserve logs and refresh package excluding test harness/game DLLs. Disable our dev harness after test. Keep normal Clock/Host app unaffected while production game is running by building/testing outputs and using isolated endpoint names; if Windows app restart is needed near delivery, it is allowed for our helpers, but never stop KSP.

Deliver design, protocol, source, runnable package, human registration instructions, tests and model work/evidence ledger. Production installation is already authorized as the user's preferred target, but do not replace loaded plugin files: complete build/tests/package first, then request the user close regular KSP only if still running when installation is ready. Copy/backup only our plugin after closure and verify hash. No change to other mods or manual edits of the Expanse save.
