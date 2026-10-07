# Colony settlement journal — observer-only contract

This development package is not installed or live-qualified. Pipe envelope
Version remains 1; the independent saved journal container is Version 2.

The baseline is 100000 funds and 0 science. Old receipts, cargo values, Career
balance and Career science are not imported. The journal observes existing
accepted payment pipelines; it never sets funds/science and never vetoes a valid
payment because of capacity, capture, encoding, storage or observer availability.
There is no verified science event source in this scope.

## Existing read endpoint

Use the protected current-user colony management pipe derived with
ColonyManagementWire.PipeForInstallation(absoluteKspRoot), or its existing
explicit override. Framing is four little-endian length bytes followed by UTF-8
JSON. Property names are PascalCase. SettlementJournalClient.ReadAsync in
Expanse.Clock.Core is a read-only client; no listener/port or credential is added.

```json
{"Version":1,"RequestId":"a-new-GUID","Kind":"settlements","Settlements":{"AfterCursor":null,"ThroughCursor":null,"Limit":100}}
```

Limit is 1–250, and Command must be absent. The response envelope Outcome is
settlements. Its Settlements page has:

| Field | Meaning |
| --- | --- |
| Status, Reason | ok, gap or unavailable and explanation |
| AfterCursor | Exact request echo |
| Head, ReadThrough | Current journal head and pinned range ceiling |
| CoveredThrough, NextCursor, HasMore | Page position and whether pinned range has another page |
| Complete, Gaps | Coverage certification and explicit problems |
| WorldId, BranchId, ParentBranchId | Current page authority and fork lineage |
| Baseline | Funds=100000, Science=0, GameUt and Heads captured on one Unity-thread turn |
| BaselineCursor | Exact current-branch sequence-0 journal cursor for that baseline |
| RetainedFromCursor, RetainedFromHash | Boundary immediately before the oldest retained event |
| CoverageGaps | Persisted lost ranges or explicitly unknown ranges |
| GameUt, Heads | Current coherent read UT and both source heads, sampled on one Unity-thread turn |
| Events | Ordered retained settlements |

Heads contains RecoverySequence, RecoveryHash, ColonySequence, ColonyRevision,
ColonyHash. Native source heads are independent from journal cursors. BaselineCursor
is an opaque branch-guid:0:sha256 cursor, where the hash binds the baseline UT,
amounts and both native source heads. It is not merely a native receipt sequence.

After the first page, send NextCursor as AfterCursor and keep the original
ReadThrough as ThroughCursor until HasMore=false. New accepted settlements can
advance Head without changing that pinned ceiling. Complete certifies coverage,
while HasMore describes pagination. **A requested range crossing lost coverage makes Status=gap and Complete=false.**
A consumer with an acknowledged, matching-hash cursor at or after
RetainedFromCursor can receive complete retained suffix pages even while
CoverageGaps reports the pruned prefix. A lagging or invalid cursor must pause
balance/cursor advancement; no missing events or new baseline may be invented.
Unknown-range observer loss remains incomplete for all requests until an explicit
new baseline is authorized.

Cursors are opaque branch-guid:journal-sequence:sha256 values. Invalid, changed,
future, missing-prefix and other-branch cursors report invalidCursor, cursorGap,
rollback, retentionGap or branchChanged. Operational receipt compaction does not
change journal sequence numbering.

## Events, exact amounts and attribution

Fields are Source (recovery/colony), EventId, Sequence, Cursor, WorldId, BranchId,
OriginBranchId, OperationId, SettledUt, Kind, FundsDelta, ScienceDelta,
FundsDeltaExact, ScienceDeltaExact, nullable ColonyId, RouteId, RouteVersion,
ShipmentId, VesselId, SourceDepotId, EvidenceHash, PreviousHash and Hash.

Events.Sequence is **journal-wide across both sources**, starting at 1 after the
sequence-0 baseline. It continues monotonically when the tail evicts or capture
fails, and a fork continues the inherited head sequence. It is neither a native
command sequence nor a retained-array index. Page.BranchId is the current branch;
Events.BranchId and Events.OriginBranchId identify the event's branch of origin.
Inherited events keep origin and EventId after a fork, while Cursor is reissued
under the current page branch. Hashes detect alteration/continuity, not signatures.

Use FundsDeltaExact and ScienceDeltaExact as signed integer strings/BigInt in
JavaScript. Numeric counterparts are Int64 JSON values. Science deltas are zero.
RecoverySale and colony constructionEscrow, constructionRefund, importPurchase,
passengerFare, logisticsSetup and wolfPurchase are the accepted kinds. Rejected,
held, faulted, reserved, cargo dispatch/arrival/physical movement and market-value
observations produce no monetary event. Only original qualified acceptance may
trigger the observer.

Recovery captures the immutable source shipment, route/version and depot before
acceptance removes that shipment. Funds evidence is the exact accepted readback.
Vessel attribution uses the registered anchor. Colony attribution uses the depot
owner or one unique registered colony facility matching that vessel; unresolved
or ambiguous vessel/colony fields remain null. No name, location or Site ID is
substituted for authority. Colony events retain the native colony ID and funds
effect witness; importPurchase also retains its shipment ID. Other unavailable
attribution fields stay null.

