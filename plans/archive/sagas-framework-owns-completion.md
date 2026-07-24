# Plan: Framework owns the saga-completion lifecycle end-to-end

> **Status (2026-06-25)**: Draft. Captured after a real-world adoption
> of `BaseSagaService` exposed four distinct leaks where the consumer
> ended up implementing what the framework should own. This plan
> tracks the framework-side cleanup that lets the next consumer adopt
> `BaseSagaService` without re-inventing those four pieces.

## Observation: four things the consumer had to implement

The framework's `BaseSagaService<T1..T9>` ships with auto-armed
watchdog ticks, an in-memory completion tracker, and a
`TryRecoverViaWatchdogAsync` slow path. In practice — when a saga
fans out items across multiple pods via a transport-backed deployment
— none of these alone drives the saga to completion. Consumers end up
writing four pieces of completion plumbing that the framework's docs
imply it owns.

### Leak 1 — `LoadProjectionAsync` must compute authoritative counts

`BaseSagaService.TryRecoverViaWatchdogAsync` reads the consumer's
projection via `LoadProjectionAsync` and decides whether to emit
`SagaCompletedEvent` based on
`saga.CompletedItems + saga.FailedItems >= saga.TotalItems`.

But per-item terminal events (`SagaItemStartedEvent` etc.) ride
**per-item streams** (`SagaItemStreams.ResolveStreamId(sagaName, sagaId,
itemId)`), not the saga's own stream. Any consumer projection whose
`Apply(SagaItemCompletedEvent)` is defined on the saga's stream
therefore never fires for these events; the projection's
`CompletedItems` stays at 0 even when all N items are terminal in the
durable event store and in the per-item projection table.

A consumer override of `LoadProjectionAsync` that simply returns the
saga projection's `CompletedItems` feeds the framework a stale 0, so
`TryRecoverViaWatchdogAsync` decides "not done" and returns false.

The consumer ends up re-implementing an authoritative-counts path
(cheap per-item aggregate → event-store reconciliation for stranded
rows) that the framework should already have, because the framework
already owns the `wh_per_saga_item` schema.

### Leak 2 — Watchdog doesn't re-arm itself

Framework docs (XML on `SagaCompletionWatchdogTickEvent.RescheduleCount`)
say:

> Drives the receptor's exponential backoff schedule
> (30s → 2m → 8m → 30m → abandon-and-alert).

`BaseSagaService.TryRecoverViaWatchdogAsync` does not re-arm when it
returns false. The tick is consumed; if recovery wasn't needed yet
(e.g., the tick fired during fan-out at T+65s while items were still in
flight), the watchdog never re-arms, and the saga is permanently
dependent on the fast-path completing — which under cross-pod fan-out
it can't.

The consumer's workaround is either (a) re-arm in the consumer's tick
receptor or (b) skip the watchdog and drive completion checks
event-driven from per-item terminal receptors instead. Either way the
consumer is filling in framework behavior.

### Leak 3 — In-memory completion tracker is per-pod

The framework's fast path inside `BaseSagaService.UpdateItemAsync` is:

1. Publish the per-item event.
2. Increment in-memory `_completionTrackers[sagaId].Completed`.
3. If `tracker.Completed + tracker.Failed >= tracker.Total`, publish
   `SagaCompletedEvent` via `PublishOnceAsync`.

Under same-pod processing this works. Under cross-pod fan-out (the
common case under transport-backed deployments — the transport
distributes per-item commands to whichever pod has capacity), each
pod's tracker only sees a fraction of the items. No pod's tracker ever
reaches `Total`. Fast-path completion is structurally impossible.

The watchdog (had it re-armed) would be the compensating mechanism.
Combined with Leak 1 + Leak 2, the framework as-shipped has no working
completion path under cross-pod fan-out without consumer help.

### Leak 4 — Per-item recovery receptors are consumer-written

The working consumer pattern (after Leak 1/2/3 are worked around) is a
pair of receptors:

```csharp
[FireAt(LifecycleStage.PostAllPerspectivesInline)]
public class SagaItemCompletedHandler(MySagaService _svc) : IReceptor<MyItemCompletedEvent> {
  public async ValueTask HandleAsync(MyItemCompletedEvent @event, CancellationToken ct) {
    if (@event.SagaName != "MySaga") return;
    var ctx = new SagaContext(@event.SagaId, @event.EntityId ?? Guid.Empty, null);
    await _svc.TryRecoverViaWatchdogAsync(ctx, ct);
  }
}
```

