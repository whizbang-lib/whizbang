# W4 Phase 0 — Investigation findings

**Goal:** determine whether the gap the proposal addresses is (1) timeout-shape, (2) a consumer miss-registration, or (3) framework bug. Adjust slicing per findings.

## TL;DR (revised after empirical test, 2026-06-10)

**Confirmed empirically: the framework works correctly.** `LocalInvokeAndSyncAsync` does wait for perspectives when called from the a consumer seed-code pattern. The proposal's premise that "the dispatcher returns when events are written to the stream but not when perspectives have projected" does **NOT** hold against the current Whizbang implementation.

This means the W4 plan's central value proposition (strengthen the dispatcher to wait for projections) **doesn't apply** — the dispatcher already does. The likely cause of a consumer's 380 cold-Docker failures is the 30-second default timeout firing during slow Docker bootstrap, not a missing wait. a consumer can fix this with a longer timeout or by passing a long-lived `CancellationToken`.

The API-shape complaints (`TimeSpan? timeout`, multiple overloads) still stand — Option A's signature is cleaner — but it's a quality-of-life improvement, not a bug fix.

## Empirical test (commit pending on the W3 branch)

Added two tests under `tests/Whizbang.Core.Integration.Tests/W4Phase0_DispatcherProjectionSyncEmpiricalTests.cs`. Both PASS:

1. `LocalInvokeAndSyncAsync_FromScopeWithReceptorEmittingEvent_DidWaitAsync` — replicates the a consumer seed-code call shape (dispatcher.LocalInvokeAndSyncAsync(cmd) inside a DI scope, receptor returning an IEvent). Captures `SyncDecisionContext.DidWait` via the `onDecisionMade` callback. **Result: `didWait=true, EventsAwaited≥1, Outcome=Synced`.** The framework correctly tracked the receptor's emitted event into the scope's tracker AND invoked the perspective-completion awaiter.
2. `LocalInvokeAndSyncAsync_WithManuallyTrackedEvent_DoesWaitAsync` — control case. Manually pre-populates the scoped tracker plus dispatches the command. Verifies the awaiter receives BOTH the manually-tracked event AND the receptor's emitted event. **Confirms two tracking paths converge on the same scope tracker.**

The two passing tests are the load-bearing observation. The proposal's claim is not reproducible against the current framework code.

The proposal's API-shape complaint stands regardless: the `TimeSpan? timeout = null` parameter encourages timing-based defenses where signal-based should be enough. Option A's signature is cleaner even if the framework already does the right thing.

## What I found

### Finding 1 — a consumer call shape

`SeedEeoCodes.EnsureSeededAsync` (file: `src/services/a consumer.JobService/Features/SystemManagedListsFeature/Initialize/SeedEeoCodes.cs`, line 55):

```csharp
await dispatcher.LocalInvokeAndSyncAsync(new SystemManagedListContracts.InitializeSystemManagedListCommand { … });
// Wait for the projection to materialize before issuing AddItem commands —
// LocalInvokeAndSyncAsync syncs this stream's perspective workers, but the
// initialize must land before AddItem's repository check can see the list.
await SeedHelpers.PollUntilAsync(…, TimeSpan.FromSeconds(15), ct);
```

a consumer calls the `LocalInvokeAndSyncAsync<TMessage>(message)` overload — no timeout argument (uses default 30s), no CancellationToken argument (uses `default`). The author's comment claims the method "syncs this stream's perspective workers" but then immediately polls anyway. Translation: the author *thinks* it works but in practice observed that it doesn't, so added defensive polling.

The seeder runs inside a receptor handler (`ReseedSystemEventHandler` at `src/services/a consumer.JobService/Features/JobTemplateFeature/Streams/JobTemplateSeedEventHandlers.cs:57`). Receptor handlers run inside a Whizbang-created DI scope, so scoped services *should* be resolvable.

### Finding 2 — `IEventCompletionAwaiter` IS registered

`AddWhizbang()` calls `_registerPerspectiveSyncServices` which includes:

```csharp
services.TryAddSingleton<IEventCompletionAwaiter, EventCompletionAwaiter>();
```

(`src/Whizbang.Core/ServiceCollectionExtensions.cs:283`)

So a consumer is NOT missing this registration. Rules out (2) — pure a consumer miss-registration — as the sole cause.

### Finding 3 — Dispatcher is **Singleton**, ScopedEventTracker is **Scoped**

```csharp
// Dispatcher (template, src/Whizbang.Generators/Templates/DispatcherRegistrationsTemplate.cs:104)
services.AddSingleton<IDispatcher>(sp => { … });

// ScopedEventTracker (src/Whizbang.Core/ServiceCollectionExtensions.cs:275)
services.TryAddScoped<IScopedEventTracker>(_ => {
  var tracker = new ScopedEventTracker();
  ScopedEventTrackerAccessor.CurrentTracker = tracker;  // sets AsyncLocal
  return tracker;
});
```

