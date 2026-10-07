# Clock MVP implementation interface

This file fixes the initial work-package boundary. The architecture and acceptance criteria remain in `CLOCK-MVP-DESIGN.md`.

## Projects and ownership

- `src/Expanse.Clock.Core/`: .NET 8 protocol, validation, Host state, and Manager view client; owned by protocol/Host worker.
- `src/Expanse.Clock.Host/`: .NET 8 console Host, both pipe servers; owned by protocol/Host worker.
- `tests/`: protocol, state and fixture integration tests; owned by protocol/Host worker.
- `src/Expanse.WorldBridge/`: .NET Framework 4.7.2 KSP plugin; owned by bridge/Manager worker.
- `src/Expanse.Clock.Manager/`: .NET 8 WPF window; owned by bridge/Manager worker.
- `tools/`: build, development deploy, and launch scripts; owned by bridge/Manager worker.
- Root `README.md` and final distribution/verification integration: Sol.

Each worker may add private docs for its component and should tell Sol before changing this interface.

## Wire protocol v1

Each message is UTF-8 JSON preceded by an unsigned 4-byte little-endian byte count, in the range 1..65536. One message per connection is permitted for Manager requests; the publisher connection carries repeated samples. Writers flush each frame. Malformed input closes only its connection.

Publisher sample fields use these exact camel-case keys:

```
protocolVersion: 1
messageType: "clockSample"
sequence: positive integer (Int64)
sessionId: GUID string, newly generated per game process
loadEpoch: GUID string, newly generated per game load
installNamespace: nonempty absolute game root path string
saveFolder: nullable string
saveTitle: nullable string
utSeconds: nullable finite number
activeWorld: boolean
scene: nonempty string
paused: nullable boolean
formattedDate: nullable string
warpRate: nullable finite positive number
```

The bridge emits `activeWorld=false`, `utSeconds=null`, `saveFolder=null`, `saveTitle=null`, `formattedDate=null`, and `warpRate=null` while no save is active or a load is unresolved. `paused=null` means the pause signal is unverified. The Host validates field lengths (maximum 1024 UTF-8 bytes for identity/text values), required fields, field combinations, finite values, monotonically increasing sequence within a session, and rejects unsupported versions. It treats UT decreases and load-epoch changes as new-world transitions. The bridge produces no update command handlers.

Pipe names are `ExpanseFoundations.Clock.Publisher.v1.<USER>` and `ExpanseFoundations.Clock.View.v1.<USER>`, where `<USER>` is the nonempty `Environment.UserName` of the current Windows account. KSP 1.12 Mono throws `NotImplementedException` for `WindowsIdentity.GetCurrent().User.Value`; the account-name suffix avoids that API. Host servers use `PipeOptions.CurrentUserOnly` on Windows, so the suffix identifies the endpoint while the pipe security restricts access. The plugin is a named-pipe client and only ever connects to the publisher pipe. Both endpoints have bounded connection and I/O behavior; the game callback never touches them.

The Manager sends one framed `{ "protocolVersion": 1, "messageType": "getSnapshot" }` request to the view pipe and gets one framed response:

```
{
  "protocolVersion": 1,
  "messageType": "clockView",
  "status": "waitingForKsp | noWorld | live | paused | stale",
  "sample": { ...clockSample... } | null,
  "ageSeconds": finite nonnegative number | null,
  "publisherConnected": boolean
}
```

`Host unavailable` is a Manager-only status when the view pipe cannot be reached. The Host retains the last sample for stale display, but clears live-world classification on no-world and epoch transitions. Reception age is measured with a monotonic timer and becomes stale after approximately 3 seconds without a new sample. A paused sample still refreshes. The Manager polls at about 2 Hz, uses the Host's classification, and does not extrapolate UT.

The protocol/Host worker should provide a small public .NET 8 `ClockViewClient` or equivalent API in Core for the Manager, including a bounded async `GetSnapshotAsync` call and the typed response. Tell Sol and the Manager worker its exact namespace/method signature as soon as it compiles. The Manager worker can initially use the wire contract directly, then switch to the Core client before completion.
