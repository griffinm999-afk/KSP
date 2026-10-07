# Production budget omission contract, v1 additive fields

Clock telemetry uses a bounded 262,144-byte UTF-8 JSON body. This coordinated
publisher/Host/relay/Site change leaves Effects and WOLF frames at 65,536 bytes.
No wire fields or freshness rules change. Overflow still has explicit omission
metadata; rotation is a fallback, not an assurance of current measurement coverage.

## Wire fields

Each vessel's `production` gains:

- `budgetOmittedModuleCount`: integer 0..32, default 0 when absent. Counts captured
  module rows omitted solely for the wire budget. Publisher and Host omissions
  accumulate: count + transmitted module count must be <=32.
- `budgetSelectionSequence`: nullable safe integer 0..9007199254740991, default
  null when absent. Required when omitted count >0; null when count is 0. Uses the
  independent census capture sequence, with a capture-time fallback when census is
  unavailable. This is a render/selection marker, not a native callback receipt.

When omitted count >0, production status is `truncated` and inventory status is
`partial`. Generic truncation, null production, unknown module state, and unavailable
observation with count 0 are not budget omission. Never infer omission from reason
text. A frame can contain no module rows with a positive omission count.

No changed module identity, recipe hash, configured/prepared/achieved/background
vectors, callback captureSequence or original sample timestamps. The metadata does
not prove an omitted module's current activation, recipe, loaded state or rate.

## Actual budget

There is no fixed 24,000-byte aggregate production budget. Both boundaries retain
the 262,144-byte full-frame UTF-8 body limit. The allocator serializes an empty-module
frame with omission metadata, reserves any larger restored metadata, and fills
remaining space with whole rows and exact row byte costs. Priority: delivered or
background observations, prepared potential, active configuration, then other rows.
Vessels share each priority round and rotate their start by capture sequence.

Site must remove the v60 all-production drop at aggregate production JSON >24,000
bytes. Instead validate the full UTF-8 request/frame budget and existing structural
limits (24 vessels, <=32 transmitted+budget-omitted modules/vessel, bounded vectors
and text). A protocol-valid production section inside a <=262,144-byte
frame must remain accepted. Do not replace valid production with empty data merely
because it exceeds the old independent section threshold.

## Consumer transitions

Use session/world/load epoch, vesselId, partId+moduleIndex and recipeHash identity.
Process transmitted rows normally, including explicit invalidations. For missing
rows when budgetOmittedModuleCount >0, retain bounded last-known identity/config
and original timestamps in that same context, mark latest state budget-omitted,
and exclude those rows from current actual/model totals. No state-confirming stub
is supplied. Do not mark the callback receipt permanently invalid merely because
its row was budget omitted. Keep resource labels visible as historical/unknown.

A returning row with the same native captureSequence may regain eligibility only
if it passes the ordinary current context, recipe, enabled/activated, loaded/basis,
timestamp and potential-bound checks. Budget omission alone must not poison that
sequence. Epoch changes, rewind, unload, deactivation, recipe changes, explicit
native invalidation and stale timestamps retain their normal invalidation behavior.
Historical retention cannot undo such an invalidation.

Include budgetSelectionSequence or the census observationSequence in the rendering
change key. observedUt alone is insufficient: independent captures and rotation can
change while UT is paused. This marker never refreshes a receipt's sampleUt.

## Game-time freshness and warp

Native achieved and current BRP observations retain the existing <=10 GAME-second
age checks. Compare with latest game UT on every eligibility/render evaluation,
including heartbeat updates; reject negative age and clear context on rewind.
Use a separate wall-clock feed-disconnected gate. Never extend freshness to the
rotation interval or refresh sampleUt when retaining an omitted row.

The measured full-wrapper reconstruction fits all 15 target module identities in
one frame at 139,331 bytes (90 known rows; the prior live inventory counted 92).
Only captured callbacks are measured; rows restored from saved configuration remain
explicitly unknown. Exact full live payload and rates require post-install capture.

Main-thread capture interval remains three wall seconds. At 100x warp that can be
300 game seconds, already beyond the 10-game-second window. An 18-capture rotation
can span roughly 54 wall seconds even at 1x. Coverage over a rotation does not mean
all 18 facilities are fresh concurrently, and cannot guarantee useful current
achieved rates at high warp. Expired/unobserved rates are unknown, not zero.

## Coordinated acceptance tests

Backend: legacy defaults, malformed metadata rejection, cumulative publisher/Host
counts, whole-row identity/timestamp preservation, paused-UT rotation and bounded
native/Host frames. Consumer: observed receipt -> budget omitted -> same receipt
returns at age 4 game seconds is eligible again; age >10 is not; missing row is
historical/ineligible and keeps resource identity; real invalidations stay invalid;
epoch/rewind clear context; paused capture marker triggers rendering; >24,000-byte
production inside a valid full frame remains accepted. Run both ends before a
replacement package/install; the earlier no-metadata archive is superseded.
