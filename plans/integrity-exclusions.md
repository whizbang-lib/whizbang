# Integrity exclusions (#2588): design note

Status: design, not implemented. Accepted by the maintainer as a feature; this note is the design that comes first.
Companion: the one-off repair of #2573 (migration 599, `WrittenHereOriginRestorationStep`) on the same branch.

## What it is

An operator marks events so the integrity engine stops resolving them: no comparison, no origin-position request, no
hold, no heal, no "unknown" answer. Each mark has a reason, a time and an author, and can be reversed. It is an escape
hatch: it stops the engine from working on, holding or alarming about events a defect made unresolvable, until a fix
ships, and it lets an operator draw a line under pre-1.0 history recorded under older rules. It never repairs anything,
so it is never the answer when a fix is possible (see "Fit with the repair").

## The one rule this design follows

The integrity engine already decides "does this event count" in exactly one place: `__DIGEST_ELIGIBLE__(e)` /
`__INTEGRITY_COUNTED__(e)` in `Migrations/constants.txt`, mirrored in `src/Whizbang.Rules/EligibilitySql.cs` and pinned
by `RuleSemantics.ELIGIBILITY_SQL_VERSION`. About thirty SQL functions and the C# `DigestRuleSql.EligibleEs` /
`CountedEs` fragments read it. Gravity says the exclusion rides that rule, so no path has to remember it: an excluded
row is one the rule no longer counts. The few paths that do not read the rule today are fixed to read it, and a guard
test fails any new integrity path that bypasses it.

## 1. Cross-service agreement

**The problem.** Both sides fold the same events by identity (`_wh_digest_lane(event_id, n)` depends on the id only).
If the receiver removes an event from its folds and the origin does not, the bucket mismatches forever. A per-event mark
is not a type rule, so `RuleFingerprint` cannot catch the difference.

**Scope decision: exclusions apply to received history only.** An exclusion is made on the receiving side, about events
recorded under an origin (or missing from it). A service never excludes its own events from what it answers: its
consumers compare against it, and an exclusion there would turn into a mismatch at every consumer, which is the
failure the requirement forbids. Marking a local-lane row is refused with a reason.

**Agreement without changing the comparison wire.** Because the receiver holds every excluded id, it can remove the
event from both sides of every comparison itself:

- **Its own side:** the row carries a new flag, `EventFlags.IntegrityExcluded = 256`, and the eligibility rule tests
  it. Every local fold (stream digests, sealed epochs, the held fold, checkpoint counts) drops the event through the
  shared rule.
