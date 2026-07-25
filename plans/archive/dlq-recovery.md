# Dead-Letter Recovery — design doc

> Status: **ready for implementation** on `release/v0.494.0-alpha.1` after #227 lands.
>
> **Resolved answers** (as of 2026-06-02):
> - Q 1: Design doc first (this), implement on a separate release branch
> - Q 2: Separate `wh_dead_letters` table
> - Q 3: Yes — generation-based replay on each new deploy
> - Q 4: Hybrid Option E + Option C — default `IDeadLetterRecoveryPolicy` impl reads `[StreamRecovery]` attribute (discoverable); application can replace the whole policy for full control (escape hatch)
> - Q 5: Configurable Subscribe / Poll mode for Flow A; **default = Subscribe (real-time)**

## Goal

Systematic recovery of dead-lettered messages across two distinct layers:

- **Flow A — Transport DLQ** (broker side: Azure SB `$DeadLetterQueue`, RabbitMQ DLX). Recovery is **aggressive and automatic**: the source service's `wh_event_store` is durable, so a message rotting in the broker's DLQ is operational noise, not data risk. Dumb-but-safe re-publish loop.
- **Flow B — Whizbang internal DLQ** (`wh_outbox` / `wh_inbox` / `wh_perspective_events` rows that exceeded `MaxAttempts`). Recovery is **policy-driven**: error classification, idle-cadence triggers, optional stream-awareness, generation-based replay. This is the bulk of this doc.

## Why two flows

The architectural distinction matters because the **data-loss-risk profile is fundamentally different**:

| | Transport DLQ | Whizbang internal DLQ |
|---|---|---|
| Is the data still durable? | Yes — `wh_event_store` on the source service is the truth | The row IS the data |
| Cost of premature retry | None (idempotency at the receiver handles double-delivery) | Could re-trigger the original failure (validation, etc.) |
| Should we ever ask a human? | Only after N auto-recovery rounds fail | Often, depending on `failure_reason` |
| Right cadence | Tight (every few minutes) | Idle-triggered + periodic backstop |
| Stream-ordering concerns | No (broker preserves order within DLQ; re-publish honors session id) | Yes (FIFO matters; see § Stream-aware recovery) |

Conflating them produces either over-cautious transport recovery (messages rot for hours) or over-aggressive internal recovery (validation failures retry forever).

---

## Flow A — Transport DLQ recovery

### Surface

New per-transport background service:

- `AzureServiceBusDeadLetterRecoveryWorker` — subscribes (or polls) the `$DeadLetterQueue` subqueue for every Whizbang-managed subscription on the namespace
- `RabbitMqDeadLetterRecoveryWorker` — consumes the DLX queue Whizbang creates per transport binding

Per recovered message:

1. Read the dead-lettered message + envelope
2. Re-publish to the main subscription/queue (same body, same `correlationId`, same `sessionId`)
3. Complete on the DLQ side only after the re-publish succeeds
4. If the same `message_id` lands in the DLQ ≥ `TransportDlqMaxRecoveryAttempts` (default `3`), promote to **Whizbang internal DLQ** (i.e., insert a row into `wh_dead_letters` with `source = TransportDlqEscalation`)

### Observability

- `whizbang.transport.dlq.recovered{transport}` Counter — successful re-publishes
- `whizbang.transport.dlq.escalated{transport}` Counter — escalated to internal DLQ after repeated failures
- `whizbang.transport.dlq.size{transport, subscription}` UpDownCounter — current pending DLQ depth per subscription (gauge for alerting)

### Flow A trigger model — RESOLVED

Configurable via `TransportDlqRecoveryOptions.Mode` enum (`Subscribe` / `Poll`); **default = `Subscribe`**.

**Subscribe mode (default).**
- ASB: `new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter }`
- RMQ: bind a Whizbang-managed consumer to the DLX
- Real-time recovery (<1s end-to-end); broker pushes us a notification when something lands
- Trade-off: holds an extra long-lived receiver per pod per subscription. For deployments where namespace connection-count is the binding constraint (e.g., Standard-tier ASB with many subscriptions), operators flip the config to `Poll`.

**Poll mode.**
- Worker wakes every `TransportDlqPollIntervalMinutes` (default `5`)
- Drains the DLQ subqueue/queue in a single pass via batched receivers
- No extra long-lived receivers; one short-lived receiver per poll cycle
- Worst-case recovery latency = poll interval

Per-transport overrides supported (e.g., ASB on `Subscribe`, RMQ on `Poll`) via `TransportDlqRecoveryOptions.PerTransport[TransportTag]`.

---

## Flow B — Whizbang internal DLQ

