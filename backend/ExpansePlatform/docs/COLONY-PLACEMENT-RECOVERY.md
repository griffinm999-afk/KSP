# Same-terms unattempted construction recovery

The construction page exposes **Retry original unattempted placement** for held paid orders. Its capability is disabled unless the selected save's current placement scenario and vessel inventory prove the original request is in RecoveryHold, no assembly was attempted and no marked vessel exists. The paid package hash must still match the installed catalog. An unrelated held/applying effect or an unresolved retry blocks it.

The existing local management submit protocol accepts command kind `retryConstructionPlacement`, target ID equal to the construction order ID, current context and expected revision, and these immutable fields from that order's placement:

- `PlacementOperationId`
- `RequestFingerprint`
- `PayloadHash`

The command receipt and a zero-funds `constructionPlacementRetry` effect retain the authorization before the provider queue is changed. The original hold reason is appended to the journal and retained with its witness hash in the effect's before witness. Payments, consumed materials, completed work, plot, template hash, request payload and placement operation remain unchanged. The adapter serializes its applying state before calling the provider; accepted serialization is save-owned state, not a claim that an independently read-back disk save has occurred.

The placement provider retains `RetryOperationId` in its ordinary saved status. Repeating the same retry ID acknowledges the earlier request without re-arming a later hold or assembly. A cold load reconciles that exact ID; unavailable or mismatched provider identity remains held. The retry effect is acknowledged only from actual provider readback. The construction order resumes through its existing separate placement observer, and operational status still requires actual anchoring and utility commissioning.

This action does not change a quote, enlarge certified bounds, move a committed plot, refund consumed costs or replace a building. The same footprint may fail again. A changed package needs an explicitly reviewed paid-order deployment amendment, which is not implemented here.

Validation includes pure domain accounting/replay/wire tests and detached tests of the exact placement provider and codec. Stubbed scene/marker boundaries cannot qualify native Unity behavior; actual same-save retry, cold continuation and lost-ack acceptance remain open.
