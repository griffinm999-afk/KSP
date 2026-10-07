# Test evidence — September 24, 2026

This is a development preview. Passing these checks does not establish the full
colony compatibility promised by the design.

## Build and portable tests

- .NET SDK 8.0.423; plugin target .NET Framework 4.7.2.
- Build against local KSP 1.12.5 and installed Harmony: **0 warnings, 0 errors**.
- Portable suite: **9,016 assertions passed**. It includes 4,000 exact coordinate
  round trips across four cultures, 1,000 rotation/origin/reference-rebase cases,
  quaternion sign equivalence, invalid input rejection and settling-state tests.
- Assembly reference inspection found no `System.Runtime.Serialization` dependency.
  References are mscorlib, System, System.Core, Assembly-CSharp, 0Harmony and the
  Unity engine modules used by the plugin.

## Full runtime run

Run began at **2026-09-25 00:39:15 UTC** in the muted, isolated
`C:\Users\griff\Documents\KSP-RMM-Dev` installation. It used a separate
`Foundations-Test` save and a four-part fixture on Minmus near 1° latitude,
1° longitude, placed using stock surface easing.

The fixture retains the command cabin, Hitchhiker cabin and two Gigantor solar
arrays from the local stock Space Station Core, with original transforms and
attachments. Static tests verified one connected tree and the retained cabin
connection. It is a test fixture, not a flight-qualified craft or a substitute
for the user's large base.

**Passed:**

- 1,000 exact round trips through KSP's native ConfigNode serialization.
- Loaded anchoring and 300 consecutive physics steps with the vessel packed and
  all structural rigidbodies kinematic.
- Renaming and halving resource amounts without changing the authoritative pose.
- Restoring the pose after a deliberate 25 cm root displacement.
- 100 explicit `Vessel.GoOffRails()` calls without allowing dynamic physics.
- Both solar arrays deployed completely.
- **100 actual high-warp entry/exit cycles**, with unchanged saved pose and no
  structural rigidbody becoming dynamic; both arrays remained deployed.
- Final largest placed-part error: approximately **1.14e-13 m and 1.51e-7°**.
  This is the end-of-run measurement in the active fixture's local scene frame,
  not a claim of that precision at every location or throughout every frame.
- Save/reload restored the anchored vessel and identical authoritative pose.
- Release returned the vessel to stock physics; measured surface speed three
  seconds later was **0.000318 m/s**. The acceptance ceiling was 0.5 m/s for an
  obvious release kick, not the drift tolerance while anchored.

Full-run plugin SHA-256:
`DB288FC338A8C95828E6F03D9153C82D5451F2DA17D8A02DCE90B22E9C9EC909`.
Raw evidence is retained in `artifacts/runtime-full`.

After that run, two narrow changes were made: unloaded surface placement is
left to stock KSP, and non-finite initial geometry/speed is rejected.

## Final-build cold-start check

Run began at **2026-09-25 00:47:53 UTC** in a fresh KSP process.

**Passed:** native ConfigNode round trips, restoring the recorded Minmus anchor,
300 held physics steps, unchanged anchor data, and release after cold loading.
Largest placed-part error was **4.52e-7 m and 3.51e-6°**, within the 1 mm /
0.0001° design budget. Surface speed three seconds after release was
**0.000798 m/s**.

Final plugin SHA-256:
`52F283283E876DDCC6E28E114998D19DAB405A6436F96251F949EEBBBEC0D6BD`.
Raw evidence is retained in `artifacts/runtime-cold-start`. The final build is
installed in the development game only. The automatic harness has been removed
from GameData after testing.

The full-run log also contains stock `TemperatureGauge.OnDestroy` /
`PhysicsGlobals.get_ThermalColorsDebug` null-reference exceptions during game
shutdown after the test pass. No Foundation exception occurred during the
successful test sequence. These shutdown messages were not silently discarded.

## 0.2.0 docking and construction development checks

The final 0.2.0 DLL (SHA-256
`1D5138568042C9F510A11C6F580EC6652BFDF883EAC7DE32E97688CF372799EB`)
was built against the local KSP 1.12.5 and Harmony references with zero
warnings or errors. The shared, KSP-independent suite passed 9,016
assertions. All game runs used the isolated, muted development installation
and its separate `Foundations-Test` save.

- A landed seven-part subtree of the installed stock Space Station Core was
  held on Minmus. Stock `Part.decouple` and `Part.Couple` removed and added a
  member while the anchor pose remained unchanged.
- The actual stock EVA construction detach and attach methods removed and
  restored a cargo-capable part. The new part became kinematic immediately;
  the held vessel and anchor membership remained consistent.
- Two stock docking ports discovered each other across a held/free vessel
  boundary. Direct docking and undocking passed from both dominant-vessel
  directions, and 20 repeated topology cycles passed.
- The **normal stock docking state machine** acquired a visitor at the
  skyward-facing port, joined it to the foundation, and undocked it back to
  dynamic physics. A downward-facing fixture port was initially tried and
  caused the test visitor to fall through terrain; the test was corrected to
  use the accessible port.
- A docked assembly retained its anchor and all members through save/reload,
  then undocked correctly.
- A final-build fresh-process load restored a previously saved anchor for
  300 physics steps, then released it. The largest placed-part error was
  4.52e-7 m and 3.51e-6°; surface speed three seconds after release was
  0.00233 m/s, below the 0.5 m/s release-kick ceiling.

These are automated in-game checks of stock methods and the stock docking
state machine. They do not replace a player-operated docking/EVA session on
the user's actual base. The small test visitor was positioned close to the
port by the test harness; the mod contains no approach autopilot.

## Not established by these tests

- One-hour loaded endurance, physics warp endurance and months-long unloaded
  catch-up; the 100 warp cycles do not substitute for these.
- The user's roughly 60 t cruciform base, large part counts, a 1,200 t depot,
  multiple nearby bases, polar sites or sloping terrain.
- KAS/KIS/USI-specific construction, physical link adapters, moving robotics,
  docking two independently anchored bases, or destruction of the reference
  part. The preview does not claim support for these operations.
- Solar tracking accuracy/retraction, stock EVA/crew behavior, FreeIVA, converters,
  KJR, USI and KSPCommunityFixes working together in the production mod set.
- A quantified performance improvement or universal Kraken prevention.

Production KSP and The Expanse save were not used for these tests.