### The `wh_dead_letters` table (decided)

```sql
CREATE TABLE wh_dead_letters (
  -- identity
  dead_letter_id     UUID PRIMARY KEY,                -- generated; survives re-emission
  source_table       TEXT NOT NULL,                   -- 'wh_outbox' | 'wh_inbox' | 'wh_perspective_events'
  source_id          UUID NOT NULL,                   -- original message_id / event_work_id
  stream_id          UUID,                            -- nullable for single-source messages
  message_type       TEXT NOT NULL,
  destination        TEXT,                            -- routing destination (for outbox source)
  perspective_name   TEXT,                            -- for perspective source

  -- payload (forensic preservation)
  envelope           JSONB NOT NULL,                  -- full envelope from time of failure
  metadata           JSONB NOT NULL,

  -- failure provenance
  failure_reason     INTEGER NOT NULL,                -- MessageFailureReason enum
  error_text         TEXT,
  attempts_when_dlq  INTEGER NOT NULL,                -- attempts at time of dead-lettering
  dead_lettered_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  dead_lettered_by   UUID,                            -- instance_id

  -- recovery state
  recovery_status    INTEGER NOT NULL DEFAULT 0,      -- DeadLetterRecoveryStatus enum
  recovery_attempts  INTEGER NOT NULL DEFAULT 0,
  last_recovery_at   TIMESTAMPTZ,
  next_recovery_at   TIMESTAMPTZ,
  recovered_at       TIMESTAMPTZ,                     -- non-null when permanently recovered

  -- generation tagging (decided: yes)
  generation         TEXT NOT NULL,                   -- e.g., "0.493.0-alpha.1+app-1.42.0"
  retried_on_generations TEXT[] NOT NULL DEFAULT '{}', -- generations that have already been auto-retried

  -- operator disposition
  operator_disposition INTEGER NOT NULL DEFAULT 0,    -- DeadLetterDisposition enum
  operator_notes     TEXT,
  operator_actor     TEXT
);

CREATE INDEX wh_dead_letters_next_recovery_idx
  ON wh_dead_letters (next_recovery_at)
  WHERE recovered_at IS NULL AND recovery_status != 2;  -- 2 = HoldForReview

CREATE INDEX wh_dead_letters_stream_idx ON wh_dead_letters (stream_id, dead_lettered_at);
CREATE INDEX wh_dead_letters_generation_idx ON wh_dead_letters (generation);
```

Why a table over the existing status bit:
- DLQ rows are a forensic record; `wh_outbox` is the active queue. Mixing them adds clutter to every active-queue query.
- Recovery state (status, attempts, next-attempt, disposition) needs additional columns; growing `wh_outbox` for DLQ-only concerns is wrong.
- Cleaner enumeration for ops UI: `SELECT * FROM wh_dead_letters` vs. `SELECT * FROM wh_outbox WHERE status & 32768 > 0`.

The existing status bit on `wh_outbox` becomes a transient marker — set by `FailureFlushWorker` when `attempts >= MaxOutboxAttempts`, immediately followed by an insert into `wh_dead_letters` + delete from `wh_outbox`. Same atomic move for inbox and perspective_events.

### Enums

```csharp
public enum DeadLetterRecoveryStatus {
  Pending = 0,         // awaiting next_recovery_at
  Recovering = 1,      // currently being attempted by a worker
  HoldForReview = 2,   // no auto-retry; needs operator action
  Recovered = 3,       // re-emitted successfully
  PermanentlyFailed = 4,// exhausted all recovery policies
}

public enum DeadLetterDisposition {
  None = 0,            // operator hasn't touched it
  RetryNow = 1,        // operator requested immediate retry
  HoldIndefinitely = 2,// operator says "don't touch this without me"
  MarkPermanentlyFailed = 3, // operator gave up
}
```

### Error-class policy

Per-reason recovery defaults, exposed via `DeadLetterRecoveryOptions`:

| `failure_reason` | Default policy | Rationale |
|---|---|---|
| `Throttled` | `AggressiveRetry` (3 attempts, 30 min cooldown) | Transient broker pressure; we already have `TransportFailureClassifier` so by the time it lands here it's been throttled for >25 broker events |
| `TransportException` | `MediumRetry` (3 attempts, 1 hour cooldown) | Broker outage; may have resolved by next attempt |
| `LeaseExpired` | `AggressiveRetry` (5 attempts, immediate cooldown) | Usually transient pod-restart artifact |
| `MaxAttemptsExceeded` | `ConservativeRetry` (1 attempt, 6 hour cooldown) | Underlying problem may still be present |
| `EventStorageFailure` | `HoldForReview` | Data-layer problem; never safe to auto-retry |
| `ValidationError` | `HoldForReview` | Code fix usually needed |
| `SerializationError` | `HoldForReview` | Schema drift; needs code/migration |
| `TransportNotReady` | `MediumRetry` (3 attempts, 30 min cooldown) | Service may have recovered |
| `Unknown` | `OneShotRetry` (1 attempt, then `HoldForReview`) | Try once; if still failing, escalate |

