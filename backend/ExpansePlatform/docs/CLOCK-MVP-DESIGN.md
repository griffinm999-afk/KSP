# Expanse Foundations: clock MVP

Architecture decision and implementation handoff, 26 September 2026.

## Purpose and scope

Deliver a runnable independent KSP bridge, external Host, and standalone Windows Manager that displays the current KSP clock. This is the first Expanse Foundations platform milestone. The existing ExpanseFoundations anchoring plugin is a separate component and must not be overwritten. RMM is not a dependency. No resource, funds, vessel, save-state or time-warp mutations belong in the shipping bridge.

The user wants Astra High to own architecture and final review, Sol to package/integrate work, and Luna to do most implementation and verification. Root waits after this design; Sol reports milestones and escalates novel architectural questions. Use at most two Luna workers concurrently with root and Sol present. Assign exclusive file ownership. Keep a short work/evidence ledger by model and record rework without inventing token or monetary figures.

## Deliverable and placement

New source root: ExpansePlatform/ within the current workspace. Keep source, build scripts, tests and docs here. Use distinctive plugin identity Expanse.WorldBridge.dll under GameData/ExpanseWorldBridge/Plugins. Preserve existing RMM, kOS/MechJeb ExpanseBridge, and anchoring projects.

Use .NET Framework 4.7.2 for the KSP plugin with the locally available reference assemblies and KSP 1.12.5 references. Use .NET 8 for the external Host and WPF Windows Manager. Pin/reproduce builds and reference only required Unity/KSP assemblies; never distribute game DLLs. The plugin should have no third-party runtime dependencies or System.Runtime.Serialization dependency. A small fixed-schema JSON encoder is acceptable; handle escaping, invariant numbers, nulls and non-finite values explicitly and test it against the Host parser.

Conceptual dependency direction:

    KSP main thread -> single latest sample -> bridge worker -> local pipe
                                                            |
                                                   Expanse Host
                                                 shared clock state
                                                            |
                                                 local view interface
                                                            |
                                                  Windows Manager

Keep Host state/validation and protocol types in a library independent of Unity and WPF. The console Host and tests use this library. The Manager formats and displays the Host's view and never implements a second set of connection/session rules. Later the same Host will own domain commands for in-game and standalone clients, but this MVP exposes clock reads only.

## Local connection

Preferred transport is Windows named pipes restricted to the current Windows user. Host is the server; the plugin initiates a connection on its worker. A second pipe serves Manager snapshots. Use distinct versioned endpoint names including a stable user namespace. Verify older KSP Mono compatibility early. If pipe support actually fails in the game, escalate with evidence before switching transport; a loopback-only authenticated alternative is a bounded fallback, not a parallel implementation.

Frame JSON with a 4-byte little-endian byte length followed by UTF-8 JSON, maximum 64 KiB. Reject invalid length, malformed UTF-8/JSON, unsupported protocol version, invalid numeric fields and excessive field sizes. Set bounded connection attempts and close streams to cancel shutdown/read/write. Reconnect with a modest real-time delay, avoiding busy loops. Do not put blocking transport, serialization, file I/O or logging in a per-frame KSP callback.

Prefer a single active KSP publisher in this first Host. A second publisher must be rejected visibly rather than silently replacing the first; after disconnect a new connection can be accepted. Manager clients must never be accepted on the publisher endpoint. Use bounded asynchronous snapshot requests or broadcast; one slow Manager cannot block clock ingestion. Host failure, malformed clients, and Manager exit must leave the game responsive. Host lives independently of the Manager.

## Sampling and presentation contract

Sample about twice per real second from a persistent KSP addon on Unity's main thread using a real-time monotonic cadence, independent of game UT. Read only a few facts, never enumerate vessels or parts. Publish an immutable snapshot by atomic exchange into one slot. New samples replace unsent samples. The worker takes a snapshot and handles encoding/traffic. No unbounded queue, no catch-up for missed clock samples, no per-game-hour work.

Minimum publisher fields:

- protocolVersion, messageType, monotonically increasing sample sequence;
- bridge process/session GUID and load epoch identifier;
- install/save namespace, save folder and display title when a world is active;
- nullable UT seconds, active-world flag, scene, pause indication where verified;
- optional authoritative KSP-rendered date string and warp display metadata if API access is verified.

UT is authoritative, including backward changes. Never infer continued game progress using wall time. Display raw UT seconds accurately and a friendly KSP calendar string when available. Prefer KSP's formatter to guessed day/year constants. The UI can refresh twice per second and must show the latest received sample rather than extrapolating through high warp.