Every consumer adopting `BaseSagaService` will have to write the same
boilerplate per saga. Each one is a fresh chance for a typo (wrong
SagaName, wrong event type, missing the failed-event twin) to silently
break completion. This is exactly the kind of mechanical glue the
framework's generator should emit.

## Goal

After this plan ships, a fresh consumer of `BaseSagaService` should
need to supply only:

- The 9 generic event types (matches today).
- The per-item command processing logic (consumer's `IReceptor<TItemCmd>`).
- `BuildXxxEvent` factories (matches today).
- The post-completion hooks via `[FireAt(PostAllPerspectivesInline)]` on
  `SagaCompletedEvent` (matches today).

They should **not** need to:

- Implement `LoadProjectionAsync` for the completion decision.
- Register per-item terminal receptors that call
  `TryRecoverViaWatchdogAsync`.
- Worry about the watchdog re-arm cadence.
- Understand the cross-pod tracker fragmentation.

## Design sketch

### Component 1 — Framework-owned `SagaItemAggregateStore`

Whizbang.Sagas already owns the `SagaItemModel` schema (it ships the
projection). Add a framework-internal repository / lens query that
reads `wh_per_saga_item` to compute `(Completed, Failed, InProgress,
Total)` for a given `sagaId`. Implement the two-tier pattern (cheap
aggregate → event-store reconciliation for non-terminal rows whose
per-item stream carries a terminal event).

Constraints:

- AOT-clean: same source-gen patterns as the rest of `Whizbang.Sagas`.
- Configurable via `SagaOptions`: schema name override, projection
  table name override.
- Exposes `Task<SagaItemAggregate> GetAggregateAsync(Guid sagaId, CancellationToken ct)`.

### Component 2 — Replace `LoadProjectionAsync` with framework state

`BaseSagaService.TryRecoverViaWatchdogAsync` calls `LoadProjectionAsync`
today to source authoritative counts. Replace that with a call to
Component 1's `SagaItemAggregateStore.GetAggregateAsync(sagaId)`.

The consumer's `LoadProjectionAsync` override becomes optional — it's
only needed when the consumer's saga has additional state the
framework can't infer from `wh_per_saga_item` alone. For the
**completion decision specifically**, the framework reads its own
per-item aggregate.

Backward-compat: consumers that already override `LoadProjectionAsync`
continue to work; the framework just no longer relies on the override
for the completion math.

### Component 3 — Framework-generated per-item recovery receptors

Add a generator (`Whizbang.Sagas.Generators` already exists) that emits
the per-item terminal receptors for each `BaseSagaService<T1..T9>`
subclass it sees. The emitted receptors:

- Filter by `event.SagaName == _sagaService.SagaName`.
- Call `_sagaService.TryRecoverViaWatchdogAsync(ctx, ct)`.
- Are decorated with `[FireAt(LifecycleStage.PostAllPerspectivesInline)]`.

Consumer code loses the boilerplate handler pair entirely. New
consumers never write it. The pattern is implementation detail of
`[Saga<TBase>("Name")]` (or the equivalent registration).

### Component 4 — Watchdog re-arm

Add explicit re-arm logic to `BaseSagaService.TryRecoverViaWatchdogAsync`
or to a wrapper receptor the framework registers for
`SagaCompletionWatchdogTickEvent`:

```csharp
if (!recovered) {
  var backoff = _watchdogBackoffSchedule[Math.Min(@event.RescheduleCount, _watchdogBackoffSchedule.Length - 1)];
  if (@event.RescheduleCount + 1 >= _maxRescheduleCount) {
    await _emitter.PublishAsync(new SagaCompletionAbandonedEvent { ... });
    return;
  }
  var next = new SagaCompletionWatchdogTickEvent {
    StreamId = ctx.SagaId,
    SagaName = ctx.SagaName,
    EntityId = ctx.EntityId,
    RescheduleCount = @event.RescheduleCount + 1,
  };
  await _emitter.PublishAsync(next, DateTimeOffset.UtcNow + backoff);
}
```

Default schedule (matches the docstring): `30s, 2m, 8m, 30m, abandon`.
Configurable via `SagaOptions.WatchdogBackoff`.

Even with Component 3 in place (event-driven recovery on every terminal
item), the watchdog re-arm is still load-bearing — it's the safety net
for "lost work" cases where per-item events never made it to the
receptor (transport drop, projection-only writes during replay, etc.).
The combination is correct: event-driven recovery on the happy path,
watchdog as the floor.

## Constraints (non-negotiable)

1. **AOT-clean** — Whizbang.Generators emits receptors at compile
   time; no runtime reflection in any of the four components.
2. **Backwards-compatible** — consumers that already override
   `LoadProjectionAsync` or register per-item receptors keep working.
   New code paths kick in only for the framework's own logic.
3. **No silent behavior change** — `SagaCompletionAbandonedEvent` must
   be a new event consumers opt into reacting to; the framework can't
   change the meaning of `SagaCompletedEvent` or invent new states
   without a release-note callout.
4. **Tests RED-first** — each of the four leaks has a reproducible
   failure mode (see Verification); lock each fix with a RED test
   against the broken behavior before adding the framework code.

## Out of scope (first cut)

- **Reconciliation against the durable event store on every recovery
  call** — Component 1 uses the per-item aggregate; the slow
  event-store reconciliation tier should only kick in when projection
  rows are non-terminal but their per-item streams have terminal events
  durable (the cross-pod lost-update strand case). It's the uncommon
  case — the common path is the cheap aggregate.

- **Replacing the in-memory completion tracker** — it's still useful as
  a same-pod fast path for low-fan-out sagas (a single pod handling all
  N items in-process). Component 3 + Component 4 give cross-pod
  correctness; the fast path stays as an optimization.

## Critical files to modify

- `src/Whizbang.Sagas/Services/BaseSagaService.cs` — gut and replace
  `TryRecoverViaWatchdogAsync`'s `LoadProjectionAsync` call with the
  framework's own aggregate read; add watchdog re-arm.
- `src/Whizbang.Sagas/Services/SagaItemAggregateStore.cs` *(new)* — the
  framework-owned per-item aggregate.
- `src/Whizbang.Sagas/SagaOptions.cs` — add `WatchdogBackoff` schedule.
- `src/Whizbang.Sagas.Generators/SagaItemRecoveryReceptorGenerator.cs`
  *(new)* — emits per-item terminal receptors per `BaseSagaService`
  subclass.
- `src/Whizbang.Sagas/SagaCompletionAbandonedEvent.cs` *(new)* — emitted
  by Component 4 when `RescheduleCount` exhausts.
- `tests/Whizbang.Sagas.Tests/Services/CrossPodCompletionTests.cs` *(new)*
  — reproduces the four failure modes (RED-first) and locks each fix.

## Verification

### Per-component RED tests

1. **Component 1**:
   `SagaItemAggregateStore_ResolvesAuthoritativeCounts_BypassingStaleSagaProjectionAsync`
   — seed `wh_per_saga_item` with all-terminal rows; assert the
   aggregate returns the per-item-derived counts independent of any
   consumer projection state.

2. **Component 2**:
   `TryRecoverViaWatchdogAsync_UsesFrameworkAggregate_NotLoadProjectionAsync_Async`
   — consumer overrides `LoadProjectionAsync` to return stale 0/N; the
   framework still completes the saga because it reads its own
   aggregate.

3. **Component 3**: `SagaItemTerminalEvent_AutoTriggersRecovery_Async`
   — emit a `SagaItemCompletedEvent` for the saga's last item; assert
   `SagaCompletedEvent` is published without the consumer registering
   any receptor.

4. **Component 4**:
   `Watchdog_ReArmsWithBackoff_OnTryRecoverFalseAsync` — fire a tick at
   T+0 before items finish; assert a second tick is scheduled at
   T+30s, third at T+2m30s, etc., and `SagaCompletionAbandonedEvent`
   fires after `RescheduleCount` exhausts.

### End-to-end (after all four ship)

- `Saga_CrossPodFanOut_CompletesViaFrameworkAlone_Async` — multi-pod
  test (3 pods, 100 items distributed) using TestContainers Postgres +
  InMemory transport. Consumer registers zero completion-related code;
  saga still terminates correctly.

- `Saga_ConsumerCanOverrideLoadProjectionAsync_ForExtraState_Async` —
  consumer overrides `LoadProjectionAsync` for a custom field (e.g., a
  hooks list); completion still uses the framework aggregate (not the
  override's `CompletedItems`).