Policies are records:

```csharp
public sealed record RecoveryPolicy(
  string Name,
  int MaxRecoveryAttempts,
  TimeSpan Cooldown,
  bool HoldForReviewAfterExhaustion
);
```

Operators override via `DeadLetterRecoveryOptions.PolicyByReason[MessageFailureReason]` at startup.

### Generation-based replay (decided: yes)

Every DLQ row records the `generation` string at time of dead-lettering — e.g., `"0.493.0-alpha.1+app-1.42.0"` (Whizbang version + service version + branch hash).

On worker startup:
- Compute current generation
- Find all `wh_dead_letters` rows where `current_generation NOT IN retried_on_generations` AND `recovery_status != HoldForReview`
- Reset `next_recovery_at = NOW()`, append `current_generation` to `retried_on_generations`
- This gives every row exactly one auto-retry per generation

Catches the "we shipped the fix" case for free — typical scenario:
- Validation rule was too strict; messages dead-lettered
- We fix the rule, deploy
- All `ValidationError` DLQ rows from the prior generation auto-retry once
- Most succeed; remaining failures stay in the same DLQ row but won't retry again until the next generation

Configurable: `DeadLetterRecoveryOptions.GenerationRetryReasonAllowList` (default: all reasons; operators can exclude e.g. `EventStorageFailure`).

### Stream-aware recovery configurability — RESOLVED (hybrid E + C)

Five options were considered in increasing order of flexibility:

**Option A: Always per-message.**
- Each DLQ row recovers independently.
- Pros: simple; matches industry norm.
- Cons: a successfully-recovered event 50 still leaves event 51 (which had a different failure) stranded; FIFO semantics broken on partial recovery.

**Option B: Always tail-recover when stream is set.**
- When recovering DLQ entry on stream X at event 50, also re-enqueue events 51, 52, … 60 (next 10 stuck on the same stream) in coordinated fashion.
- Pros: preserves FIFO under partial recovery.
- Cons: amplifies blast radius; one recovery attempt now touches 10 rows. If the underlying error was per-event (e.g., one event has a bad payload), this still strands.

**Option C: Per-perspective attribute.**
```csharp
[Perspective]
[StreamRecovery(StreamRecoveryMode.TailAware)]  // or PerMessage
public class OrderProjection : IPerspective<OrderState> { }
```
- Pros: declarative; near the type; source-genned so analyzers catch typos.
- Cons: requires source gen change; can't change at runtime.

**Option D: Per-message-kind attribute on the IEvent.**
```csharp
[FifoStreamRecovery]  // marker attribute; recovery worker reads it
public record OrderPlaced(Guid OrderId, ...) : IEvent { }
```
- Pros: semantically clearer — "FIFO is a property of THIS event's stream"; doesn't require knowing which perspectives consume it.
- Cons: same as C; less flexibility if different consumers need different policies for the same event.

**Option E: Default + override via interface.**
```csharp
public interface IDeadLetterRecoveryPolicy {
  StreamRecoveryMode GetStreamMode(DeadLetterEntry entry);
  TimeSpan? OverrideCooldown(DeadLetterEntry entry);
  bool ShouldRecover(DeadLetterEntry entry);
}
```
- Default behavior: tail-aware for entries with `stream_id != null`, per-message otherwise.
- Application can register a custom impl that inspects `message_type`, `failure_reason`, `stream_id`, etc., to decide.
- Pros: works for 80% out of the box; full escape hatch for 20%; runtime-changeable; no source gen.
- Cons: requires understanding the plugin interface to customize; less discoverable than an attribute.

**Resolution: Hybrid E + C.**
- Default impl of `IDeadLetterRecoveryPolicy` reads `[StreamRecovery(Mode)]` attribute on perspectives + `[FifoStreamRecovery]` on event types — discoverable for the 80% case
- Application overrides the whole policy by registering a custom `IDeadLetterRecoveryPolicy` — full escape hatch for the 20% edge cases
- Default-default: `TailAware` for entries with non-null `stream_id`, `PerMessage` otherwise

Implementation note: the policy plug-in interface receives the full `DeadLetterEntry` (including `message_type`, `failure_reason`, `stream_id`, `dead_lettered_at`), so custom policies can route on any of those without further extension.

