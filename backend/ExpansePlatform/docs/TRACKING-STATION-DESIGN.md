# Expanse Tracking Station — first implementation design

Design owner: Astra. Implementation/integration owner: Sol. Critical integration review: root.

2026-10-01. Implements the approved organization/navigation core of `outputs/expanse-colony-futures/dist/tracking-station.html`. The concept images establish direction, not an obligation to recreate an orbital renderer. This document records inspected local APIs and architecture; it does not establish runtime qualification.

## Package boundary

Create an additive plugin in `src/Expanse.TrackingStation`, assembly `Expanse.TrackingStation.dll`, namespace `Expanse.TrackingStation`. Target net472/C# 7.3, matching the installed KSP and existing WorldBridge project. Build this project directly. It must not depend on or modify WorldBridge, Clock Host, Clock Manager, Domain economic behavior, or the pending Ore package. No Harmony, Kopernicus, MechJeb, kOS, or USI dependency is needed.

Reference local `Assembly-CSharp.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.IMGUIModule.dll`, and `UnityEngine.UI.dll` (plus an additional Unity module only when required by compilation), all `Private=false`, through an overridable `KspManagedDir`. Use the existing net472 reference-assemblies package. Deliver only this assembly and its own documentation in `GameData/ExpanseTrackingStation/Plugins`; do not redistribute KSP/Unity assemblies. Installation remains root's responsibility after package review.

An addon with `[KSPAddon(KSPAddon.Startup.TrackingStation, false)]` owns scene lifetime. Add an application-launcher button using `ApplicationLauncher.AppScenes.TRACKSTATION`, with a readable hover label. Use a generated small icon if necessary. Keep the stock tracking component and its map alive. Closing the custom panel immediately exposes the stock interface. Avoid disabling or reparenting stock UI in this increment: that creates compatibility and restoration risks for other tracking mods.

## Components and ownership

Separate a pure, Unity-free browser model/query layer from the thin KSP snapshot adapter and IMGUI view. Suggested boundaries (names may follow implementation style):

| Component | Responsibility |
| --- | --- |
| BodySummary / VesselSummary | Immutable snapshots with stable keys; no Unity objects |
| BrowserModel / Query | Body hierarchy, membership, counts, filtering, deterministic sorting, visible rows |
| BrowserPreferences | Per-save state, schema version, named views, bounds validation |
| KspSnapshotAdapter | Main-thread enumeration; nullable data; identity resolution for actions |
| TrackingStationAddon | Scene lifecycle, refresh scheduling, toolbar, actions, input lock cleanup |
| TrackingWindow | Resizable/scalable IMGUI, virtual rows, explicit selected-vessel details |

The UI selects a vessel GUID in its own state. Selection alone never flies, loads a craft, saves the game, or changes the stock tracking selection. Re-resolve GUID against the current `FlightGlobals.Vessels` for every action; do not retain a Unity vessel reference across refreshes or frames as authoritative identity.

## Local API evidence

Read-only metadata and selected method IL were inspected with the installation's `Mono.Cecil.dll` against `C:\Kerbal Space Program\KSP_x64_Data\Managed\Assembly-CSharp.dll` on 2026-10-01. These are actual public members in that assembly:

| Purpose | Verified API |
| --- | --- |
| Bodies | `FlightGlobals.Bodies : List<CelestialBody>` |
| Parent topology | `CelestialBody.referenceBody`, `orbitingBodies`, `isStar` |
| Body identity and label | `bodyName`, `flightGlobalsIndex`, `GetDisplayName()`, `displayName` |
| Vessel summaries | `FlightGlobals.Vessels`; `Vessel.id`, `vesselName`, `vesselType`, `situation`, `mainBody`, `protoVessel`, `mapObject` |
| Crew | `Vessel.GetCrewCount()`; `ProtoVessel.GetVesselCrew()` |
| Map focus | `MapView.MapCamera`, `PlanetariumCamera.SetTarget(MapObject)` and `SetTarget(CelestialBody)` |
| Stock selection/action bridge | `KSP.UI.Screens.SpaceTracking.Instance`, public `SetVessel(Vessel, bool keepFocus)`, public `SelectedVessel`, public `FlyButton` |
| Scope of scene | `HighLogic.LoadedScene`, `HighLogic.CurrentGame`, `HighLogic.SaveFolder` |
| Preferences | `KSPUtil.ApplicationRootPath` plus own plugin preferences file |
| Input isolation | `InputLockManager.SetControlLock`, `RemoveControlLock`, `IsLocked` |

`SpaceTracking.FlyVessel(Vessel)` is **private**. Its IL checks mission/discovery conditions, saves through normal stock persistence, then calls `FlightDriver.StartAndFocusVessel`. Invoke the stock Fly button only on explicit user Fly. Do not replicate a partial version of those guards or call raw FlightDriver methods.

`SpaceTracking.GoToAndFocusVessel(Vessel)` is a scene-transition helper: it checks clear-to-save, saves `persistent`, then loads TRACKSTATION. It is **not** the appropriate focus action inside this panel.