Site mapping must explicitly associate native world
3bba0a04-1978-47e4-b12f-11618c19b2a9 and native Ilus colony
be28c55a-a190-4508-beaf-b964cd071e90 with its Site colony ID. They are consumer
configuration context, not hard-coded event attribution.

## Bounded retention and failure behavior

The save owns a tail of at most 2048 events and 8 MiB. On eviction it persists one
coalesced missing-prefix marker and the exact RetainedFromSequence/Hash boundary.
CoverageGaps entries contain FromSequence, ThroughSequence, BeforeHash, AfterHash,
Reason, ThroughUt and RangeKnown. Known loss is an inclusive missing-event range,
with the hash before that range and the hash at its end. RangeKnown=false means
journal positions could not be established; its zero/default positions must not
be interpreted as known cursors. No archived history subsystem is introduced.

Capacity eviction retains later exact events and advances journal-wide head
sequence. A capture/serialization failure after an accepted payment records a
missing position, coalesces the older tail and that position into an explicit
lost-prefix range, then allows future observations to append. This conservative
coalescing also discards older otherwise valid evidence, visibly. It bounds both
events and loss metadata; there is no silent truncation or completeness promise.

If the journal cannot be used at all (missing runtime, corrupted payload, absent
source heads), gameplay acceptance still succeeds. RecoveryCapsuleModule persists
an independent SETTLEMENT_OBSERVER_LOSS marker. ColonyRuntime also saves a
bounded SETTLEMENT_OBSERVER_UNKNOWN_GAP marker, so colony observation loss
persists even if the recovery runtime is unavailable. Reads return gap/incomplete with
an unknown-range marker; that failure cannot disappear on ordinary reload. Corrupt
journal nodes are preserved. Storage/fork-witness failures are also explicit
coverage uncertainty, never a new gameplay payment authorization rule.

The observer pre-captures attribution inside an exception boundary, then appends
only after exact readback and accepted gameplay state. It appends to the then-current
journal, so nested callbacks cannot overwrite another observation's prepared
snapshot. The original financial validations, fences, failure/retry behavior and
payment callbacks are unchanged; the prior observer callback guard was removed.
EconomicRecoveryEffects.cs is byte-identical to the pre-journal source SHA256
744744EF0D96D4C8CE8A98EC1A20EC2A6A4EF00981D0FEDA4AA1375324D6AEDA.

SETTLEMENT_JOURNAL is a separate ColonyRuntime save node; existing colony state
and recovery capsule encoding are untouched. The local bounded fork witness at
GameData/ExpanseWorldBridge/PluginData/SettlementJournal/<world-guid>.json never
restores game authority. Identical ordinary reload preserves branch identity;
older/divergent saved journal/source witnesses create a child branch. Consumers
retain their cursors, and the fork witness must be preserved for detection across
process restarts. A save predating the baseline or mismatching saved source heads
reports a gap; it does not replay old income or block valid payments.

## Precision invariants and validation

Current captured delta types are Int64: EconomicRecoveryIntent.FundsDelta,
FundsSuccessWitness.IntendedDeltaFunds, ColonyEffect.FundsDelta and
ColonyJournalEntry.FundsDelta. Ore recovery requires whole Ore units and checked
integer compensation. Fractional account balances are allowed, but the original
readback requires after-before == intended integer delta and observed == intended
after, including an exact representability check for recovery. The journal formats
that validated delta directly; it never rounds account balances to infer income.
ColonyRuntime's existing floored balance witnesses do not change the original
integer effect delta or its preceding double-delta/post-callback equality checks.

The shipping-method economic test validates 125.5 to 500125.5 for exactly 500000
income. Rounded large-balance evidence rejects. Journal wire cases reject
12345.5, -0.5 and exponent notation in the integer delta field; Int64 parsing uses
long.Parse/AllowLeadingSign, never a double-to-long truncation. Flooring an
otherwise valid fractional readback also rejects. A future fractional settlement
source needs a versioned exact decimal/rational contract; this scope has none.

Tests cover retained-tail exhaustion, persisted hash/range gaps, monotonic merged
sequence, explicit baseline cursor, inherited origin/current branch distinction,
capture failure and qualified shipping-method payments with exhausted/failed
observation. The full suite is refreshed with the package test result. This is
compile/unit/regression evidence; native save/load behavior remains unqualified
until a separately authorized deployment and live test. The package is uninstalled.

## Existing relay extension — proposal only

Add a read-only collector step to the already authorized outbound Site sync loop:
read a pinned range from this existing pipe; send baseline, BaselineCursor,
coherent Heads/GameUt, retained boundary/gaps and exact-string event fields using
its current authenticated ingest destination. Preserve all gap/branch statuses,
deduplicate EventId and acknowledge ingest before advancing a consumer cursor.
Any requested-range gap or unknown-range loss pauses Site balances; an already
acknowledged pruned prefix does not prevent validated suffix ingestion. Reuse current credentials/destination; add no port,
listener, credential or command/mutation access. The relay owner is locating its
source separately. No transport change or broader access is implemented here.