- **The origin's side:** when the origin holds the event, the receiver subtracts it from the origin's answer. It knows
  whether the origin holds it, and where, because the mark asks the origin once through the existing exchange,
  `RequestOriginPositions` / `OriginPositionsAnswer`: `positioned` (the origin holds it at commit sequence P),
  `archived` (held in the archive at P), `unstamped` (ask again later), or `unknown` (the origin never wrote it or no
  longer holds it). The answer is stored on the mark. The receiver keeps a maintained per-bucket adjustment (the XOR of
  the excluded events' lanes and their count, keyed by origin, tenant, type, stream and origin epoch) and applies it to
  every origin answer and checkpoint count before comparing: stream digests and type digests (subtract the bucket's
  adjustment), windowed answers (subtract the adjustments whose origin position lies in the window), checkpoint counts
  (subtract the count in the checkpoint range).
- **`unknown`**: the origin's side has nothing to subtract. This is exactly the #2573 shape.
- **`unstamped` or not yet asked**: the mark is pending. A bucket holding a pending mark is treated as undecided
  (compared, never repaired), the state the engine already has for held events (`HeldWindowVerdict.Undecided`), until
  the next ask settles it.

**The one protocol change.** `OriginPosition` gains optional `TenantScope`, `EventType` and `StreamId`, because an
event excluded while missing locally (a deficit nobody can heal) has no local row to read them from. The fields are
additive; an older origin leaves them out, and such a mark stays pending (undecided, never a mismatch) until the origin
upgrades. No new message type, no change to `RequestIntegrityManifest`, `IntegrityManifest`, `IntegrityCheckpoint` or
the redelivery messages.

**Why not the alternatives.**

- *The exclusion travels with each comparison request* (the receiver lists excluded ids, the origin subtracts them):
  unbounded requests, every origin path changes, and the origin's answer cache stops being shared across consumers.
- *The origin is told and records it too:* an origin's folds are shared by all its consumers, so an exclusion one
  consumer needs would have to be stored per consumer at the origin. That is more state in more places, and two
  services must stay in step for an operator action on one of them.

The origin-side seal (`wh_integrity_consumer_seals`) needs no change: the receiver's verdict, made after the adjustment,
is what it reports.

## 2. Integrity paths that must honor it

Through the eligibility rule, once it tests the flag (no per-path change):

- Stream digests: `_wh_fold_stream_digests` (461) callers, `_emit_event_store_chain` (573),
  `_emit_event_store_chain_for_inbox` (574), `wh_append_event` (537), `wh_seed_stream_digests_batch`,
  `wh_compute_stream_digests_window`, `_wh_heal_stream_digests_statement` (461), C# `ComputeStreamDigestsAsync`,
  `ComputeStreamDigestsForChunkAsync` (`EFCoreWorkCoordinator`).
- Sealed epochs: `_wh_epoch_buckets`, `compute_type_digests_epoch`, `compute_type_digests_epoch_window` (461),
  `_wh_fold_digest_epoch` and the 592 touch triggers.
- The held fold: `_wh_track_unsequenced_digest`, `wh_unsequenced_received_type_digests`,
  `wh_rebuild_unsequenced_digests` (452).
- Checkpoint counts: `AdvanceIntegrityCheckpointAsync`, `CountReceivedFromOriginAsync` (`CountedEs`).
- Cuts and archiving waits: `_wh_cut_awaits_seal` (560), `_wh_carry_cut` / `_wh_uncarry_restored` (461). An excluded
  event never holds a cut.
- Redelivery reconciles and adoption: `wh_reconcile_redelivered_bundle*_counting_satisfied` (594),
  `wh_adopt_redelivered_origin`, `wh_integrity_correct_origins` (453), `wh_integrity_reattribute_origins` (594).

Paths that do not read the rule today and must be changed:

- `wh_origin_position_ask` (561): asks for every held row. It skips excluded rows (they leave the held set through the
  flag, and the ask reads the held index; the index predicate gains the flag test).
- `wh_origin_positions_of` (561, answering side): unchanged; it answers for the origin's own events, which are never
  excluded there.
- `SelectRedeliveryEventsAsync` (`EFCoreWorkCoordinator`, answering side): unchanged for the same reason.
- `_wh_touch_sealed_epochs_on_update` (592): fires on `origin_service_id` and `origin_commit_sequence` only. The mark
  function refolds what it changes itself, as `_wh_flag_at_most_once_occurrences` (453) does: capture eligibility,
  unfold the stream buckets, drop emptied buckets, `_wh_refold_epochs_covering` per lane, `_wh_bump_origin_generation()`
  once. That function is the template.
- The comparisons, where the adjustment is applied (C#): `IntegrityManifestReceptor._compareTypeLevelAsync`,
  `_healProvenTypeLanesAsync`, `_followResumeCursorAsync` and the stream drill-down
  (`Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs`); `IntegrityCheckpointReceptor.HandleAsync`
  (`_heldCount`, `_unsequencedAsync`); the audit driver `IntegrityAuditWorker.RunAuditOnceAsync` /
  `RunCheckOnceAsync` / `RunSweepOnceAsync` and `RecheckDigestEpochsAsync`.
- Heal and ledger: `wh_integrity_ledger` rows of an excluded bucket are re-armed or removed when the mark lands
  (`CoordinatorIntegrityRepairLedger`; precedent `IntegrityForgetNonOwnerLedgerRowsAsync`); the repair drain
  (`RepairDrainWorker` → `wh_integrity_claim_repair_drain`), the recheck (`IntegrityLedgerRecheckWorker`,
  `wh_integrity_claim_ledger_recheck`) and the epoch heal queue (`wh_heal_queued_digest_epochs`, 593) see the bucket
  match after the adjustment and clear it the ordinary way. No separate "skip" branch.
- Unknown answers: `wh_origin_position_unknown` rows of an excluded event are removed by the mark (the event is
  resolved by decision), and restored by unmark only if the origin still answers unknown on the next ask.

To make the "one rule" enforceable, the comparison side gets one entry point, `IntegrityExclusionAdjustment.Apply(...)`,
called by the receptors' shared compare helper rather than by each branch, and a source guard test (like
`IntegrityOwnershipRuleSourceGuardTests`) fails any comparison path that compares an origin answer without it.

## 3. Storage

```sql
-- The marks: one row per excluded event id, received history only.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_exclusions (
  event_id               UUID        NOT NULL PRIMARY KEY,
  origin_service_id      UUID        NOT NULL,
  tenant_scope           TEXT        NOT NULL,
  event_type             TEXT        NOT NULL,
  stream_id              UUID        NOT NULL,
  origin_state           SMALLINT    NOT NULL,   -- 0 pending, 1 held at the origin, 2 unknown to the origin
  origin_commit_sequence BIGINT,                 -- where the origin holds it (state 1)
  local_present          BOOLEAN     NOT NULL,   -- a wh_event_store row carries the flag
  reason                 TEXT        NOT NULL,
  marked_by              TEXT        NOT NULL,
  marked_at              TIMESTAMPTZ NOT NULL,
  selection_id           UUID                    -- the selection that marked it, for audit and bulk unmark
);
CREATE INDEX IF NOT EXISTS idx_integrity_exclusions_pending
  ON __SCHEMA__.wh_integrity_exclusions (origin_service_id) WHERE origin_state = 0;

-- The maintained adjustment: what the origin's answer holds that this side excluded. Only state 1 rows contribute.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_exclusion_adjustments (
  origin_service_id UUID   NOT NULL,
  tenant_scope      TEXT   NOT NULL,
  event_type        TEXT   NOT NULL,
  stream_id         UUID   NOT NULL,
  origin_epoch      BIGINT NOT NULL,   -- origin_commit_sequence / the origin lane's epoch width
  digest_lo BIGINT NOT NULL, digest_hi BIGINT NOT NULL, id_lo BIGINT NOT NULL, id_hi BIGINT NOT NULL,
  event_count       INTEGER NOT NULL,
  PRIMARY KEY (origin_service_id, tenant_scope, event_type, stream_id, origin_epoch)
);

-- The audit log: every mark and unmark, append-only, its own retention (OperationalTableRegistry, _rolling).
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_exclusion_log (...);  -- action, selection, counts, reason, by, at
```

**Hot paths pay nothing.**

- **Local folds:** the flag sits on the row every fold already reads, so there is no join. A store with no exclusions
  pays one more bit test inside an expression already evaluated.
- **Comparisons:** the receptor reads the adjustment only when the origin has any. A per-origin count is kept in
  `wh_integrity_known_origins` (or a one-row-per-origin `exclusion_generation`), already read for the compare. When it
  is zero the adjustment query is never issued. When it is not, the read is one primary-key range per compared bucket
  set, the same shape as the held fold.
- **Marking:** batched, idle-gated, never on the receive path.

**Versioning.** Testing the flag in the eligibility rule changes the rule text, so `ELIGIBILITY_SQL_VERSION` goes to 2,
and the change rides the existing mixed-version pair halt: a pair at mixed versions halts rather than compares. Both
sides must upgrade for comparisons to resume. That is safe (no false mismatch), but it is a fleet-wide pause during a
rolling upgrade; see decision D2.

## 4. API, operator entry point, audit, logs, metrics

Public API (in `Whizbang.Core`, implemented by the Postgres driver; Dapper and EF Core alike):

```csharp
public interface IIntegrityExclusions {
  Task<IntegrityExclusionResult> ExcludeAsync(IntegrityExclusionRequest request, CancellationToken ct = default);
  Task<IntegrityExclusionResult> UnexcludeAsync(IntegrityUnexclusionRequest request, CancellationToken ct = default);
  Task<IntegrityExclusionSummary> SummarizeAsync(Guid? originServiceId = null, CancellationToken ct = default);
}

public sealed record IntegrityExclusionRequest {
  public required string Reason { get; init; }          // required, non-blank
  public required string RequestedBy { get; init; }
  public IReadOnlyList<Guid>? EventIds { get; init; }   // by id, or by selection, never both
  public IntegrityExclusionSelection? Selection { get; init; }
  public bool DryRun { get; init; }                     // counts what would be marked, marks nothing
}

public sealed record IntegrityExclusionSelection {
  public Guid? OriginServiceId { get; init; }
  public IReadOnlyList<string>? EventTypes { get; init; }
  public DateTimeOffset? From { get; init; }            // created_at range
  public DateTimeOffset? Until { get; init; }
  public IntegrityUnresolved? Unresolved { get; init; } // section 5
}
```

A selection is recorded once (a selection id, its predicate as JSON, reason, author) and applied by a maintenance step
in bounded batches. The call returns at once with the selection id and, for a dry run, the counts. Ids are applied
the same way above a small inline limit.

**Operator entry point: fits two existing patterns.**

- **HTTP**, opt-in, beside the existing operator groups in `Whizbang.Hosting.AspNet`
  (`DeadLetterOperatorEndpoints`, `StreamRedeliveryEndpoints`): `MapWhizbangIntegrityExclusionEndpoints()` maps
  `POST /whizbang/integrity/exclusions` (mark, `dryRun` supported), `POST /whizbang/integrity/exclusions/unmark`, and
  `GET /whizbang/integrity/exclusions` (the summary). It uses the same authorization policy hook as the DLQ endpoints.
- **The work:** a request table polled by an `IMaintenanceStep` (`IntegrityExclusionStep`), as
  `ArchiveRestoreRequestStep` does, under the maintainer duty, idle-gated and bounded. It also re-asks pending marks
  through the existing position-fill exchange (`OriginPositionFillStep` asks for pending exclusion ids alongside held
  rows).

A configuration-declared form (like `Whizbang:EventStore:RetiredTypes`) is deliberately not offered: an exclusion is a
dated operator decision with a reason, not standing configuration, and a key left in configuration would keep
re-marking after an unmark.

**Unmark** clears the flag, refolds through the same helper, removes the adjustment rows and the marks, and re-arms the
ledger buckets, so the engine resolves the events again from scratch. It is logged and audited like a mark.

**Audit and logging.** Every mark and unmark writes `wh_integrity_exclusion_log` (who, why, selection, counts,
per-origin), and logs a Warning per origin and batch. Excluding is also emitted as a framework system event if the
host audits system events (decision D4).

**Metrics.** `whizbang.stream_integrity.excluded_events` (gauge per origin, from the per-origin count, O(origins)),
`whizbang.stream_integrity.exclusions_marked` and `..._unmarked` (passive counters, tagged `origin`),
`whizbang.stream_integrity.exclusions_pending` (gauge: marks whose origin position is not settled).

## 5. The past: "everything currently unresolved older than X"

`IntegrityUnresolved` is a flags enum over what the engine already knows, each a bounded indexed selection:

| Flag | Source | Selection |
| --- | --- | --- |
| `UnknownAnswers` | `wh_origin_position_unknown` | rows recorded before X |
| `Undecided` | the held set, `idx_event_store_unsequenced_received` (452) | held rows of the origin created before X |
| `Unhealed` | `wh_integrity_ledger` | buckets with `unhealable_at` or `exhausted_at` set, first seen before X |

The first two are event ids this service stores, so they mark like any id. `Unhealed` buckets are deficits: the
missing events are not stored here. For those the step asks the origin for the bucket's ids through the existing
redelivery path in state-only, ids-only mode (`RequestRedeliveryCommand` with `StateOnly`, bounded by `MaxEvents`),
then marks the ids it lacks as `local_present = false` with the origin's positions. That needs the origin to list the
ids without shipping bodies (decision D3). Until then, `Unhealed` marks only the bucket's locally present events and
leaves the deficit counted.

The operator story: `POST /whizbang/integrity/exclusions` with `{ "selection": { "unresolved": "All", "until":
"2026-11-01T00:00:00Z" }, "reason": "pre-1.0 history recorded under older rules", "dryRun": true }`, read the counts,
then repeat without `dryRun`.

## 6. Fit with the #2573 repair

- **Repaired properly by 599** (`WrittenHereOriginRestorationStep`): every row recorded under another origin whose first
  hop names this service, wherever the hop is kept (row, route, staged form). That is the reaction and return shapes on
  any store that kept the full form, staged it or distilled it into a route that has not aged out. These go back to the
  local lane with their digests, seals, held counts, unknown answers and ledger buckets. No exclusion is needed for them.
- **Left for the exclusion:** the rows 599 counts as ambiguous. These are unpositioned rows whose origin disowned them,
  or that name no service anywhere (Slim form, or a route aged out). Nothing in the store proves who wrote them, so 599
  must not move them, and they keep their origin's windows undecided. The exclusion is the right tool for them, as
  `unresolved: UnknownAnswers | Undecided` with an `until` before the 594 deployment. Their origin answers unknown, so
  their marks settle at state 2 with no adjustment.
- **Not for the exclusion:** anything 594 or 599 can fix. The docs page says so, and the API refuses to mark a row 599
  would restore while the restoration walk has not finished (it reads the walk's cursor), so a fix is never fenced off
  by mistake.

## 7. Tests and estimate

Strict red/green, Postgres integration tests against the real functions, plus unit tests for the C# compare helper.

1. Eligibility: a flagged row leaves every fold through the rule. Parameterized over the ~30 functions' entry points
   (append, inbox emit, seed, window, heal, epoch close), and checkpoint counts. The rule's golden hash moves to v2.
2. Mark and unmark refold exactly what they change, and unmark is the exact inverse (digests, epochs, generation,
   held fold, unknown table, ledger).
3. Cross-service agreement, two-service tests in one database (two schemas), for each origin state: excluded and held
   at the origin, excluded and unknown, excluded while missing locally, pending. Each runs type-level, windowed,
   stream drill-down and checkpoint compares and asserts *no* divergence, *no* repair request, *no* position ask, and
   the bucket's seal advances. Positive controls: the same scenario without the mark mismatches.
4. Rolling upgrade: the v1/v2 rule fingerprint pair halts and resumes. An old origin without the new `OriginPosition`
   fields leaves the mark pending and its bucket undecided, never mismatched.
5. Hot path: with no exclusions, the compare issues no adjustment read (statement count), and an `auto_explain` plan
   guard shows no join on the fold paths.
6. Selections: by ids, by type, origin and time range, and by `Unresolved` with `until`; dry run marks nothing; bounded
   batches resume from the cursor; refusals (local lane, blank reason, both ids and selection, a row 599 will restore).
7. API, HTTP endpoints (auth hook), the maintenance step registration on both drivers, logs (Warning per origin and
   batch) and the four metrics.
8. Guard: a source test that fails a comparison path comparing an origin answer without the adjustment helper.

**Estimate:** about 8 to 10 engineering days. Rule and flag plumbing with mark/unmark SQL: 2. Adjustment table and
the compare-side helper across the receptors: 3. Selections, step, HTTP and API: 2. Cross-service and upgrade tests:
2. Docs page: 0.5. The deficit (`Unhealed`) ids listing is another 2 days if decision D3 is yes.

## Decisions for the maintainer

- **D1. Received history only.** Exclusions are refused for this service's own events, so an origin never changes what
  it answers. Confirm.
- **D2. Rule version bump.** Testing the flag in the eligibility rule bumps `ELIGIBILITY_SQL_VERSION`, which halts
  every pair at mixed versions during a rolling upgrade until both sides run the new rule. The alternative is to ship
  the flag test dark (the rule reads the flag, but nothing sets it until every service of the fleet reports v2), which
  avoids the pause at the cost of a two-release rollout.
- **D3. Deficit buckets.** Should `Unhealed` reach events missing locally (needs an ids-only redelivery answer from the
  origin), or should the first version mark only stored events and leave deficits counted?
- **D4. System event.** Should a mark or unmark also be emitted as an audited framework system event, beside the log
  table?
