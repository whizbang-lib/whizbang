# Whizbang.Sagas — architecture proposal

> **Audience**: the parallel Whizbang library session.
> **Status**: design proposal, ready to consume.
> **Source of motivation**: a week of a consumer bulk-import work that ended with a
> `Whizbang.Sagas` shaped hole — pattern-quality coordination that every a consumer
> saga reinvents, with a residual emission race the current
> `SagaCompletionGuard` doesn't fully close. The proposals below come from
> what we needed and what we wished was there. Cross-reference:
> `plans/saga-ui-audit-2026-06-22.md`, `docs/patterns/adding-a-new-saga.md`.

---

## TL;DR

Two changes, in this order:

1. **Add `IDispatcher.PublishOnceAsync(claimKey, evt)`** — a dispatcher-level
   exactly-once primitive backed by an atomic claim row. Fixes the 1–4×
   `SagaCompletedEvent` emission race without violating Apply purity or
   requiring any consumer to change their saga shape.
2. **Extract `Whizbang.Sagas` as a separate application-block library.**
   Houses `BaseSagaEvent` / `BaseSagaItemEvent`, `BaseSagaModel`,
   `ISagaService`, the `SagaItemStreams` routing convention, the
   `SagaItemModel` projection, `SagaCompletionGuard`, the live-progress
   resolver helper, and Rule-17 hook bookends. Today's `a consumer.Infrastructure.Saga/`
   becomes mostly empty — a consumer consumes the library.

The split is principled: `Whizbang.Core` is the event-sourcing kernel any
consumer needs; `Whizbang.Sagas` is a multi-stream coordination pattern
not every consumer needs. Premature granularity (multiple application
blocks) is rejected in favor of this single coordination block.

---

## 1. The emission race — current state and what's structurally wrong

### What happens on a typical bulk-import saga today

```
N items run in parallel on N per-item streams (SagaItemStreams.Of(sagaId, item)).
Each item's terminal handler is a receptor subscribed to SagaItemCompletedEvent
on its per-item stream — single-threaded per item, parallel across items.

For items 342, 343, 344, 348 (all completing within ~227ms):

  receptor for 342: Load saga → CompletionEventDispatched=false → AlreadyEmittedAsync=false → PublishAsync(SagaCompletedEvent)
  receptor for 344: Load saga → CompletionEventDispatched=false → AlreadyEmittedAsync=false → PublishAsync(SagaCompletedEvent)
  receptor for 348: Load saga → CompletionEventDispatched=false → AlreadyEmittedAsync=false → PublishAsync(SagaCompletedEvent)
  receptor for 343: Load saga → CompletionEventDispatched=false → AlreadyEmittedAsync=false → PublishAsync(SagaCompletedEvent)

Result: 4× SagaCompletedEvent on the master saga stream.
```

Both existing guards — the projection's `CompletionEventDispatched` field and
`SagaCompletionGuard.AlreadyEmittedAsync` — are *read-then-act* with a
check-to-commit window. They collapsed the historical 14× duplicate-storm
down to 1–4×, but two concurrent emitters that overlap their reads still
race.

### Why "emit from Apply" is the wrong fix

The first instinct — "put emission inside the master saga's Apply, which is
single-threaded by Whizbang's perspective runner" — violates the most
load-bearing invariant in the framework: **Apply must be pure**. Apply runs
again on replay. Anything Apply emits is either re-emitted on replay
(duplicate in store, wrong) or the framework has to track "is this live or
replay?" (which is exactly the impurity the invariant exists to forbid).
Apply projects events into state. Full stop. Anything that produces *new*
events is, by definition, a receptor — side-effectful, not pure, must be
idempotent under at-least-once delivery.

So the layering is:

| Layer | Role | Purity contract |
|---|---|---|
| **Apply** | Project events → state | Pure, deterministic, replayable, no I/O, no emission |
| **Receptor** | React to events with new commands/events | Side-effectful, **must be idempotent** under retry/replay |
| **Dispatcher** | Deliver events with the at-most-once / exactly-once guarantee receptors depend on | Transactional infrastructure, not domain |

