# Implementation notes

The design is `../outputs/Expanse-Foundations-Design.md`. This is the foundation
and stock topology preview, not all six design milestones.

## Local API findings

The local KSP 1.12.5 `Vessel.GoOffRails()` checks packed/loaded/expiry, may invoke
`CheckGroundCollision()`, clears physics hold, switches the reference frame,
unpacks parts and resumes velocity, then fires the off-rails event. The prefix
used here runs before all of that. An off-rails event listener would be too late.

`Part.Pack()` makes its rigidbodies kinematic and marks joints unbreakable.
`Part.Unpack()` reverses the kinematic flag and rebuilds joints. It is guarded
separately because other mods can call it directly.

`VesselPrecalculate.SetLandedPosRot()` is the packed surface-placement writer.
The prefix replaces only the held-vessel path. It places the captured settled
geometry without modifying construction `orgPos/orgRot`. Ordinary vessels retain
stock behavior.

Stock `Part.ResumeVelocity()` knows the landed/reference-frame convention.
Release uses stock GoOffRails and ResumeVelocity, while suppressing ground
repositioning for that handoff. Kinematic flags and USI tether state are saved
per member. Release authorization is scoped to one vessel, and a deployable's
unpack authorization is scoped to one part.

USI's part field alone is insufficient: ModuleStabilization has separate state
updated by `onStabilizationToggle`. The adapter updates that event as well as
the part UI state; its FixedUpdate is suppressed only for a Foundations-held
vessel. USI is optional, so an absent type must not fail Harmony initialization.

## Changes from the initial proposal

The proposed default 0.01°/s instantaneous angular-velocity gate proved too
strict for the stock test structures. The implementation measures actual
body-relative structural rotation over the settling interval and uses a
configurable 0.05°/s default. This changes entry qualification, not the held
pose's allowed drift.

Version 0.2 adds docking discovery against a packed foundation. Coupling moves
the visiting craft to the unchanged foundation pose if KSP selected the base as
the docking source. KSP topology edits are reconciled on the following frame:
new parts receive body-relative local poses, removed parts leave the membership
table, and a split retains the hold only on the component containing the saved
reference. The original surface pose remains unchanged. Stock EVA construction
uses different methods, `OnDetachFlight` and `OnAttachFlight`; both are covered.
Incoming cargo parts become kinematic before the next physics step.

## Remaining release gates

- Reference-part removal and damage handling, and merges of two held bases.
- KIS/USI construction adapters and stock EVA GUI playthrough.
- Long-duration drift/warp testing and the actual large cruciform base.
- Wheels/legs after reload and release; FreeIVA and stock crew/EVA gameplay.
- Solar tracking and retraction, active production, unloaded catch-up, KJR and
  KSPCommunityFixes together with the user's mod set.
- Terrain-change recovery and an explicit suspended-anchor repair workflow.

The development harness and decompiled API inspection are not shipped as
production tools. No test automatically modifies The Expanse save.