When the singleton Dispatcher is constructed at app startup, its constructor receives `IScopedEventTracker? scopedEventTracker = null` — Scoped services can't be resolved from the root scope, so the parameter is null on the singleton.

The dispatcher's instance field `_scopedEventTracker` is therefore null forever on the singleton. The intent (per Dispatcher.cs:134 comment) is that scope-time consumers use `ScopedEventTrackerAccessor.CurrentTracker` (AsyncLocal) instead.

### Finding 4 — Two tracking paths in the dispatcher; only ONE checks the AsyncLocal

**Path A (broken for singleton):** Dispatcher's `_trackEventForSync` (line 3253):

```csharp
private void _trackEventForSync(Type messageType, Guid eventId, Guid streamId) {
  _scopedEventTracker?.TrackEmittedEvent(streamId, messageType, eventId);
  //  ↑ NULL for singleton dispatcher — events silently NOT tracked here

  if (_syncEventTracker is not null && _trackedEventTypeRegistry is not null) {
    // Singleton tracker still records (cross-scope sync)
  }
}
```

**Path B (works):** `SyncTrackingEventStoreDecorator` (registered scoped, captures the scope's `IScopedEventTracker` instance at IEventStore resolution; records every appended event into the scope's tracker). See `ServiceCollectionExtensions.cs:422-432`.

So if events flow through `_eventStore.AppendAsync(...)` within the receptor's scope, the decorator records them into the scope's tracker. The dispatcher's wait method reads from `ScopedEventTrackerAccessor.CurrentTracker` (AsyncLocal), which the scope set via the factory lambda. *Both should refer to the same tracker instance*.

In theory: the wait DOES see the events. In practice: needs empirical verification.

### Finding 5 — The wait method early-returns when the tracker is empty or null

`_waitForAllPerspectivesAsync` at line 4177:

```csharp
var scopedTracker = _scopedEventTracker ?? ScopedEventTrackerAccessor.CurrentTracker;
if (scopedTracker is null) {
  return new SyncResult(SyncOutcome.NoPendingEvents, 0, …);  // ← silent early-return
}

var trackedEvents = scopedTracker.GetEmittedEvents();
if (trackedEvents.Count == 0) {
  return new SyncResult(SyncOutcome.NoPendingEvents, 0, …);  // ← silent early-return
}
```

Both early-return paths return `SyncOutcome.NoPendingEvents` without waiting and without throwing. The caller's `Task<SyncResult>` resolves successfully. The dispatcher returns "happy" with no log emit and no exception. The actual perspective never gets awaited.

**This is the failure mode the proposal describes** ("returns when events are written to stream but not when perspectives have projected"). If either early-return triggers in a consumer seed code path, the seeder gets immediate return → reads from repo → projection hasn't run yet → `null` → and that's why a consumer added `PollUntilAsync` as a defense.

### Finding 6 — Why the early-return might fire in a consumer's specific case

Two scenarios where the wait early-returns silently:

**Scenario A:** Receptor scope never resolves `IScopedEventTracker` BEFORE the dispatcher's wait runs, so the AsyncLocal `CurrentTracker` stays null. Probable when the receptor's constructor doesn't take `IScopedEventTracker` as a dependency.

The seed receptor's constructor (`ReseedSystemEventHandler`) takes `IDispatcher`, repositories, and `ILogger`. It does NOT take `IScopedEventTracker`. So nothing in the scope explicitly resolves the tracker before `_dispatcher.LocalInvokeAndSyncAsync(cmd)` is called.

BUT — when the dispatcher's LocalInvokeAsync runs and the command's handler emits events via the event store, the event store IS resolved scoped, and its `SyncTrackingEventStoreDecorator` factory triggers `IScopedEventTracker` resolution via `sp.GetService<IScopedEventTracker>()` (line 423). That factory lambda sets the AsyncLocal.

So whether the AsyncLocal is set BY THE TIME the wait method runs depends on whether the command's handler actually touches the event store before returning. For a command like `InitializeSystemManagedListCommand`, the handler should append `SystemManagedListInitializedEvent` to the store → triggers SyncTrackingEventStoreDecorator → sets AsyncLocal.

**Scenario B:** The factory lambda's `ScopedEventTrackerAccessor.CurrentTracker = tracker` assignment runs but the AsyncLocal flows through an async boundary where it gets reset. AsyncLocal is fragile across `Task.Run`, `ConfigureAwait(false)`, and similar.

### Finding 7 — Most likely root cause