The race lives in the receptor layer. Receptors *correctly* PublishAsync —
that's their job. What they lack is the framework primitive to constrain the
emission to **exactly once** when N of them race. The correct layer for that
constraint is the dispatcher, not the projection.

### Why "route per-item events to the master stream + emit from Apply" is also wrong

Same Apply-purity violation. Re-routing the event input doesn't change the
fact that emitting from inside Apply (whether via `ApplyResult.Actions` or
any other surface) is a side effect that breaks replay. Reject.

### Why a partial-unique DB constraint is wrong

A partial UNIQUE index on `wh_event_store(stream_id, event_type) WHERE event_type LIKE '%SagaCompletedEvent%'`
is bulletproof for live writes but breaks legitimate rewind/replay scenarios
because INSERT 2-N hits `23505` even when the duplicate is a valid rebuild.
The store must remain freely insertable.

---

## 2. The proposed primitive: `IDispatcher.PublishOnceAsync`

### API

```csharp
namespace Whizbang.Core.Dispatch;

public interface IDispatcher {
  // existing surface …

  /// <summary>
  /// Publish at most one event with the given claim key. The first call to
  /// succeed in claiming the key proceeds with dispatch and returns true.
  /// Subsequent calls with the same key return false without publishing.
  /// </summary>
  /// <remarks>
  /// Idempotency is enforced at the messaging layer via an atomic INSERT
  /// against <c>wh_unique_emission_claims (claim_key PRIMARY KEY, claimed_at, claimed_by_event_id)</c>.
  /// The claim table is messaging infrastructure, not domain state — it
  /// does NOT participate in projection replay. Reasonable retention is a
  /// few hours; rows for completed claims can be purged by the same
  /// cleanup that prunes <c>wh_outbox</c> / <c>wh_inbox</c>.
  /// </remarks>
  ValueTask<bool> PublishOnceAsync<TEvent>(
      string claimKey,
      TEvent evt,
      CancellationToken ct) where TEvent : IEvent;
}
```

### Storage shape

```sql
CREATE TABLE IF NOT EXISTS wh_unique_emission_claims (
  claim_key             text         PRIMARY KEY,
  claimed_at            timestamptz  NOT NULL DEFAULT now(),
  claimed_by_event_id   uuid         NOT NULL,
  -- Optional retention helper. Rows older than the cleanup window are safe
  -- to drop. Restart-safe because the first emitter has already written the
  -- domain event to wh_event_store; the claim row is purely a race breaker.
  expires_at            timestamptz  NOT NULL DEFAULT (now() + interval '7 days')
);

CREATE INDEX IF NOT EXISTS idx_wh_unique_emission_claims_expires
  ON wh_unique_emission_claims(expires_at);
```

### Implementation sketch (EFCore.Postgres driver)

```csharp
public sealed class EFCoreDispatcher : IDispatcher {
  // … existing fields …

  public async ValueTask<bool> PublishOnceAsync<TEvent>(
      string claimKey,
      TEvent evt,
      CancellationToken ct) where TEvent : IEvent {
    ArgumentException.ThrowIfNullOrWhiteSpace(claimKey);

    // Atomic claim. INSERT … ON CONFLICT DO NOTHING returns 1 row affected
    // for the first writer, 0 for the rest. No SELECT-then-INSERT race.
    var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
      $@"INSERT INTO wh_unique_emission_claims (claim_key, claimed_by_event_id)
         VALUES ({claimKey}, {evt.MessageId})
         ON CONFLICT (claim_key) DO NOTHING",
      ct);

    if (affected == 0) {
      // Someone else already claimed; intentional no-op.
      return false;
    }

    await PublishAsync(evt, ct);
    return true;
  }
}
```

The claim insert + the outbox insert from `PublishAsync` can run inside the
same transaction the caller is already in, which means: if the outer
transaction rolls back, the claim is released. That's the correct semantic —
"claim taken iff the emission committed." First-writer-wins under all
ordering, no SELECT-then-act window anywhere.

