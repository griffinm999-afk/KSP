# Tracking Station 0.1.2 work evidence

Scope: standalone plugin, pure tests and own package/default Tracking Station presentation. WorldBridge, Clock Host, Manager, pending Ore changes, saves and game operation are excluded. GPT-6.1 Sol owns this replacement revision and integration review under current AGENTS.md; no older-model delegation was used.

## Verified API and boundaries

Local KSP 1.12.5 DLLs verify dynamic bodies/vessels, isStar/referenceBody/bodyName and the LocalizeRemoveGender extension used for display labels. Native serialized/public list/tabs/buttons define presentation targets. Reversible CanvasGroup suppression changes alpha/raycast blocking only, keeping active objects/native interactability and preserving original state. Stock fallback, disable/destroy and caught UI failure restore changes.

Verified PlanetariumCamera.Camera, MapView.VectorCamera and mapFX camera fields receive the right-side viewport and restore original viewports; shared UI cameras are excluded. The native map remains live. Fly/Recover/Track/Delete/Leave use native button events, not replicated mutations. Vessel actions resolve current GUID, reject other tracking locks, call SetVessel and recheck identity/eligibility. Private stock FlyVessel guards remain intact. No action was executed during development. Focus uses SetTarget, never the save/reload GoToAndFocusVessel helper.

Custom locks include MAP_UI/CAMERACONTROLS over owned shell controls. Other TRACKINGSTATION_UI locks are respected, including focus-follow. Own locks release before native actions and on focus loss/cleanup. MapViewFiltering lacks a full per-vessel predicate, so list-only filtering is explicit. Preferences are plugin-owned; game references have Private=false and are not redistributed.

## Validation

Release build against actual KSP/Unity DLLs: PASS, 0 warnings/errors. Sol/root reviewed automatic startup, native presentation restoration, viewport and action/lock boundaries before packaging. Pure tests: PASS for multi-star/deep hierarchy, missing parents/self-parent/cycles, duplicate identities, scopes/counts, AND/OR predicates, unknown crew, every sort, search restore and view reconciliation. Synthetic 10,000-vessel query: 49 ms on this run is not Unity frame performance.

Actual GUI and tests share TrackingShellLayout. Geometry checks pass at 3840×2160 / UI2 / plugin 100% and 150%, 2560×1440 / UI2, 1920×1080 / UI1, 1280×720 / UI2 and 640×480 / UI2. Checks cover tree rows, card containment, sidebar proportion and accessible widths. User default 3840 / UI2: logical 1920×1080, sidebar 1324.8 physical pixels (34.5%), base font 36 physical pixels, row height 88 and 12 visible rows. UI_SCALE_APPS 1.5 is not multiplied again. Control fonts/padding are explicit; CalcSize fits captions. Evidence: workspace outputs/tracking-station-0.1.2-layout-metrics.json and tracking-station-0.1.2-tests.txt. Geometry does not reproduce Unity glyph rasterization or prove runtime appearance.

Earlier read-only production-save replay of unchanged query core: PASS for 60 unique summaries; Base/Rover+LANDED+Crewed 6, Minmus Mining Large search 1, exact reference-index scope 21 without leakage. Bodies were synthetic index groups, not live topology. Save SHA stayed 385FAEB8A510C9330DD2A3489F2F5480D8A944DC38CF4886A3C08D96E85528BC. Evidence: workspace outputs/tracking-save-replay-result.json and verifier.

Guarded installer filesystem tests check inert fake-game paths only: wrong hash/identity rejection, WhatIf, fresh/update hashes, preference/unrelated-file preservation and non-DLL backups. Final package audit is recorded in workspace outputs/tracking-station-0.1.2-package-audit.json. Package contains one own DLL plus documentation/installer; no game DLLs, saves, live fixtures or unrelated components. Production installation is a separate root-owned closed-game step with baseline verification.

## Runtime limitations

The user's v0.1 overlay screenshot established inherited-font clipping and the need for literal replacement. Version 0.1.2 supplies the default full shell and private measured styles. No new KSP process was launched/operated or save mutated during this revision. Actual 4K / UI2 rendering, marker alignment, popup layering, toolbar/timewarp access, eligibility/confirmations, planet packs/other mods, scene/save preference transitions and IMGUI fleet cost remain unverified until supervised runtime review. Complete concept parity is not claimed: resources/colony tags, adjustable columns, arrow-key navigation and full map predicates remain deferred.




Update 0.1.2: reduced default sidebar to 28%, base text to 14 logical pixels, tree rows to 32, header/footer to 78/52 (including the permanent vessel-type strip), and selection card to 600 by 166. Added 75% and 85% scale choices; corrected a focus-button branch that drew the empty-selection hint over the button. Build/layout checks verify geometry; revised in-game appearance remains pending.


Update 0.1.3: replacement owns the stock main/app/action/tooltip canvas trees, including nested and newly spawned canvases, while preserving dialog, screen-message and debug canvases. Known Kerbal Engineer Tracking Station display/editor components are suppressed separately because they use IMGUI. Native controllers remain active for verified stock actions. Stock interface and scene exit restore prior enabled flags. Local KSP 1.12.5 and KER assemblies verified the bindings; live hover/toolbar/confirmation/restoration checks remain pending. No game launched or save edited during build.

The 0.1.3 camera fix also assigns the shared viewport to ScaledCamera.cam (planet meshes) and galaxyCamera, alongside the planetarium/orbit, vector, and map FX cameras. Each previous viewport is captured and restored. This corrects the missing-camera binding found from the reported planet/orbit misalignment; actual alignment still requires a live check.
