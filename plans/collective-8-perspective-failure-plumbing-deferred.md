# Deferred: §8 — perspective/collective failure & dead-letter plumbing (D6)

**Status**: DEFERRED (decided 2026-07-01). Bug precisely identified below; fix needs a dedicated session
with work-coordinator context.
**Context**: Whizbang 0.795 collective-event apply hardening. §8 of the 0.795 plan ("Failure / dead-letter
plumbing"). Root cause D6 from the investigation.
**Why deferred**: the fix touches the **core perspective failure/completion path shared by every perspective
in every service** (`PerspectiveWorker` + the work-coordinator failure functions), and the mismatch is deeper
than a field rename (see below), so it needs its own focused session + integration coverage rather than a
rushed end-of-session change to hot, shared machinery.
**Not spiral-critical**: §8 governs what happens when an apply *fails*. The shipped D0/D1/§3/§4/§5a/§7 fix
makes applies *succeed* (scope-correct, bounded predicate UPDATE, server timeout, keyset batching, exclusive
per-scope lock, expression-indexed), so the broken failure path is far less likely to be exercised. §8 is
defense-in-depth for genuine poison events (real bugs), not the production convoy.

---

## The precise bug (a triple mismatch across the failure chain)

Failure flows: `PerspectiveWorker._flushPendingCompletionsToChannelsAsync` → `_failureChannel` →
`FailureFlushWorker` → SQL `process_work_failures` (mig 029, category dispatch) → SQL
`process_perspective_event_failures` (mig 019) for `WorkCategory.PerspectiveEvent`.

1. **Wrong identifier (value semantics).** `PerspectiveWorker.cs` (~line 1158) enqueues:
   ```csharp
   new MessageFailure { MessageId = f.LastEventId, CompletedStatus = None, Error = …, Reason = Unknown }
   ```
   `f.LastEventId` is a **domain event id**, but `process_perspective_event_failures` matches
   `WHERE pe.event_work_id = <id>`. A `wh_perspective_events` row is keyed by **`event_work_id`** (unique per
   (perspective, event) work item — one event fans out to many work rows), *not* the event id. So even the
   right field name would carry the wrong value. Contrast the **completion** path, which correctly uses
   `EnqueueEventWorkIdAsync(ec.EventWorkId)` — failures and completions are asymmetric.
2. **Wrong field name (identifier).** `MessageFailure` (in `IWorkCoordinator.cs`) has `MessageId`,
   `CompletedStatus`, `Error`, `Reason`. The mig-019 function reads `elem->>'EventWorkId'` — a field the
   payload does not have → NULL → `WHERE event_work_id = NULL` matches nothing.
3. **Wrong field name (reason).** mig-019 reads `elem->>'FailureReason'`; the payload key is `Reason`.

Net effect: the perspective-failure UPDATE matches **zero rows**. The Failed flag, `error`,
`scheduled_for` backoff, and lease release are never applied on a perspective/collective apply failure.

## What still works (so you scope the blast radius correctly)

- **Attempt counting is NOT done here.** mig-019's own comment (and mig-018) say
  `claim_orphaned_perspective_events` is the *sole* source of attempt counting. So attempts still increment on
  orphan reclaim; §8's breakage is specifically the *failure-reporting* side (Failed flag / error /
  scheduled_for backoff / immediate lease release).
- **Dead-lettering** past `MaxPerspectiveEventAttempts` exists in `PerspectiveWorker` (~line 1311-1332,
  `dead-lettered perspective event {EventWorkId} … attempts > max`) driven off `raw.Attempts`. So poison
  events still eventually dead-letter via the attempts path — §8 does not leave poison events looping forever
  *if* attempt counting via claim_orphaned works. (Verify this in the dedicated session — it's the safety
  backstop that makes §8 non-urgent.)

## What a correct fix entails

1. **Carry the work id, not the event id.** Either (a) add an `EventWorkId` (or a `WorkIds` list) to the
   perspective-category failure payload and enqueue the actual `event_work_id`(s) the failure covers, or (b)
   introduce a perspective-specific failure DTO. Mind the **fan-out granularity**: one failed envelope can
   map to multiple `event_work_id`s (per perspective); the current single-`LastEventId` shape loses that. The
   completion path already enqueues per-`EventWorkId` — mirror it.
2. **Fix mig-019 field names** to match the payload actually sent (`MessageId`/`EventWorkId` as chosen above,
   and `Reason` not `FailureReason`) via a NEW migration (never edit a shipped one). Keep the
   `status | Failed(32768)`, `error`, `scheduled_for` exponential backoff, `instance_id/lease_expiry = NULL`
   semantics.
3. **Sink completion / cursor.** Confirm the `__collective__` sink reports per-`event_work_id` completion so
   successful sink events delete their `wh_perspective_events` work rows and advance the cursor past a poison
   event (the plan flagged the sink "never deletes its work rows nor advances the cursor past a poison
   event"). Verify against the completion path.
4. **DLQ.** Confirm `_deadLetterStore` / `_generationProvider` reach the worker and that
   `FilterDeadLetteredAsync` / `> MaxPerspectiveEventAttempts` (default 10) fires as the poison backstop —
   valid-but-heavy events must succeed via D0–§7, only genuinely-poison events dead-letter.

## Tests the dedicated session must add (real Postgres)

- A failing perspective apply increments `attempts` (via claim_orphaned) AND — after the §8 fix — sets the
  Failed flag + `error` + `scheduled_for` backoff on the correct `event_work_id` row (currently a no-op).
- After `> MaxPerspectiveEventAttempts`, the event dead-letters and stops being re-leased.
- A successful sink event deletes its work row and advances the cursor past it (no re-processing).

## Risk / why a dedicated session

`PerspectiveWorker` is ~3000 lines on the hot path for **every** projection in **every** service; the
failure/completion strategy, the work-coordinator channels, and the SQL failure functions are tightly
coupled. A wrong change to the failure-reporting granularity or the cursor/DLQ interaction could silently
break retry/backoff for all perspectives. This is the same class of risk that deferred the receptor-chaos
worker scenarios (see `plans/receptor-chaos-scenarios-deferred.md`).

## Pointers

- Enqueue site: `src/Whizbang.Core/Workers/PerspectiveWorker.cs`
  `_flushPendingCompletionsToChannelsAsync` (~line 1156-1164); completion path
  `EnqueueEventWorkIdAsync` (~1166); dead-letter path (~1311-1332).
- Payload type: `src/Whizbang.Core/Messaging/IWorkCoordinator.cs` `record MessageFailure` (~line 1238).
- SQL: `src/Whizbang.Data.Postgres/Migrations/019_ProcessPerspectiveEventFailures.sql`,
  `.../029_ProcessWorkBatch.sql` (`process_work_failures` dispatch ~line 1146),
  `.../018_*` and `claim_orphaned_perspective_events` (attempt-counting source).