### Why recovery preserves order "for free"

A recovered DLQ entry is just a delayed regular message at the point it re-enters the source table — and Whizbang's existing FIFO + cursor + rewind machinery handles ordering without the recovery worker needing any special logic. The reasoning is different for the two flows:

**Flow B re-emission — order preserved by stream-FIFO claim gate.**

When event 50 of stream X dead-letters, events 51, 52 of the same stream are **already blocked** in their source table:

- `claim_orphaned_outbox` (mig 024) has an explicit "STREAM ORDERING CHECK: Don't claim if there's an earlier message in the same stream that's scheduled for future retry" — so 51, 52 sit unclaimed while 50 is in DLQ
- Same gate exists in `claim_orphaned_inbox` and `claim_orphaned_perspective_events`

Recovery for Flow B just re-inserts the DLQ row back into the source table (with `attempts = 0`, lease released). The next claim cycle picks up 50 first (still the earliest by `event_id` for stream X), processes it, and only then does 51 become claimable. Order preserved transparently — the recovery worker doesn't need to coordinate with downstream cursor state at all.

This is why `[StreamRecovery(TailAware)]` (the Q 4 default for stream-bound messages) is mostly a *grouping hint* for the recovery worker's metrics and operator UI ("these N DLQ entries are all on stream X — show them together; recovering 50 will unblock the others"), not a correctness requirement.

**Flow A re-emission — order preserved by perspective rewind on the receiver.**

Transport DLQ is messier because once a broker dead-letters event 50, the broker may deliver 51, 52 *before* 50 (broker DLQ is asynchronous to the main delivery path). When recovery re-publishes 50, the receiver gets it out of order:

1. Service A's outbox published 50, 51, 52 to ASB ✓
2. Service B's handler failed on 50 → ASB DLQ'd it
3. Service B processed 51, 52 (cursor at 52 now)
4. Recovery worker re-publishes 50 from DLQ → main subscription
5. Service B receives 50; cursor for stream X is at 52; **inversion detected**

The receiver service's perspective worker **already** handles this case — Phase H step 6 shipped the cursor-inversion detector + `RewindAndRunAsync`. When 50 arrives and the cursor is past it, the perspective rewinds to before 50 and reapplies 50, 51, 52 in order from `wh_event_store`. The recovery worker doesn't need to know any of this — it just re-publishes; the inversion-detect-and-rewind path takes over.

→ **Net invariant: a recovered DLQ entry IS a delayed regular message.** No new ordering primitives needed. The recovery worker focuses on policy (when/whether to recover); the existing FIFO/cursor/rewind infrastructure handles the rest.

### Idle-cadence trigger

The worker wakes on:
- **Timer backstop:** every `RecoveryScanIntervalMinutes` (default `10`)
- **Idle signal:** `IWorkCoordinator` exposes `OnBatchEmpty` event; worker subscribes; when batch returns 0 rows for ≥ `IdleSignalDebounceSeconds` (default `30`), trigger a scan
- **Generation transition:** on startup, after detecting new generation, fire one immediate scan with generation-replay logic
- **Operator API:** `POST /dlq/scan-now` triggers immediate scan

Default: backstop timer + idle-signal + generation-trigger all enabled.

### Recovery worker steps (per scan)

1. Compute `current_generation` (cached at startup)
2. **Generation replay:** select DLQ rows where `current_generation NOT IN retried_on_generations` AND `recovery_status NOT IN (Recovered, PermanentlyFailed)` AND `operator_disposition != HoldIndefinitely`; reset `next_recovery_at = NOW()`, append generation
3. **Operator-requested retries:** select rows with `operator_disposition = RetryNow`; reset `next_recovery_at = NOW()`, set disposition back to `None`
4. **Cooldown-driven retries:** select rows where `next_recovery_at <= NOW()` AND `recovery_status = Pending` AND `recovery_attempts < policy.MaxRecoveryAttempts`
5. **Stream-aware grouping:** apply `IDeadLetterRecoveryPolicy.GetStreamMode` per row; for `TailAware` rows, find all sibling DLQ entries on the same `stream_id` ordered by original `event_id` ASC; group them
6. **Re-emit:** for each group, mark `recovery_status = Recovering`, re-insert into source table (`wh_outbox` / `wh_inbox` / `wh_perspective_events`) with `attempts = 0` (fresh budget), wait for source-table consumer to actually process, then on success mark `recovery_status = Recovered, recovered_at = NOW()`; on failure update `recovery_attempts++, next_recovery_at = NOW() + policy.Cooldown`
7. **Exhaustion:** if `recovery_attempts >= policy.MaxRecoveryAttempts`, set `recovery_status = PermanentlyFailed` OR `HoldForReview` depending on policy
8. **Emit metrics**