Manager UI: a clean readable light window titled 'Expanse Foundations', dominant clock, small save label and connection/freshness status. No empty trading tabs or invented inventory. Distinguish Host unavailable, waiting for KSP, game running with no loaded save, live sample, paused (only with verified signal), and stale/disconnected sample. Keep the last time visible with an explicit stale status. Host reception age uses a monotonic timer; crossing about 3 seconds without a new sample marks stale. A paused game still publishes heartbeats. Reports of freshness refer to receipt, not synchronization precision.

## Save and session identity

Every KSP process has a fresh session GUID. Every genuine game load/quickload/revert starts a fresh load epoch even if UT and save folder are identical. Ordinary scene changes retain the epoch. Verify the actual KSP load events and timing in local assemblies/game; reset and suppress active-world publication during an unresolved load transition so old values cannot be labeled as the new world. A decreasing UT without an observed load event also starts a new epoch defensively.

Install path plus save folder is only a read-only namespace for this clock MVP. It is NOT a durable universe identity and does not promise safety for copying or renaming saves. A saved world UUID and checkpoint/receipt capsule belong to the later persistence milestone. Do not write to the save just to implement the clock.

The Host rejects regressing sequence numbers within a bridge session and stale transport generations. Epoch changes clear the previous live-world view before accepting the new one. Switching to the main menu must not keep displaying an old clock as live. Host restart waits for a fresh sample; no persisted telemetry is passed off as current. This is observational load handling, not a completed transaction recovery system.

## Build, launch and verification

Use C:\Users\griff\Documents\KSP-RMM-Dev as the existing isolated game installation. Build may read references from C:\Kerbal Space Program; production deployment and save edits are prohibited. Scripts must validate resolved paths, refuse the production tree or aliases into it, and refuse replacing DLLs while the target development game runs. Never stop the user's KSP.

Provide build, automated test, deploy-dev, launch-manager/host, and muted launch-dev commands. Explicitly zero audio settings in the development install before any test launch. Start background test processes hidden; opening the finished Manager is useful for the user and authorized. Do not take desktop control. Preserve any existing dev assets; back up dev DLLs/configs before replacement.

Luna should implement and exercise these checks:

1. Clean builds against actual KSP references and runnable Windows output.
2. Protocol round trips, escaping, partial/truncated/oversized frames, invalid values and versions.
3. Same-time epoch change, backward UT, same-name different install, main menu/no world, stale publisher, reordered samples, and reconnect tests.
4. Real Host + fixture publisher + Manager-view client integration: huge UT jumps, publisher stalls, Host restart, concurrent readers, bounded state and no historical sample backlog. Label synthetic publishers conspicuously and keep them out of the normal launcher.
5. UI launch and binding/freshness verification; a visual check when available without desktop control. Report any unverified rendering.
6. Real muted development KSP smoke: confirm plugin loads without exceptions and actual game samples reach the external Host. Test pause, high warp and quickload if safely automatable through a separate dev-only fixture harness. A harness may load only an explicitly named disposable test save under the dev path. Mutating test controls must never ship in the bridge. No silent desktop interaction.
7. Measure bounded sample callback cost and behavior under absent/stalled Host; report sample count and observed latency/performance. Synthetic throughput tests do not prove in-game hitch freedom. If a game scenario cannot be exercised, report exactly what remains unverified rather than claim it passed.

Prefer targeted meaningful verification. Do not launch multiple games or repeat large test suites without a changed risk. A real game test may use existing proven dev harness techniques after inspection; this is an ordinary implementation decision for Sol. Every process launched by our scripts should have its path/PID recorded for identifying our own helpers; never close user-owned processes.

## Work packages and acceptance

Sol should split into two exclusive Luna packages initially: protocol/Host/tests and KSP bridge/Manager/tooling, or an equivalent split with written interface agreement before concurrent editing. Sol resolves routine integration issues and sends larger implementation fixes back to Luna. Milestone completion report includes exact launch commands, artifact paths, build/test evidence, known integration limits and model work ledger. Root reviews architecture-sensitive issues and the final result.

Accepted MVP: user can launch the real Manager and separate Host, run muted dev KSP, see a clearly identified live clock, and detect loss of freshness. Code/tests show bounded transport and no game-thread waiting; actual runtime checks are distinguished from synthetic checks. Future virtual trade, economy, production, research, anchoring integration, Windows control commands and AI remain subsequent milestones.
