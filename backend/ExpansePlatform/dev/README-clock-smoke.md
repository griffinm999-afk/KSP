# Clock bridge dev smoke harness

This is a development-only KSP addon. It has a hard-coded root guard for `C:\Users\griff\Documents\KSP-RMM-Dev`, does nothing without the one-shot `ExpanseClockSmoke.request` file, and has no release packaging reference. The observer is a small .NET 8 console client that reads the Host through `ClockViewClient` and writes one JSON object per poll.

Build from the workspace root:

```powershell
dotnet build ExpansePlatform/dev/Expanse.Clock.SmokeHarness/Expanse.Clock.SmokeHarness.csproj -c Release
dotnet build ExpansePlatform/dev/Expanse.Clock.Observer/Expanse.Clock.Observer.csproj -c Release
```

The harness DLL is produced at `ExpansePlatform/dev/Expanse.Clock.SmokeHarness/bin/Release/net472/Expanse.Clock.SmokeHarness.dll`; the observer is at `ExpansePlatform/dev/Expanse.Clock.Observer/bin/Release/net8.0/Expanse.Clock.Observer.dll`. Stage the harness DLL into the dev install's `GameData/ExpanseClockSmoke/Plugins` only after reviewing/backing up the destination. Do not copy it into the production KSP tree or distribution.

After staging, make sure no KSP process is open and run:

```powershell
.\ExpansePlatform\dev\prepare-clock-smoke.ps1
```

The script checks the exact development root and KSP managed references, refuses a running KSP process or an existing request file, saves a timestamped backup of `settings.cfg`, zeros all six master/channel volume settings, and creates one fresh GUID request file. It does not launch KSP. Start the normal Clock Host first, then start `C:\Users\griff\Documents\KSP-RMM-Dev\KSP_x64.exe` manually. The addon creates a fresh `ExpanseClockSmoke-<guid>` save folder only after it validates the exact root and consumes the one-shot request. It never loads or edits another save.

Start the observer in another terminal, for at most ten minutes:

```powershell
dotnet .\ExpansePlatform\dev\Expanse.Clock.Observer\bin\Release\net8.0\Expanse.Clock.Observer.dll --duration-seconds 180 --output C:\Users\griff\Documents\KSP-RMM-Dev\ExpanseClockSmoke-observer.jsonl
```

The addon creates the disposable save, then copies the installed stock Making History `Muna 1.craft` into the dev root under the request GUID and uses `FlightDriver.StartWithNewLaunch` to enter Flight. It waits for the active vessel and all parts to be loaded before testing. During a four-second `FlightDriver.SetPause(true,false)` window, it logs `FlightDriver.Pause`, `Planetarium.Pause`, and `Time.timeScale`; it accepts either KSP pause flag as evidence and fails if both remain false. It then unpauses, briefly requests warp rate index 5, returns to rate index 0 (1×), saves, and reloads the same save folder/name into Flight. `TimeWarp.SetRate` takes a rate index; index 5 is not a 5× multiplier. Around the save/reload it pauses the game, sets one exact UT with `Planetarium.SetUniversalTime`, and holds that UT across two three-second observer windows. It logs identical pre-load and post-load UT values and fails if either drifts by more than 0.001 seconds; the Host observer should show a fresh `loadEpoch` at the same save identity and time. On PASS or FAIL after the request is accepted, it restores rate index 0/unpaused state, logs bridge sample callback count/mean/max timing through reflection, and calls `Application.Quit()`. The text log is `ExpanseClockSmoke-<request-guid>.log` in the dev root. The JSONL observer captures timestamp, Host status and age, connection state, session GUID, load epoch GUID, sample sequence, install/save identity, UT, pause, and warp metadata. Verify sequence and UT keep updating in the pause window, UT advances during warp, and the Host does not interpolate time.

The generated save is intentionally disposable but is retained for inspection at `saves/ExpanseClockSmoke-<guid>`; remove only that exact folder after KSP has closed and after reviewing the logs. The harness restores 1x and unpauses if an assertion fails. The completed muted dev smoke run is recorded in `../docs/WORK-EVIDENCE.md`. Its staged harness has since been moved out of active GameData to `../artifacts/deploy-backups/ExpanseClockSmoke.GameData.20260926-015902-589`; restage it deliberately and create a fresh one-shot request before any rerun.