### Observability

| Metric | Type | Tags | Purpose |
|---|---|---|---|
| `whizbang.dlq.size` | UpDownCounter | `source_table`, `failure_reason` | Current open DLQ depth — gauge for alerting |
| `whizbang.dlq.added` | Counter | `source_table`, `failure_reason` | Inbound rate to DLQ |
| `whizbang.dlq.recovered` | Counter | `source_table`, `failure_reason`, `via` (=generation/cooldown/operator) | Successful recoveries |
| `whizbang.dlq.permanently_failed` | Counter | `source_table`, `failure_reason` | Truly given up |
| `whizbang.dlq.held_for_review` | Counter | `source_table`, `failure_reason` | Waiting on a human |
| `whizbang.dlq.recovery_duration` | Histogram (ms) | `source_table`, `via` | How long recovery took |
| `whizbang.dlq.generation_replay_count` | Counter | `from_generation`, `to_generation` | Number of rows auto-replayed at deploy time |

### Operator API surface

```
GET    /dlq                           — paginated list with filters (stream_id, reason, status, age)
GET    /dlq/{deadLetterId}            — full forensic detail
POST   /dlq/{deadLetterId}/retry      — set disposition=RetryNow
POST   /dlq/{deadLetterId}/hold       — set disposition=HoldIndefinitely
POST   /dlq/{deadLetterId}/give-up    — set status=PermanentlyFailed
POST   /dlq/bulk/retry                — body: { filter, max_count } — bulk retry
POST   /dlq/scan-now                  — trigger immediate worker scan
GET    /dlq/stats                     — counts by reason/status (replaces dashboard query)
```

Lives in `Whizbang.Hosting.AspNet` as opt-in controllers via `AddWhizbangDeadLetterApi()`.

---

## Implementation slices (TDD order)

Once we resolve the two open questions (Flow A cadence, Flow B stream-aware configurability), implementation order:

1. **SQL migration** — `wh_dead_letters` table + indexes + `move_to_dead_letters(p_source_table, p_source_id)` SQL function (atomic insert-into-DLQ + delete-from-source)
2. **`DeadLetterEntry` record + `IDeadLetterStore` interface** in Core
3. **EFCore + Dapper implementations** of `IDeadLetterStore`
4. **`FailureFlushWorker` integration** — when `attempts >= MaxAttempts`, call `move_to_dead_letters` instead of just setting the DLQ status bit
5. **`DeadLetterRecoveryOptions` + `RecoveryPolicy` types**
6. **`IDeadLetterRecoveryPolicy` interface + default impl** (per Q 4 resolution)
7. **`DeadLetterRecoveryWorker`** — the scan loop + generation-replay + operator-disposition handling
8. **`OutboxDrainWorker` / `InboxDispatchWorker` / `PerspectiveWorker` hooks** — re-emission entry points
9. **Metrics** (`DeadLetterMetrics` class, new meter `Whizbang.DeadLetters`)
10. **`AzureServiceBusDeadLetterRecoveryWorker`** (Flow A — per Q 5 resolution)
11. **`RabbitMqDeadLetterRecoveryWorker`** (Flow A)
12. **API controllers in `Whizbang.Hosting.AspNet`**
13. **Documentation + ai-docs/operations page**

Estimated ~2000-2500 LOC + tests. Probably 2-3 PRs end-to-end.

---

## All design questions resolved — ready for implementation

Next step: cut `release/v0.494.0-alpha.1` after #227 lands; ship in 2-3 PRs per the slice list above.

## Non-goals (out of scope here)

- **Cross-service event-store gap detection.** A receiver could in theory query the sender's event store to detect "I'm missing event X." This is a much larger architectural change (cross-service event-store access, auth, network exposure). Defer to a separate plan if/when the existing recovery surface isn't enough.
- **AIMD-style auto-tuned recovery rate.** Phase C from the throttle work. Same approach when we get there.
- **Replacing `MaxAttempts` with cost-based decisions.** Industry has this (per-message recovery budget in dollars/compute), but our model is fine for now.

---

## References

- Throttle classification + retry that ships in PR #227: source `Whizbang.Core/Workers/TransportFailureClassifier.cs`, `Whizbang.Core/Workers/TransportPublishStrategy.cs`
- Phase H step 9 follow-up note in `plans/proud-wibbling-orbit.md` (line ~410) called out "DLQ table for forensic preservation" as deferred — this plan closes that gap
- `MessageFailureReason` enum: `Whizbang.Core/Messaging/MessageFailureReason.cs`
