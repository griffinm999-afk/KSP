# Expanse Tracking Station 0.1.3

Standalone KSP 1.12.5 Tracking Station interface replacement. It opens automatically when the native station is ready, with a full-height vessel sidebar, branded header/footer, selection card and live map on the right. No toolbar click is required. It is separate from WorldBridge, Clock Host and Manager.

## Installation and fallback

Close KSP. Extract the reviewed archive and run install-tracking-station.ps1 with PackageRoot, GameRoot and the reviewed ExpectedPluginSha256. WhatIf previews the target. The installer verifies hash/assembly identity, requires a closed game, records version metadata and backs up its own existing DLL as Backups/<timestamp>/Expanse.TrackingStation.dll.bak. The non-DLL extension prevents a duplicate assembly loading. Other components, saves and PluginData preferences are preserved; the installer does not launch KSP.

Choose **Stock interface** to restore the native browser/buttons/map filters and camera viewports. The Expanse toolbar button reopens the replacement. Native objects stay active; only presentation is suppressed. Disable, cleanup or a caught UI failure also restores the native presentation. The map uses KSP's actual bodies, orbits and positions. Filters apply to the custom list only, explicitly labeled **Filters: list only**.

## Browser and actions

Body groups come from the loaded hierarchy, including extra stars, planets and moons. Fresh preferences expand roots and stars; existing expansion preferences remain intact. Click a system name for descendant scope; **Body** chooses the exact reference body. Unknown references appear unassigned. Branch counts describe the subtree; bottom matching/total counts describe the selected scope.

Search is case insensitive and requires every word to match the vessel name or body ancestry. Search reveals matching branches without modifying remembered expansion; clearing restores it. Type and State dropdowns allow multiple choices, OR within each dimension. Scope, search, type, situation and crew combine with AND. Crewed means known crew greater than zero; uncrewed means known zero; unknown crew stays separate. Situation is KSP's recorded state, never a health or production claim.

All vessels, Facilities and Stations are quick views. Facilities means Base/Rover + Landed/Splashed, without inferred colony tags. Debris is a Debris-only type shortcut, not global map visibility. Group switches system hierarchy/flat results and can show empty branches. Sort supports natural name order, type, body, situation and crew; choose the current field again to reverse. Expand/Collapse applies to scope and is disabled during search.

Select a vessel for its full name and recorded data. Selection never flies. Focus changes the map target. Optional Focus follows selection starts disabled and never flies. Fly, Recover, Track and End / terminate invoke native buttons only after current identity, other input locks and native eligibility checks. Ineligible actions report unavailable. Recovery/deletion retain KSP confirmation dialogs. Space center invokes native exit. Finish native dialogs before custom actions.

## Readability and preferences

The shell follows GameSettings.UI_SCALE and offers its own 100/125/150/200% multiplier. UI_SCALE_APPS is not multiplied again. At 3840×2160 / UI scale 2 / plugin 100%, logical layout is 1920×1080, base text 36 physical pixels, sidebar 34.5%. Small screens cap effective scale and use compact controls. A private GUI skin explicitly sets fonts/padding and measures button captions; shared skin/matrix and nested clip scopes restore after drawing. Long row names have tooltips/full scrolling details. Only visible tree rows are drawn.

Views saves named scope/search/filter/sort/expansion/grouping settings; the same name replaces a view. Preferences belong to the save-folder identity under this plugin's PluginData, outside KSP saves. Damaged/unsupported files are preserved and writes disabled until moved aside manually. Escape closes a menu, then clears search or selection.

## Runtime review and deferred work

Resources, colony tags, favorites, editable vessel names/types, adjustable columns, arrow-key tree navigation, complete synchronized map predicates and production/attention badges are deferred. No mission tags or health values are invented. Native timewarp/other-mod controls are available through Stock interface where needed.

Actual 0.1.2 in-game appearance, camera/marker alignment, modal layering, native eligibility/confirmations, planet packs, scene/save transitions and IMGUI fleet performance remain unverified. Review automatic startup at the actual display settings, search/clear, combined filters/scopes/counts, full names/sorting, Stock interface/reopen and modal action blocking. Explicit Focus/Fly runtime testing requires an eligible vessel and user authorization. No build-time game launch, live fixture or save mutation occurred. See TRACKING-STATION-WORK-EVIDENCE.md.




Update 0.1.2: reduced default sidebar to 28%, base text to 14 logical pixels, tree rows to 32, header/footer to 78/52 (including the permanent vessel-type strip), and selection card to 600 by 166. Added 75% and 85% scale choices; corrected a focus-button branch that drew the empty-selection hint over the button. Build/layout checks verify geometry; revised in-game appearance remains pending.

Click the Vessels heading to collapse or reopen search/filter controls. Filters remain applied while hidden, and the collapse preference is remembered per save folder.

Vessel type toggles remain across the top when the Vessels controls are collapsed. Select multiple types, or All to clear the type filter. Narrow screens provide previous/more arrows for the remaining types.


Update 0.1.3: replacement owns the stock main/app/action/tooltip canvas trees, including nested and newly spawned canvases, while preserving dialog, screen-message and debug canvases. Known Kerbal Engineer Tracking Station display/editor components are suppressed separately because they use IMGUI. Native controllers remain active for verified stock actions. Stock interface and scene exit restore prior enabled flags. Local KSP 1.12.5 and KER assemblies verified the bindings; live hover/toolbar/confirmation/restoration checks remain pending. No game launched or save edited during build.

The 0.1.3 camera fix also assigns the shared viewport to ScaledCamera.cam (planet meshes) and galaxyCamera, alongside the planetarium/orbit, vector, and map FX cameras. Each previous viewport is captured and restored. This corrects the missing-camera binding found from the reported planet/orbit misalignment; actual alignment still requires a live check.