`SetVessel` has a once-per-`Time.frameCount` guard and looks up a private dictionary of tracked map objects. Calling it does not guarantee the selection changed. It can also refocus the camera when selection changes, even though its boolean parameter is named `keepFocus`. Never use it on ordinary row selection; after explicit Fly preparation recheck the actual `SelectedVessel` identity before invoking the button.

Available types include Debris, SpaceObject, Unknown, Probe, Relay, Rover, Lander, Ship, Plane, Station, Base, EVA, Flag, DeployedScienceController, DeployedSciencePart, DroppedPart, DeployedGroundPart. Do not silently drop less-common enum values. Recorded situations include DOCKED in addition to the proposal's usual seven; display it when present. Enum extension/unknown values get a readable fallback.

## Body tree and scope

Build from the complete loaded body set, never stock names or fixed indexes. Use the canonical `bodyName` as the persistence key (not the localized display label); retain index only as a runtime discriminator/fallback. Vessel identity is the GUID. Duplicate body keys require a deterministic disambiguator rather than dictionary overwrite.

Follow `referenceBody`, ignoring self-parent edges. Null/missing parents become roots. Detect cycles and cut a deterministic edge, so malformed planet-pack topology cannot hang recursion. `isStar` identifies stars, including stars orbiting other stars; do not assume only the root is a star. Preserve barycenters and unusual non-star roots as actual topology rather than inventing a stock solar-system shape.

The user-visible hierarchy is All vessels → star system → planet system → exact body → vessels. A system heading is a synthetic subtree scope. Its body child is an exact-reference-body scope. Thus Kerbin system includes Kerbin, Mun and Minmus; Kerbin body includes only vessels currently associated with Kerbin. Every star/system can also have vessels directly orbiting its own body. Further moon/submoon nesting must remain supported. A single-body planet can omit a redundant system heading if the scope semantics remain clear.

Associate a vessel once, using its current `mainBody`; a null/unavailable body belongs in an explicit Unassigned body branch. An escaping vessel stays at its current reference body; never duplicate it under a destination. Counts are deduplicated by GUID and propagate to ancestors, while a direct-body leaf count stays exact. Selecting a scope label changes scope; its chevron only changes expansion. Expand/collapse all operates under the current scope.

## Search, filters, counts and sorting

Search is case-insensitive and tokenized by whitespace. Every token must match the vessel's searchable fields (tokens may match different fields). Initially these are vessel name and body labels/canonical names; explicit tags remain an extension. Do not infer mission, site, or colony tags from naming conventions. Scope remains a separate AND predicate and is always visible.

Use an ordinary expansion set plus a temporary search reveal overlay. A nonempty search reveals ancestors of matching vessels without mutating the ordinary set. Clearing search removes the overlay and restores the exact prior expansion state; query refreshes must not overwrite that state. If supporting user toggles during search, keep them in transient search state. Body-name searches reveal vessels on the matching body as well as matching vessel names; empty bodies can remain visible only when Show empty branches is enabled.

Types: selected alternatives OR together. Situations: selected alternatives OR together. Type, situation, crew, scope, and search groups combine with AND. Empty selection within a dimension means Any; label that clearly. Debris and flags need explicit, visible controls, with no hidden contradictory exclusion if their types are selected. Crew is Any / Crewed / Uncrewed; unknown crew is distinct from zero and matches neither specific filter. Zero crew says nothing about controllability.

Display matching / total counts on filtered branches and selected scope. Total means all unique vessels in that branch before search/type/situation/crew filtering; scope defines the branch, not another denominator filter. Debris/flag hiding affects the matching count. Hide zero-matching branches by default, with a Show empty branches preference. Keep selected scope recoverable even if it becomes empty. Distinguish Loading, data unavailable, zero vessels in scope, and no matching vessels.

Offer name, body, type, situation and crew sorting, reversible from clear header controls. Natural names compare digit runs without integer parsing overflow, so Hoppers 2 precedes Hoppers 10 and long digit strings are safe. Tie-break by case-insensitive full name then GUID for stable order. Unknown crew sorts consistently; reversing never randomizes ties. Group None produces a flat list; grouped mode sorts within each group. Fixed headers remain outside the scroll viewport.

## Rendering and responsiveness

Use an IMGUI window initially; prefer direct `GUI` rectangles for virtualized rows rather than one GUILayout call per vessel. Cache the flattened visible tree/table rows when query or data changes. Given row height h and scroll offset y, render only floor(y/h) through ceil((y+viewportHeight)/h), with small overscan. Both the tree and table must avoid rendering the complete fleet when hidden by scrolling. A cached snapshot refresh once per 1–2 seconds while visible is acceptable for first release; do not enumerate vessels during every Layout/Repaint event. Coalesce event-triggered dirty updates. Reuse data if a refresh temporarily fails.

No per-vessel `Load`, part instantiation, resource scan, or physics activation is allowed for listing. Use summary fields and crew counts. Destroyed/missing selections clear safely; a selected vessel filtered out of the view is explicitly marked or cleared rather than silently acting on a different row. Rebuild topology only when scene/body identity changes.