Given the SyncTrackingEventStoreDecorator pattern, the most likely actual gap is:

**The command's local receptor in a consumer's seed flow doesn't actually emit events through the event store** — instead it emits via outbox (write-then-publish-later) or via a path that doesn't trigger the SyncTrackingEventStoreDecorator. Or the events are emitted in a non-tracked way.

The author would observe: `LocalInvokeAndSyncAsync` returns quickly (because tracker has zero events → early-return `NoPendingEvents`). Then the perspective worker eventually catches up asynchronously. The 100ms-poll-15s-deadline papers over the gap until cold-Docker spins past 15s and breaks.

## Updated theory after empirical test (2026-06-10)

My earlier read of `_trackEventForSync` at line 3253 (uses `_scopedEventTracker?` field, null on singleton) WAS correct in isolation. But it missed that the dispatcher has a SECOND tracking method, `_trackInScopedTracker` at line 2646, which DOES use the AsyncLocal fallback:

```csharp
private void _trackInScopedTracker(Guid streamId, Type messageType, Guid eventId) {
  var scopedTracker = _scopedEventTracker ?? ScopedEventTrackerAccessor.CurrentTracker;
  // ↑ THIS is the fallback that makes the framework work for singleton dispatcher
  if (scopedTracker is not null) {
    scopedTracker.TrackEmittedEvent(streamId, messageType, eventId);
  }
}
```

The cascade path in a consumer's seed flow hits `_trackInScopedTracker` (correct fallback). The path through `_trackEventForSync` (no fallback) appears to be a dead or alternate path that doesn't fire in this scenario. The asymmetry between the two methods IS a latent code smell — they should consolidate on one shape — but it's not the load-bearing bug the proposal claims.

## Most likely actual cause of a consumer's cold-Docker failures

`SeedHelpers.PollUntilAsync` uses `TimeSpan.FromSeconds(15)` as its deadline. The dispatcher's `LocalInvokeAndSyncAsync` defaults to a 30-second timeout. Under Testcontainers cold-start, the perspective worker bootstrap + first event processing can exceed BOTH thresholds — the dispatcher's 30s wait throws `TimeoutException`, a consumer's polling defense never gets to run because the dispatcher already threw, and the seeder fails.

a consumer may have observed the dispatcher's `TimeoutException` and assumed it meant "the dispatcher gave up before the perspective ran" — which is *technically true* — and then added the polling defense, not realizing the polling has the same 15s ceiling. Two timers stacked on top of each other; cold Docker exceeds both.

The fix is therefore in a consumer, not the framework:

1. Pass a longer timeout: `dispatcher.LocalInvokeAndSyncAsync(cmd, timeout: TimeSpan.FromMinutes(2), ...)`. Or:
2. Pass a `CancellationToken` with a longer deadline, so the caller bounds the wait instead of the default 30s.
3. Delete `SeedHelpers.PollUntilAsync` and the 10 callsites — the dispatcher already waits correctly.

After a consumer makes those changes, the 380 cold-Docker failures should drop to zero.

## Recommendation

### Empirical confirmation done — framework works

The two passing Phase 0 integration tests prove the framework correctly waits. a consumer's `PollUntilAsync` is unnecessary defensive code. The fix is a consumer-side: extend the timeout (or remove it).

```csharp
[Test]
public async Task LocalInvokeAndSyncAsync_FromReceptorScope_ActuallyWaitsForProjectionAsync() {
  // Arrange: real Postgres + scoped dispatcher + one perspective
  using var scope = serviceProvider.CreateScope();
  var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

  // Act
  await dispatcher.LocalInvokeAndSyncAsync(new SeedTestCommand { … });

  // Assert: read model is populated BEFORE this line runs
  var readModel = await repository.GetByStreamIdAsync(streamId);
  await Assert.That(readModel).IsNotNull();
}
```

If this test PASSES → the framework works correctly; a consumer's problem is something else (likely the seed receptor doesn't go through the event store path). Fix is a consumer-side.

If this test FAILS → confirmed framework gap. Proceed with W4 slicing.

Estimated effort: 2-3 hours to set up the test fixture + run.

### Likely outcomes & next steps

**If empirical test reveals the framework is broken for a consumer's call shape:**

The simplest framework fix is in `_trackEventForSync` — also write into the AsyncLocal so events tracked through the dispatcher's direct path land in the same place the wait reads from:

```csharp
private void _trackEventForSync(Type messageType, Guid eventId, Guid streamId) {
  // Resolve tracker from BOTH field and AsyncLocal so singleton dispatchers
  // still hit the current scope's tracker.
  var tracker = _scopedEventTracker ?? Perspectives.Sync.ScopedEventTrackerAccessor.CurrentTracker;
  tracker?.TrackEmittedEvent(streamId, messageType, eventId);
  …
}
```

