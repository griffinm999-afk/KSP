# WOLF colony administration

The colony manager exposes WOLF biome depots alongside the physical settlement.
WOLF resources represent ongoing capacity, not units stored in a tank. WOLF
Power is separate from physical ElectricCharge and the EC/s power chart.

## Using the controls

1. Start KSP and load the intended save. The colony itself does not need to be
   the active vessel for its WOLF depot to be available.
2. In the app, choose **Colony**, select the body and biome, and open **WOLF**.
3. Review the resource's incoming supply, outgoing allocations, and available
   capacity. Choose the resource and either add capacity or set its total supply.
4. Check the resulting supply and available capacity, then apply the change.
5. Save the game normally to retain the change on disk.

For example, a depot with 35 Power supplied and 35 allocated has none available.
Adding 50 makes supply 85, leaves allocations at 35, and makes 50 available.
Setting supply to 50 instead leaves 15 available. Existing allocations cannot
be removed by reducing supply below them.

This is a deliberate administrative shortcut. It bypasses delivering and
consuming WOLF modules, their construction cost, and their recipe prerequisites.
It does not create physical cargo, Kerbals, generators, or new transport routes.
Adding Gypsum makes WOLF capacity available to the depot; a WOLF hopper is still
needed when a physical facility needs actual Gypsum in a tank. Adding an
EngineerCrewPoint changes a virtual resource, not the crew aboard the base.

## Implementation boundaries

- The bridge observes WOLF's loaded registry and uses its depot provider API to
  adjust incoming capacity. Existing outgoing allocations remain unchanged.
- Changes run on KSP's main thread and target an established depot in the exact
  world, execution run, session, and load epoch shown by the app.
- The request includes the observed incoming and outgoing values. A conflicting
  change is rejected so an old screen cannot silently overwrite newer values.
- Requests specify a target total and have a request identity. Repeated delivery
  of the same request must not add capacity twice.
- WOLF's normal scenario OnSave/OnLoad persists the resource streams. The app
  does not rewrite persistent.sfs behind a running game and does not force a save.
- The administration channel is separate from physical delivery recovery state.
  A game load or revert follows the state of that save; it does not replay old
  WOLF administration actions.

## Verification

`run/wolf-api-smoke` exercises the installed WOLF assembly with an isolated,
in-memory depot. It checks addition, preservation of allocations, refusal to
remove committed capacity, new resource streams, and ConfigNode save/load
persistence. It never loads or writes a player's save.

Live in-game edits remain a separate validation step after loading the updated
bridge. An app receipt confirms application to the active game; the subsequent
normal game save provides disk persistence.

### Installed verification, 2026-09-30

- 96 Core/Host tests passed, including WOLF observation isolation, deficits,
  command framing/correlation, input limits, and preview endpoint isolation.
- Manager and WorldBridge Release builds completed with no warnings or errors.
- WPF smoke passed at 1180×930 and 860×680; checks cover selected resource and
  draft preservation, in-flight/stale/no-world controls, and overflow preview.
- The installed WOLF API harness passed 11 checks using an in-memory depot.
- Patch `ExpanseFoundations-wolf-admin-20260930-065500` was installed with verified
  file hashes and backups. Manager and Host restarted, and the Host returned
  `waitingForKsp` as expected. The player's persistent save hash was unchanged.
- An initial installation attempt encountered a brief DLL lock after application
  exit and changed no installed file hashes. A bounded copy retry completed the
  installation. No KSP process was stopped or started.

### Startup repair, 2026-09-30

- The first WOLF bridge build referenced `System.Runtime.Serialization`, which
  this KSP/Mono installation could not load. Kopernicus consequently failed to
  load its custom planets. The bridge now uses Unity's bundled JSON serializer.
- KSP wrote `persistent.sfs` once after the warning. A copy of that file and a
  pre-warning quicksave are preserved under
  `artifacts/save-safety/20260930-kopernicus-warning`. Neither was restored or
  edited. The post-warning save and earlier quicksave each list the same 56
  vessel IDs, but that comparison does not prove every save field is intact.
- The WOLF pipe's custom ACL constructor also proved unsupported in KSP's Mono
  runtime. The bridge now uses the standard named-pipe constructor. An invalid,
  non-mutating request returned the expected validation rejection in KSP.
- With both repairs installed, a fresh KSP startup reached the main menu and
  Kopernicus completed loading 66 bodies without a missing-assembly error. KSP
  was closed after verification; the persistent save hash was unchanged by the
  verification runs. A live, valid WOLF edit and subsequent game save remain
  unverified.

### Greater Flats capacity adjustment, 2026-09-30

- A live `The Expanse` session at Minmus:Greater Flats accepted 25 distinct
  WOLF admin changes. Each resource that had zero **available** capacity was
  given 50 available points. Metals was checked first; the other 24 followed.
  All 25 in-game receipts reported `applied` and `available: 50`.
- This includes previously absent resource streams and the two fully allocated
  veins: ExoticMineralsVein changed from 5 supplied / 5 allocated to 55 / 5,
  and SubstrateVein from 15 / 15 to 65 / 15. Existing nonzero-available
  resources and Minmus:Midlands were not changed.
- The before-change save is preserved at
  `artifacts/save-safety/20260930-wolf-zero-before/persistent-before-wolf-bulk.sfs`.
  Individual receipts are in `outputs/wolf-zero-metals-test.json` and
  `outputs/wolf-zero-remaining-report.json`. A normal KSP save is required to
  persist the live changes; receipts alone do not establish disk persistence.
- The 25 changes brought Greater Flats above the bridge's old 48-resource
  observation limit. A bridge update raises that per-depot display limit to 80,
  enough for the installed 63-resource WOLF catalog. The Manager's WOLF table
  now scrolls its rows under a fixed column header, with the editor collapsed
  above it. The isolated WPF smoke checks header position after scrolling 63
  rows at 860×680. Both updated projects build without warnings.