Window movement and resize must clamp to actual screen bounds. Persist logical dimensions, UI scale and useful column widths; scale 1–2 is the target. At narrow widths or enlarged scale, hide secondary columns and use a details area/drawer, retaining full name/body/situation/crew plus Focus/Fly. No permanently unreachable close button. Use explicit text labels and contrast, not color alone. A tooltip or details label exposes untruncated names. Clear search/filter controls and the stock-view action remain available on empty results.

Acquire only this addon’s input lock while pointer/focus is inside its UI; release on hide, scene destruction, focus loss and failure. Do not call `ClearControlLocks()`. Stock tracking locks can disable the very native Fly button being invoked; release only this addon's lock before verifying native action availability, and respect remaining locks. Avoid hiding another mod's lock or forcing its controls enabled.

## Actions and stock fallback

Focus: confirm Tracking Station scene, resolve GUID, verify a valid map object and camera, then `SetTarget(v.mapObject)`. If unavailable, show an actionable status and leave the view intact. Body scopes may focus via `SetTarget(body)`. Focus follows selection, if implemented, defaults off and is visibly configurable.

Fly: process only a discrete button click. Resolve current GUID; confirm scene/stock instance/map object and native restrictions. Release this addon’s input lock, call `SetVessel(v, true)` or the verified equivalent, then require `SelectedVessel != null && SelectedVessel.id == requestedId`, `FlyButton != null`, and `FlyButton.IsInteractable()` before `FlyButton.onClick.Invoke()`. If same-frame throttling prevents selection, either defer once to the next frame with the GUID carried through and revalidated, or report that selection is still updating. Do not retry a completed scene switch. Do not set `SelectedVessel` directly; that bypasses stock button/selection reconciliation.

Stock view: close the custom panel and release its input lock. Keep application-launcher access so the custom panel can be reopened. Remove all event handlers, launcher button and owned textures on scene destruction. Failures in this addon must leave the native Tracking Station usable.

The current public `MapViewFiltering` API is a global **vessel-type** filter (`VesselTypeFilter`, `SetFilter`, `CheckAgainstFilter`) and cannot express search/body/crew/situation predicates. Its state also interacts with stock list construction. Therefore this first implementation must explicitly label filtering as list-only, with the stock map unchanged. Do not expose a working-looking Apply filters to map toggle. Full reversible marker/orbit filtering is a deferred adapter requiring independent runtime qualification; public `OrbitRendererBase.drawMode/drawIcons` existence alone does not establish safe persistence or restoration across stock LateUpdate. This is a disclosed departure from the proposal default, not a claim of full map-filter parity.

## Per-save workspace and named views

Store only UI metadata, isolated from game persistence. Use a plugin-owned configuration below its PluginData directory, keyed by a stable hash of canonical installation/save-folder identity, or a similarly safe isolated path. Never modify `persistent.sfs`/quicksaves to save preferences and never derive a write path directly from a view name. A schema-versioned ConfigNode or simple supported serializer is sufficient. Load only after `HighLogic.SaveFolder` is known. Do not carry preferences between two save identities.

Persist ordinary expansion, selected scope, sort, grouping, filters, panel geometry, scale and named views. Named views capture scope/filter/group/sort/columns; display explicit Save/Delete actions and handle duplicate names predictably. Provide sensible default presets (All vessels, Colony facilities by type, Stations); do not fabricate Active mission tags. Favorites and tag editing can remain later increments.

Validate enum values, numeric bounds and collection sizes on load. Missing bodies or views fall back to All vessels. Drop stale selection GUIDs; never resolve by matching a vessel name. Write only on dirty state with debounce or close, using atomic temp/replace where practical. Corrupt/unsupported preference files must be preserved and reported; continue with defaults without immediately overwriting the corrupt original. Preferences failure must not disable the browser.

## Verification and release claims

Pure model tests should cover multi-star and deep-moon topology, self-parent/cycle/missing-body safety, GUID deduplication, exact-body versus subtree scopes, AND/OR filtering, unknown crew, natural numeric sorting, counts, search reveal/restore, stale scope/selection recovery and preferences serialization. Use synthetic snapshots without starting KSP or touching saves. A representative 10,000-vessel query/flatten workload can establish offline scaling; it does not prove IMGUI frame performance.

Compile the standalone plugin against actual installed references and inspect the artifact contents/dependencies. Do not run the platform-wide build/deploy path for this package. Runtime acceptance later must include toolbar open/close, stock fallback, click-through/input locks, resize/scale, search restore, Focus/Fly availability, and a modded body tree. These require explicit authorized in-game execution; no game launch or fixture is part of this design task. Until then describe the result as build-verified, runtime unqualified.

Later extensions: biome/site grouping, named settlements, explicit tags, favorites, resource timestamps, keyboard navigation and optional rename/type editing where not yet implemented. No inferred production-health badges. Any omitted proposal feature must be listed in delivery notes, especially list-only map filtering and the absence of measured in-game performance.