### Migration impact

Once `PublishOnceAsync` lands, a consumer collapses:

```csharp
// Before — in BulkImportCompletion.TryEmitAsync (~25 lines):
var saga = await sagaRepository.LoadAsync(sagaId, ct);
if (saga is null || saga.CompletionEventDispatched) return;
if (saga.TotalItems <= 0) return;
var agg = await itemRepository.GetAggregateForSagaAsync(sagaId, ct);
var counts = await SagaItemCompletionReconciler.ResolveCompletionCountsAsync(...);
if (counts is null) return;
var (completedItems, failedItems) = counts.Value;
if (await SagaCompletionGuard.AlreadyEmittedAsync(...)) return;
await dispatcher.PublishAsync(new SagaContracts.SagaCompletedEvent { … });

// After — same file:
var saga = await sagaRepository.LoadAsync(sagaId, ct);
if (saga is null || saga.TotalItems <= 0) return;
var agg = await itemRepository.GetAggregateForSagaAsync(sagaId, ct);
var counts = await SagaItemCompletionReconciler.ResolveCompletionCountsAsync(...);
if (counts is null) return;
var (completedItems, failedItems) = counts.Value;
await dispatcher.PublishOnceAsync(
  claimKey: $"saga-completed:{sagaId}",
  new SagaContracts.SagaCompletedEvent { … },
  ct);
```

The 15 projection-side `Apply(SagaCompletedEvent)` CAS guards (`if (current.CompletionEventDispatched) return None();`) — added in a consumer commit `b9a640f9d` as defense-in-depth against the race — become unnecessary. They can be removed in a follow-up cleanup. The receptor-side `AlreadyEmittedAsync` checks in 5 handlers (BulkImport, JobMapping, JobArchActivation, OrderFieldPopulation, EmployeeImport) also become redundant.

Result: ~20 lines removed across a consumer, one new line per emission site (use
`PublishOnceAsync` instead of `PublishAsync`), and the 1–4× residual goes
to exactly 1× by construction.

### Why a *dispatcher-level* primitive and not a *receptor* attribute

I considered `[CompletionGuarded(EmittedEvent = typeof(X))]` on the receptor
class — Whizbang's source generator would wrap `HandleAsync` with the
claim check. Rejecting that:

- Receptors sometimes conditionally emit (the X path emits, the Y path
  doesn't). A receptor-level attribute can't model that without becoming
  another layer of policy.
- The emission decision belongs to the call site, not the handler. The
  caller knows the claim key (it's the domain semantic — `"saga-completed:{sagaId}"`),
  the framework doesn't.
- `PublishOnceAsync` is composable with anything: cron-triggered emissions,
  command handlers, receptors, manual repair scripts. A receptor attribute
  helps only receptors.

Explicit API at the call site is the correct ergonomic.

---

## 3. Two related primitives the saga library benefits from

These exist either as TODOs in the codebase or as patterns every saga
reinvents. None are required by the `PublishOnceAsync` work above, but
landing the saga library cleanly assumes them.

### 3a. `[StreamRouting]` — already TODO'd

Across the existing perspective runners we have comments like:

```
// TODO: [WHIZBANG MIGRATION] Convert to StreamRouting attribute on event
```

Today, perspective routing reads `event.StreamId` directly. A
`[StreamRouting]` attribute on a property (e.g. `EntityId` for sagas, or
`OperationId` for bulk operations) would let the framework route an event's
projection input to a different stream than the one it was emitted on. This
is the saga-library-relevant case: per-item events ride per-item streams
for `SagaItemModel` (high parallelism) AND route to the master saga stream
for the saga's own aggregate updates if a future saga design wants that.

Independent of completion-race fix. Tracked separately.

### 3b. `BaseSagaModel.Id` should not require the `[StreamId] public new Guid Id` shadow trap

Every concrete saga model declares:

```csharp
public class SagaModel : BaseSagaModel {
  [StreamId]
  public new Guid Id { get; set; }
  // …
}
```

The `new` keyword *hides* `BaseSagaModel.Id` rather than overriding it.
This silently breaks polymorphism: a helper taking `BaseSagaModel saga`
and reading `saga.Id` gets `Guid.Empty`. We hit this during F3 (the live-
progress helper) and had to refactor to take `Guid sagaId` as an explicit
parameter. The fix:

- Make `[StreamId]` inheritable so it can sit on `BaseSagaModel.Id` directly, OR
- Make `BaseSagaModel.Id` virtual so derived can override (not shadow), OR
- Add a Roslyn analyzer that warns on the `new` shadow when the base property
  is the saga key.

Lowest-risk option is the second — make `BaseSagaModel.Id` virtual,
deprecate the `new` shadow pattern in derived sagas. Whizbang.Sagas can
ship a code analyzer that flags the pattern.

---

## 4. `Whizbang.Sagas` — the application block

### Layering

```
Whizbang.Core            ← event-sourcing kernel: events, perspectives,
                            receptors, outbox/inbox, dispatcher (incl.
                            PublishOnceAsync), work pump, [StreamRouting],
                            transports, scopes, observability, JSON
                            registry, ICompositeEvent, ICollectiveEvent

Whizbang.Sagas           ← multi-stream coordination block:
                            BaseSagaEvent, BaseSagaItemEvent, BaseSagaModel,
                            ISagaService<TItem>, BaseSagaService<TItem>,
                            SagaItemStreams (per-item stream-id derivation),
                            SagaItemModel + SagaItemProjection,
                            SagaItemRepository (with GetAggregateForSagaAsync),
                            SagaItemCompletionReconciler,
                            SagaCompletionGuard (wraps PublishOnceAsync),
                            SagaLiveProgressResolvers (the F3 helper, generic),
                            SagaHookApplyHelper + Rule-17 hook bookend events,
                            Roslyn analyzer for the [StreamId] shadow trap

(stays in a consumer or its own block later, NOT in Whizbang yet:
  notifications, audit, multi-tenancy scopes — only one consumer today.)
```

### Why two libraries and not six

I initially proposed six (Sagas, Collectives, Composites, Notifications,
Audit, MultiTenancy). That's wrong — it mirrors dotnet's Enterprise
Library which targeted a much larger orthogonal surface (data, validation,
logging, cache, security, exception handling, policy injection — each was
its own concern across an entire app stack).

Whizbang's surface is narrower and the consumer surface is smaller. The
right split criterion is **does every consumer need this?**

- `ICompositeEvent` (wire-only fan-out) and `ICollectiveEvent` (set-mutation
  descriptor) are *event primitives* — any consumer with bulk-like patterns
  uses them. They stay in Core.
- Sagas are a *coordination pattern* on top of multiple streams. Not every
  consumer needs them. They earn the separate library because:
  - The surface (BaseSagaModel, ISagaService, SagaItemStreams, etc.) is
    cohesive and useful only together.
  - Pulling in saga primitives shouldn't be required just to use event
    sourcing.
  - The `PublishOnceAsync` dispatcher API + the saga lib together let a consumer
    drop ~80% of `a consumer.Infrastructure.Saga/` — that's a concrete migration
    win, not a hypothetical one.
- Notifications, audit, multi-tenancy — only a consumer uses them today. Premature
  library extraction bakes in the wrong shape because there's only one
  user. They stay in a consumer.Infrastructure until a second consumer exists.

Two libraries. No more, no less.

### What a consumer migrates

When `Whizbang.Sagas` ships, the work on the a consumer side is mechanical:

1. Delete `src/aspects/a consumer.Infrastructure/Saga/` *except*
   `a consumer.Contracts.Saga.SagaNames` (a consumer-domain saga names) and any
   a consumer-domain-specific contracts.
2. Replace `a consumer.Infrastructure.Saga` namespace imports with `Whizbang.Sagas`
   in every projection, receptor, service, and test.
3. Remove the 15 `Apply(SagaCompletedEvent)` CAS guards added in
   `b9a640f9d` — `PublishOnceAsync` makes them defense-in-depth against
   nothing.
4. Remove the receptor-side `SagaCompletionGuard.AlreadyEmittedAsync`
   checks in 5 handlers — same reason.
5. Drop the per-saga `XxxCompletion.TryEmitAsync` helpers; replace with the
   one provided by `Whizbang.Sagas`.
6. The `SagaLiveProgressResolvers` helper (in
   `src/services/a consumer.BffService/Features/SagaItemFeature/Domain/`) moves
   into `Whizbang.Sagas`; the 10 BFF saga model delegates point at the
   library version.

Expected diff: net negative ~500 LOC in a consumer, no behavior change, single
emission of `SagaCompletedEvent` by construction.

---

## 5. Implementation sequencing

If the other session wants to ship in order of consumer value:

1. **`PublishOnceAsync` + the claim table.** Smallest, highest-impact
   change. Once it's in Whizbang.Core, a consumer's `SagaCompletionGuard` can be
   refactored in a consumer (or in `Whizbang.Sagas` once that exists) to use it.
   The 1–4× residual goes to 1× immediately.
2. **`Whizbang.Sagas` package extraction.** Move existing a consumer saga
   primitives into the new library. No a consumer migration required immediately
   — a consumer can keep its `Infrastructure.Saga/` shim that re-exports from
   `Whizbang.Sagas` for one transitional release.
3. **a consumer migration.** Switch namespaces, delete shim. Net LOC drop.
4. **`[StreamRouting]`** — independent track, not on the critical path for
   the emission race fix.
5. **`BaseSagaModel.Id` shadow fix** — bundled with `Whizbang.Sagas` v1.0 or
   shipped as a 1.x cleanup.

---

## 6. What this proposal does *not* do

- It does not change the per-item-streams routing convention. Sagas still
  fan out to per-item streams for high parallelism.
- It does not change `Apply`'s purity contract. Apply stays pure; receptors
  stay where side effects live.
- It does not introduce a partial-unique constraint on `wh_event_store`.
  The store is still freely insertable; the claim table is the
  uniqueness primitive.
- It does not require any saga consumer to rewrite their projection
  shape. Existing `[StreamId] public new Guid Id` continues to work; the
  shadow analyzer is a *warning*, not a break.
- It does not propose a third or fourth Whizbang library. Two is the floor
  and the ceiling.

---

## 7. Open questions for the Whizbang session

1. **Claim retention.** The claim table grows unboundedly without cleanup.
   Proposal above: 7-day TTL + index on `expires_at`, drop with the
   existing outbox/inbox prune job. Is that consistent with how Whizbang
   currently handles transient infrastructure tables?
2. **Transaction semantics of `PublishOnceAsync`.** If the caller is inside
   an ambient transaction and the outer transaction rolls back, the claim
   row also rolls back — releasing the claim. Is that the desired
   semantic? (I think yes — "claim is taken iff the emission committed"
   is the cleanest invariant — but worth confirming.)
3. **Claim key collisions across saga types.** `"saga-completed:{sagaId}"`
   is unique within a single saga but not across saga types. If two
   *different* sagas share a `sagaId` (shouldn't happen but in principle
   possible), they'd collide. Prefixing with saga name —
   `"saga-completed:BulkImport:{sagaId}"` — is safer.
4. **Telemetry on the claim path.** Should `PublishOnceAsync` emit a metric
   on "lost the claim race" so we can see how often the race actually
   happens at runtime? Useful for verifying the production 1–4× reduces to 0
   post-fix.
5. **Roslyn analyzer for the `[StreamId] new Guid Id` shadow.** Ships in
   `Whizbang.Sagas`? Or in `Whizbang.Analyzers` (a third package, but a
   tooling one not a runtime one)? Lean toward bundled with the saga
   library.

Answers to these probably emerge naturally during implementation. None of
them block the design.
