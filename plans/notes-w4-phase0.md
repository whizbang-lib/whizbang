# W4 Phase 0 — Investigation findings

## TL;DR

**Confirmed empirically: the framework works correctly.** `IDispatcher.LocalInvokeAndSyncAsync` does wait for perspectives when called from a real receptor-emits-event flow inside a DI scope. The W4 proposal's premise that "the dispatcher returns when events are written to the stream but not when perspectives have projected" does **NOT** hold against the current Whizbang implementation.

This means the W4 plan's central value proposition (strengthen the dispatcher to wait for projections) **doesn't apply** — the dispatcher already does. The remaining value is API hygiene: replace the `TimeSpan? timeout = null` shape with a CT-only, `SyncMode`-parameterized contract.

## Empirical test (in this repo)

`tests/Whizbang.Core.Integration.Tests/W4Phase0_DispatcherProjectionSyncEmpiricalTests.cs` — two tests, both pass:

1. `LocalInvokeAndSyncAsync_FromScopeWithReceptorEmittingEvent_DidWaitAsync` — replicates the canonical call shape (dispatcher inside a DI scope, receptor returns an IEvent). Captures `SyncDecisionContext.DidWait` via the `onDecisionMade` callback. **Result: `didWait=true, EventsAwaited≥1, Outcome=Synced`.** The framework correctly tracked the receptor's emitted event into the scope's tracker AND invoked the perspective-completion awaiter.
2. `LocalInvokeAndSyncAsync_WithManuallyTrackedEvent_DoesWaitAsync` — control case. Manually pre-populates the scoped tracker plus dispatches the command. Verifies the awaiter receives BOTH the manually-tracked event AND the receptor's emitted event. Confirms two tracking paths converge on the same scope tracker.

These tests stay as permanent regression-locks against a future refactor silently breaking the wait behavior.

## Architectural notes (for future refactors)

The dispatcher has TWO tracking paths and only ONE of them uses the AsyncLocal fallback:

- **`Dispatcher._trackEventForSync` (~line 3253):** uses the `_scopedEventTracker` instance field. That field is **null on the singleton-registered dispatcher** (Scoped services can't be resolved from the root scope). So this path silently no-ops for the singleton case.
- **`Dispatcher._trackInScopedTracker` (~line 2646):** uses `_scopedEventTracker ?? ScopedEventTrackerAccessor.CurrentTracker`. This path is the one that actually fires for the cascade flow.

The asymmetry is a latent code smell — both methods should consolidate on the AsyncLocal-fallback pattern. But it doesn't break behavior today because the cascade flow (command → receptor → event) goes through `_trackInScopedTracker` (the correct path).

Wait path: `_waitForAllPerspectivesAsync` (~line 4177) reads via `ScopedEventTrackerAccessor.CurrentTracker` (the AsyncLocal) and silently early-returns `SyncOutcome.NoPendingEvents` if the tracker is null or empty. The early-return is the failure mode the proposal feared — but it doesn't fire on the cascade path the tests exercise.

## What this does to the W4 plan

The plan's central claim (strengthen the dispatcher to wait for projections) is **no longer needed** — it already does. What survives:

- **Slice 1 — `SyncMode` enum + new method shape.** Still ships. CT-only, no `TimeSpan`. Read-after-write expectation explicit at the callsite.
- **Slice 8 — `[Obsolete]` on the timeout-shaped overloads.** Still ships. Points consumers at the new API.
- **Slice 7 — Consumer-side migration.** Belongs in the consumer's repo, not Whizbang. (Migration doc lives outside this repo; consumer agent owns the work.)

Dropped from scope (framework already handles these correctly):
- Promote `CreateWaiter` to production
- Perspective-completion signal wiring (already wired)
- Re-entry guard (already handled)
- Cross-service projection skip (already by design)
- `WhizbangOptions.SyncWaitsForProjections` flag (no behavior change needed)

## References

- New API: `Whizbang.Core.IDispatcher.LocalInvokeAndSyncAsync<TMessage>(TMessage, SyncMode, CancellationToken)`
- New enum: `Whizbang.Core.Perspectives.Sync.SyncMode`
- Existing dispatcher impl: `src/Whizbang.Core/Dispatcher.cs`
- Awaiter wiring: `src/Whizbang.Core/ServiceCollectionExtensions.cs:_registerPerspectiveSyncServices`