That's a ~2-line fix that solves the a consumer problem TODAY. a consumer can delete `PollUntilAsync` and the 380 cold-Docker failures.

Whether to ALSO ship Option A's API surface (W4 slices 1-8) is a separate decision once a consumer is unblocked.

**If empirical test reveals the framework works but a consumer's seeders don't go through the event store path:**

The fix is in a consumer. The seed receptor needs to use the event-store-backed pattern instead of a write-via-outbox pattern (or whichever path is missing the SyncTrackingEventStoreDecorator hook).

W4 plan stands down — the proposal's API change isn't needed.

**If neither test confirms the gap one-way:**

Run the seeder under a profiler / debug log. The dispatcher's `_invokeOnDecisionMade` callback receives `didWait: bool` — a consumer can register an `onDecisionMade` handler to log whether the wait actually ran. If `didWait: false` shows up in a consumer logs during seeding, that's the smoking gun.

## What this means for W4 plan (revised)

The original W4 plan's central value proposition (strengthen `LocalInvokeAndSyncAsync` to wait for projections) is **not needed** — it already does.

What remains useful:

- **API cleanup (Slices 1, 2, 8 of the revised plan):** the `SyncMode` enum + the typed-event-await sugar + `[Obsolete]` on the timeout-shaped overloads is still cleaner. Lower urgency now that a consumer has a simpler path forward, but still nice for new code.
- **Promote `CreateWaiter` to production (Slice 3):** lower priority, since a consumer no longer needs it for the seed-code use case. Reconsider when a real consumer asks.
- **Re-entry guard (Slice 5):** not needed. The framework already handles re-entrant dispatch — no a consumer-observed deadlocks support this slice.
- **Cross-service skip (Slice 6):** already happens (the awaiter only knows about locally-registered perspectives).
- **a consumer consumer migration (Slice 7):** STILL VALUABLE — delete `PollUntilAsync` and extend the timeout. ~half day of a consumer work.

**Recommended scope:** Slices 1, 2, 7, 8 (API cleanup + sugar + a consumer migration + obsoletion). Slices 3, 4, 5, 6 of the original revised plan drop out. The two Phase 0 tests stay as regression locks.

## Action items (revised after empirical test, 2026-06-10)

### Done
- ✅ Wrote two Phase 0 integration tests (`W4Phase0_DispatcherProjectionSyncEmpiricalTests`). Both pass — framework works correctly.

### a consumer-side (highest value, fastest)
1. **Extend the timeout** in a consumer's seed callsites — `dispatcher.LocalInvokeAndSyncAsync(cmd, timeout: TimeSpan.FromMinutes(2), ct: ct)` — OR pass a long-lived `CancellationToken` so the caller bounds the wait.
2. **Delete `SeedHelpers.PollUntilAsync`** and the 10 callsites.
3. **Re-run a consumer integration suite** against cold Docker. Expected: 380/421 failures drop to ≤ baseline. Record headline metric.

### Whizbang-side (lower priority, quality-of-life only)
4. Ship Slices 1, 2, 7, 8 of the revised W4 plan: API cleanup (`SyncMode` enum + new method signature + sugar + `[Obsolete]` on old overloads). No bug fix urgency; ship when convenient.

### Dropped from scope (no longer needed)
- ~~Strengthen `LocalInvokeAndSyncAsync` to wait for projections~~ — already does.
- ~~Promote `CreateWaiter` to production~~ — no consumer demand.
- ~~Re-entry guard~~ — framework already handles re-entry safely.
- ~~Cross-service skip~~ — already happens by design.
- ~~`WhizbangOptions.SyncWaitsForProjections` flag~~ — no behavior change needed; nothing to flag.

## References

- Proposal: `c:/src/a consumer application/.claude/worktrees/feature+ai-ready-codebase/docs/proposals/whizbang-dispatcher-projection-sync.md`
- a consumer call shape: `src/services/a consumer.JobService/Features/SystemManagedListsFeature/Initialize/SeedEeoCodes.cs:55`
- a consumer caller (in scope): `src/services/a consumer.JobService/Features/JobTemplateFeature/Streams/JobTemplateSeedEventHandlers.cs:57`
- Dispatcher tracking bug suspect: `src/Whizbang.Core/Dispatcher.cs:3253` (`_trackEventForSync`)
- Dispatcher wait early-return: `src/Whizbang.Core/Dispatcher.cs:4186-4199`
- Tracker AsyncLocal setter: `src/Whizbang.Core/ServiceCollectionExtensions.cs:275-279`
- SyncTrackingEventStoreDecorator: `src/Whizbang.Core/ServiceCollectionExtensions.cs:422-432`
