# Expanse Foundations

A KSP 1.12.5 foundation plugin, developed locally for permanently landed bases.

**0.2.0 development preview.** This holds a landed base through warp and reload,
and supports stock docking, undocking, and EVA part construction. It is not the
complete colony-compatibility release described in the design.

## What it does

Choose **Anchor base** from the Foundations toolbar window in flight. At 1×,
the plugin waits for stock easing to finish and for two seconds of settled
motion, then records the actual position and orientation of the structure.
Defaults are 10 mm/s surface speed and 0.05°/s measured structural rotation
over that interval. Instantaneous Rigidbody rotation spikes do not restart
the rotation measurement; actual continued rotation does.
The base stays kinematic at that body-relative pose. It follows the rotating
planet and floating origin without integrating creep into a new position.

The saved reference is a structural part, not centre of mass or station name.
Filling tanks or renaming the vessel does not redefine the anchor. Coordinates
are written to a versioned Scenario in the same KSP save as the vessel. A
VesselModule marker contains only the anchor ID. Quickloading therefore loads
the corresponding anchor along with the world state.

**Release base** hands the vessel back to stock physics at its held pose.
USI Ground Tether is suspended while Foundations owns the vessel and its
previous state is restored on release. No added parts or USI installation are
required. A separate Harmony runtime is required (the user's game has it).

## Docking and construction

Stock docking ports on an anchored base can be approached and captured. The
incoming craft becomes part of the held assembly without changing the saved
surface pose. On undocking, only the component containing the foundation
reference remains anchored; the visitor resumes normal physics. Stock EVA
construction can detach and attach cargo parts while the base stays held.

The reference part is identified by persistent part ID, so vessel names and
KSP's choice of merged root do not move the anchor. Save data keeps the same
schema as 0.1.0; existing anchors are read without conversion. Only one
foundation can own a merged vessel. Release one base before joining two
independently anchored bases.

## Preview boundaries

- KAS link-capable parts, robotics and conflicting hold modules are rejected.
  Installing KAS elsewhere in the game does not disable Foundations.
- KIS and USI mod-specific construction, moving existing anchored members,
  reference-part destruction, and attaching two anchored bases are not supported.
- Selective unpacking permits leaf deployables to update while their structural
  rigidbody stays kinematic. Solar functionality requires gameplay validation;
  do not infer it from compilation.
- There is a scoped stock portrait EVA query adapter. FreeIVA, crew transitions,
  production catch-up, KJR, KIS and USI construction require separate tests.
- This does not fix every form of the Kraken, and it is not an orbital hold.
- A malformed anchor is retained in the save and the corresponding vessel is
  kept packed. There is no automatic destructive reset.
- Do not remove the plugin while relying on an anchor: stock KSP will resume
  normal physics without it. Release and save first when uninstalling.

The application window labels this preview and its operation restrictions.
Measured pre-correction errors are real; KER readings are not modified.

## Build and test

Requirements: .NET SDK 8, local KSP 1.12.5 files, and the installed `000_Harmony`
runtime. The plugin targets .NET Framework 4.7.2, matching the local Harmony
assembly. No System.Runtime.Serialization dependency is introduced.

```powershell
.\tools\build.ps1
.\tools\test-game.ps1
```

The first command runs the portable pose tests, builds against the local KSP
assemblies and stages `GameData/ExpanseFoundations`. The second also compiles a
development-only harness, deploys only to
`C:\Users\griff\Documents\KSP-RMM-Dev`, mutes that installation, and launches a
stock-derived fixture in a dedicated `Foundations-Test` save. It does not operate the
production game. The fixture is an existing stock craft copied from the local
game. The solar fixture keeps an exact four-part connected subtree and its
original transforms/attachments; it removes the tall station's other parts.
No proprietary game binaries or craft files belong in the release zip.

Use `test-game.ps1 -Minmus` to place the fixture on Minmus with stock easing.
`-DockingCycles` exercises 20 stock docking topology cycles on a seven-part
stock-derived fixture. `-DockingPersistence` checks the docked save and reload.
`-StockConstruction` checks stock EVA construction's attach/detach methods.
Use `test-game.ps1 -Resume` after a completed full run to test the recorded anchor
in a fresh KSP process, without rebuilding the fixture.
Runtime results go to timestamped `foundations-tests-*.txt` files in the development installation.
The harness is excluded from distribution. See `TEST-EVIDENCE.md` for actual
results and checks that have not been performed.

## Internals

`Vessel.GoOffRails` is intercepted before its ground repositioning and part
unpack loop. `Part.Unpack` is separately guarded. `VesselPrecalculate.SetLandedPosRot`
places held parts from the immutable record instead of stock pristine geometry.
`LateUpdate` repairs scene-frame changes. Parent parts are placed before their
children. The original `orgPos/orgRot` are not
overwritten. Fixed rigidbodies remain kinematic, including the roots of supported
deployables. All game access is on Unity's main thread.

The `core/Pose.cs` file is shared by the plugin and console tests; it uses doubles
and invariant round-trip serialization. Runtime settings affect engagement
thresholds only and can be reloaded from the window.

The Details export includes a CSV of the latest 2,048 measurements, sampled at
most twice per real second. It records the largest placed-part position and
orientation error. Sampling does not write files or change warp; export is
explicit. The saved anchor is included separately in the export.

## Attribution

The packed-hold approach, selective deployable handling, wheel initialization
workaround and portrait EVA query adaptation derive from gotmachine's MIT-licensed
PhysicsHold, reviewed at commit
`bf2fbe91b4a07b3cc2bf14a97f62d55ebd8dd585`.
The complete upstream notice is in `licenses/PhysicsHold-MIT.txt`.
The pose registry, fixed-pose placement, early gates, UI and test harness here
are a separate implementation. Decompiled local KSP code was used to inspect
API ordering, not copied into this distribution.
