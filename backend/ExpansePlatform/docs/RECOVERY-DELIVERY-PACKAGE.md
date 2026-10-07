# Recovery and delivery runtime package

October 1 pricing revision: new Ore export route quantities are configurable whole units (1 to 1,000,000), default 1,000, at fixed 100 funds/unit. Default payment is 100,000 funds. Already departed 500/unit shipments retain their original compensation. EXS4 bytes and hashes remain compatible, and unapplied historical-price commands are held. This supersedes the original fixed-price staging description below; package/deployment evidence for this revision must be recorded separately.

October 1 source staging adds modeled Ore recovery exports: a typed virtual Kerbin buyer, 1,000 physical Ore per dispatch, default 64,800-second transit, and exactly 500,000 total Career funds at modeled arrival. This extension is source/synthetic verified and requires package review and an isolated in-game gate before production installation. Preserve matching Bridge/Domain/Host/Manager assemblies together; older readers cannot process EXS4 export capsules. Existing EXS1/2/3 byte/hash compatibility remains preserved until an accepted export transition. `ORE-EXPORT-DESIGN.md` and the new usage section document the economic capability and terminal-fault behavior. Never advertise production availability from a workspace build alone.

This is the runtime inventory for the installed delivery package. Background Resource Processing 0.2.7 supports physical debit and credit effects for registered selected tanks on unloaded vessels when delivery readiness passes; loaded endpoints are unsupported for physical delivery effects. The M3a–M5 loaded development runs are historical fixture evidence and do not test the separate BRP 0.2.7 unloaded path. The selected KSP save remains authoritative; Host SQLite data mirrors accepted state and scheduler history. Development verification uses explicit per-user `dev` pipes and an isolated run-specific Host `--data-dir`.

## Runtime files

| Runtime | Include | Install/run location |
| --- | --- | --- |
| KSP Bridge | `Expanse.WorldBridge.dll` and matching `Expanse.Domain.dll` from `src/Expanse.WorldBridge/bin/Release/net472/` | `C:\Kerbal Space Program/GameData/ExpanseWorldBridge/Plugins/` |
| Host | The complete `src/Expanse.Clock.Host/bin/Release/net8.0/` output, including the Host executable, DLL, runtime/dependency manifests, Core/Domain, SQLite and SQLitePCLRaw dependencies | Keep together in one Host directory; run `Expanse.Clock.Host.exe` or `dotnet Expanse.Clock.Host.dll` |
| Manager | The complete `src/Expanse.Clock.Manager/bin/Release/net8.0-windows/` output, including the Manager executable, DLL, runtime/dependency manifests, Core/Domain | Keep together in one Manager directory; Windows requires the .NET 8 Desktop Runtime |

The KSP modules register through `KSPAddon` and `KSPScenario` attributes; no separate module configuration file is part of this package. Do not include `Expanse.Recovery.KspFixture.dll`, any observer/smoke harness, one-shot request, dev save, PDB, `bin/obj` trees, or test output in a runtime release. PDBs may be retained separately for diagnostics.

## Development fixture disable and backup

The bounded physical fixtures are development-only. The final fixture was disabled after the accepted run: its former active path `C:\Users\griff\Documents\KSP-RMM-Dev\GameData\ExpanseRecoveryDevOnly\Plugins\Expanse.Recovery.KspFixture.dll` is absent, and its copy is retained in `artifacts/deploy-backups/ExpanseRecoveryDevOnly-disabled-20260926-122153` with SHA256 `BF163DB413FFC42FFC031106AA12CE1F94BCCA91083447B24E3B91F03F836959`. One-shot request paths `C:\Users\griff\Documents\KSP-RMM-Dev\ExpansePhysical3bSmoke.request` and `C:\Users\griff\Documents\KSP-RMM-Dev\ExpanseScheduleM5Smoke.request` are absent after consumption. The schedule request created only a token-derived Muna craft variant, changing a selected LiquidFuel amount to 400 while preserving the stock craft. Fixture logs and disposable saves remain under the approved dev root for reproduction. Fixtures must never be copied into production or any general runtime package.

After the fixture has completed and the dev game has exited naturally:

1. Verify no KSP PID is running from the dev executable path and no unresolved KSP process remains. Do not stop or inspect-control the user's production game.
2. Verify the fixture request was consumed. Preserve the token log, derived craft, disposable save folder, Host data directory, and command output for evidence; do not delete or overwrite them.
3. Move the complete `GameData\ExpanseRecoveryDevOnly` directory to a new timestamped child of `ExpansePlatform\artifacts\deploy-backups\` (for example `ExpanseRecoveryDevOnly-disabled-<UTC timestamp>`). Confirm the active GameData path no longer contains that directory and hash the retained fixture DLL.
4. Keep the development fixture source and DLL under `ExpansePlatform\dev\Expanse.Recovery.KspFixture`; build/deploy scripts must continue targeting only the approved dev install.

Do not remove or alter `ExpanseWorldBridge`, the save's `EXPANSE_RECOVERY` Scenario node, the saved PART/BRP records, or the accepted Host database as part of fixture cleanup. A save or Host database backup is a separate evidence artifact and must be copied before any deliberate destructive test.

## Source and package backup manifest

Create this manifest beside each reviewed package under `ExpansePlatform\artifacts\source-backups\<UTC timestamp>\manifest.txt`. Keep the source snapshot and package archives outside active KSP `GameData`; record the exact source commit or snapshot identifier and SHA256 for every listed file. Do not infer a release hash from a previous build after Domain/Host changes.

| Manifest item | Required contents / record |
| --- | --- |
| Source snapshot | `src/Expanse.Domain`, `src/Expanse.Clock.Core`, `src/Expanse.Clock.Host`, `src/Expanse.Clock.Manager`, `src/Expanse.WorldBridge`, `tests`, `tools`, and the recovery/delivery interface, usage, package, and work-evidence documents. Record commit ID when the checkout has one; otherwise record a timestamped source archive hash. |
| Dev fixture source | `dev/Expanse.Recovery.KspFixture` source and project file; clearly label as non-runtime. |
| KSP install preimage | Before a dev replacement, copy the existing `Expanse.WorldBridge.dll` and `Expanse.Domain.dll` from the exact dev plugin directory to `artifacts/deploy-backups`; record original and replacement SHA256 values and the install root. Production changes are outside this dev package procedure. |
| Desktop runtime outputs | Archive complete Host and Manager Release output directories. Record the archive SHA256 and each entry's relative path, length, and SHA256. |
| Runtime state, if relevant | Record exact disposable save-folder/token, selected `.sfs` hash, isolated Host `--data-dir`, and logs. Copy these as evidence; never edit them to manufacture a pass. |
| Gate evidence | List synthetic suite result separately from in-game observations, including exact Bridge/Domain/Host/fixture hashes, test token, and whether save/reload or due arrival actually passed. |

Example manifest fields:

```text
created_utc=
source_commit_or_snapshot_sha256=
ksp_dev_root=
bridge_sha256=
domain_net472_sha256=
host_runtime_archive_sha256=
manager_runtime_archive_sha256=
fixture_sha256_non_runtime=
save_folder_and_token=
selected_save_sha256=
host_data_dir=
synthetic_tests=
in_game_observations=
unverified_gates=
```
