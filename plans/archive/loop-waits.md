# Loop waits: nothing left behind, by construction

Status: **done** in #2617 (implemented on `design/loop-waits`, archived). Follows #2613 / PR #2614.

## Outcome (#2617)

The maintainer's decisions, as implemented:

1. `WakeSet` and `WakeSignal` are public, with `<docs>` and `<tests>` tags.
2. The verification kit stays in `Whizbang.Testing` (not packable); shipping it to applications is deferred.
3. No accepted shape and no suppression: every site in section 4 moved onto `WakeSet`, including the batching windows
   (one set per window), the perspective sync wait, the heartbeat tick, the lease dispatch race, the crash-exit bound,
   the startup probe and stage gate, and the work coordinator gate, whose permit race uses the new semaphore source
   (`Add(SemaphoreSlim)`, `PermitWakeSource.TryTake`) with exact hand-back. `CompositeBatches` needed no new source
   kind: its pending read is a one-shot task source on a per-window set, and the read stays pending for the next window.
4. WHIZ612 treats a task it cannot place as compute; gate waits are marked `[WaitSource]`.
5. WHIZ611 is folded into WHIZ612 and retired (one rule; its acceptance of a canceled per-race token is gone).
6. `PendingChannelWait<T>`, `AwaitConsumerWakeAsync` and #2614's test-local `WaitTrackingReader` are removed; the tests
   use the kit's `CountingChannelReader`.
7. The Azure Service Bus send timeout is `sendTask.WaitAsync(SendTimeout, timeProvider, ct)`.
8. `BatchBuffer` has no race to move: its wait is a completion source the release completes, canceled through a
   registration that admission disposes (locked by `BatchBufferTests.AWaitingAppend_OnceAdmitted_IsNotKeptByItsTokenAsync`).

The sections below are the design as written before the decisions.

## Why

A long-running loop that races fresh waits in `Task.WhenAny` every pass abandons the losers. Where a loser is held
decides whether that is a leak:

| Loser | Held by | Released when |
|---|---|---|
| `ChannelReader.WaitToReadAsync(token)` | the channel's waiting-reader queue | an item is written, or `token` is **canceled** (disposing its source is not enough) |
| `SemaphoreSlim.WaitAsync(token)` | the semaphore's waiter list | a permit is released to it (it then holds the permit), its timeout passes, or `token` is canceled |
| `TaskCompletionSource.Task`, `WakeSignal.WaitAsync` | nothing new, if the same task is reused; `WhenAny` removes its continuation from losers | completion |
| `Task.Delay(d, time, token)` | the timer queue, and a registration on `token` | `d` passes, or `token` is canceled |
| `token.Register(...)` | `token`'s source | the registration is disposed, or the source is canceled or collected |

On the host's stopping token "canceled" means never. #2613 measured the first row: 630,082 abandoned channel waits
after 21.5 h on an idle instance (about 8 MiB/h). #728 was the same bug for the semaphore wake. Each was fixed with a
local tool (`WakeSignal`, `PendingChannelWait<T>`), and each loop still has to know to use it.

This plan makes the correct loop the easy one: one primitive (`WakeSet`) that owns the race, one analyzer that reports a
hand-written race over waits, and one verification kit in `Whizbang.Testing` that proves a loop holds a bounded number
of waits.

## 1. Audit (src/, all projects)

Method: every `Task.WhenAny` (19), every `SemaphoreSlim.WaitAsync` with a timeout or raced (134 calls in all, one raced),
every `CancellationToken.Register`/`UnsafeRegister` on a token (11 of the 82 `Register` hits; the rest are registries),
every `CreateLinkedTokenSource` (34), every `Task.Delay` (125) and `ContinueWith` (19), every `+=` subscription between
objects of different lifetimes, and every timer. Each LEAK has a RED test; each BOUNDED or SAFE row whose
classification rests on code of ours (a cancel, a reuse, a dispose) has a test that fails when that code is removed,
verified by deleting it on the build server (section 1.3). Rows that rest only on the runtime (a finite delay fires, a
`using` disposes) say so.

### 1.1 Table, worst first

Rate: how often a new loser is made. Class: LEAK (grows without bound on an idle or steady instance), BOUNDED (released
within a fixed time), SAFE (released when the race is decided, or nothing is left).

| # | Site (develop) | Left behind, held where | Rate | Class | Evidence / test |
|---|---|---|---|---|---|
| 1 | `PerspectiveWorker._runChannelConsumerLoopAsync`, `Workers/PerspectiveWorker.cs:750-753` | 2 channel waits per pass (`WaitingReadAsyncOperation` + `AsTask` + `CallbackNode` on the stopping token), queued in the work and drain channels | every idle pass, per consumer (timeout 1 s, plus every signal wake) | **LEAK** | RED: `PerspectiveWorkerInjectedClockTests.IdlePasses_*HoldAtMostOneWaitOnEachChannel*` (2, from #2614's RED commit) and `IdlePasses_HeldToAWaitBudget_KeepAtMostOneWaitOnEachChannelAsync` (new, uses the `Whizbang.Testing` kit). Fixed by #2614. |
| 2 | `AzureServiceBusTransport.PublishAsync`, `Transports.AzureServiceBus/AzureServiceBusTransport.cs:784` | the `SendTimeout` delay (30 s default) when the send wins: a timer plus a registration on the caller's token | **every published message** | BOUNDED (30 s) | Runtime only (the delay fires). Not countable: the delay uses the system clock, not an injected `TimeProvider`. Steady state is 30 s x publish rate live timers. |
| 3 | consumer-loop idle timeout, `PerspectiveWorker.cs:762` | the idle delay when a channel or the signal wins: a timer plus a stopping-token registration | every busy pass | BOUNDED (`PollingIntervalMilliseconds`, 1 s) | Runtime only. Removed by the `WakeSet` migration (one re-armed timer). |
| 4 | `DeadLetterRecoveryWorker` scan loop, `Workers/DeadLetterRecoveryWorker.cs:246` | the backstop delay (10 min) when a NOTIFY wake wins | every dead-letter wake | BOUNDED (10 min) | Runtime only for the delay; the signal's single waiter is locked by `DeadLetterRecoveryWorkerTests.NotificationListener_ManyBackstopTimeouts_HoldAtMostOneWakeWaiterAsync`. A dead-letter storm holds wake rate x 600 delays. |
| 5 | `DutyHolderWorker.ExecuteAsync`, `Startup/DutyHolderWorker.cs:206` | the renew delay (5 s) when a release wake wins; a fresh TCS per pass (dropped, nothing registered) | every role-release wake | BOUNDED (5 s) | Runtime only. |
| 6 | `WorkCoordinatorGate._acquireInteractiveAsync`, `Messaging/WorkCoordinatorGate.cs:137-148` | the losing semaphore wait (shared or reserve), queued in that semaphore | every contended interactive acquire | BOUNDED (`AcquireTimeoutMilliseconds`, 30 s; with no deadline, until the next permit, which it hands back) | Hand-back locked by `WorkCoordinatorGateInteractiveReserveTests` (`...TakesTheSharedPermitThatFreesFirst`, `...TakesTheReserveWhenItFreesFirst`); deadline by `...PastTheDeadline_ProceedsWithoutASlot...`. This is a permit race, not a wake: see decision 3. |
| 7 | `StartupReadiness._narratedWaitAsync`, `Startup/StartupReadiness.cs:176` | one probe delay when the wait wins | once per startup | BOUNDED (probe interval) | One-shot. |
| 8 | `CapturingWorkCoordinator.WaitForInboxAsync`, `Testing/MultiService/CapturingWorkCoordinator.cs:84` | the deadline delay (no token) | per test call | BOUNDED (the timeout) | Test library. |
| 9 | `WhizbangInstanceExit.ExitOnCrash`, `RunControl/WhizbangInstanceExit.cs:176` | the crash budget delay | once, as the process dies | BOUNDED | One-shot. |
| 10 | `PerStreamSerializer._drainStreamAsync` window, `Workers/PerStreamSerializer.cs:208-212` | nothing: the per-window source is canceled once the race is decided | per drain window | SAFE | NEW `RaceLoserReleaseTests.PerStreamSerializerDrainWindow_EndedByArrivals_HoldsOnlyTheOpenWindowsDeadlineAsync`. |
| 11 | `CompositeBatches._arrivesWithinAsync`, `Minting/CompositeBatches.cs:316-319` | nothing: the window's timer is canceled, the pending read is reused across windows | per batch window | SAFE | NEW `RaceLoserReleaseTests.CompositeBatchWindow_EndedByArrivals_HoldsOnlyTheOpenWindowsDeadlineAsync`. |
| 12 | `PerspectiveSyncAwaiter` applied wait, `Perspectives/Sync/PerspectiveSyncAwaiter.cs:330-361, 401, 408` | nothing: re-read delays and the in-process (perspective and sink) waiters are released by the `finally` cancel | per sync wait (request) | SAFE | NEW `PerspectiveSyncAwaiterAppliedTests.WaitForApplied_WonByALocalApply_LeavesNoDelayOnTheClockAndNoWaiterOnTheTrackerAsync`, `...SettledByTheLedger_LeavesNoWaiterOnTheTrackerAsync`. |
| 13 | `HeartbeatWorker.WaitForNextTickAsync`, `Workers/HeartbeatWorker.cs:287-290` | nothing: `either` is canceled | per beat while the schema gate is closed | SAFE | NEW `HeartbeatWorkerStartupBeatTests.TheWait_WhileTheGateIsClosed_EndedByTheCadence_ReleasesItsWaitOnTheGateAsync`, `...EndedByTheGate_ReleasesItsCadenceDelayAsync`. |
| 14 | perspective drain window, `PerspectiveWorker.cs:970-978` | nothing: canceled | per drain window | SAFE | `WindowWaitReleaseTests.PerspectiveWorkerDrainWindow_*` (#2614). |
| 15 | `SlidingWindowBatcher.ReadBatchesAsync`, `Workers/SlidingWindowBatcher.cs:110-116` | nothing: canceled | per window | SAFE | `WindowWaitReleaseTests.SlidingWindowBatcher_*` (#2614). |
| 16 | `BatchBuffer.AdmitAsync` registration, `Workers/BatchBuffer.cs:136` | nothing: admission or cancel detaches the registration | per append that waits | SAFE | NEW `BatchBufferTests.AWaitingAppend_OnceAdmitted_IsNotKeptByItsTokenAsync` (GC check with positive control). |
| 17 | `SyncEventTracker.WhenAppliedAsync` registration, `Perspectives/Sync/SyncEventTracker.cs:135` | nothing: removed on cancel; disposed by a continuation when served | per sync wait | SAFE | Cancel path: `SyncEventTrackerAppliedTests` (`AppliedWaiterCount` 1 then 0). Served path: no seam (the continuation runs on the pool); the only caller cancels its token on the way out, locked by row 12. |
| 18 | `WakeSignal.WaitAsync` registration, `Async/WakeSignal.cs:110` | one registration per parked waiter, disposed on `Set` | per waiter (at most one) | SAFE | `WakeSignalTests`. |
| 19 | `LeaseDispatchExecutor.RunWithLeaseAsync`, `Workers/LeaseDispatchExecutor.cs:51-53` | nothing: `await using` disposes the registration; the dispatch it abandons on a lease loss is compute, not a wait | per leased dispatch | SAFE | Compiler-scoped (`await using`); the signal TCS is a local with no seam, so no counting test. |
| 20 | `PerspectiveWorker._clearedBeforeNextPollAsync`, `PerspectiveWorker.cs:1282-1284` | the poll delay is canceled when the set clears; `allCleared` is reused | per startup scan poll | SAFE | Startup only; the cancel ends the loop. |
| 21 | `StartupStageGate._eitherAsync`, `Startup/StartupStageGate.cs:70` | the loser is a never-reset TCS or a `Task.WaitAsync` promise (unregisters on cancel) | once per gated service start | SAFE | One-shot. |
| 22 | `Dispatcher._afterCauseCopyWriteAsync`, `Dispatcher.cs:977` | `WhenAny(single)`: no loser | per dispatch | SAFE | Idiom for "await without observing". |
| 23 | reused never-completing tasks raced every pass (the `WakeSignal` waiter, `drainNever`, an unconfigured signal) | a `WhenAny` continuation per pass, removed from the loser when the race is decided | per pass | SAFE | Runtime (`WhenAny` removes its continuation from every loser). |
| 24 | per-pass linked sources: `ClaimWorker.cs:636` nap, `PgSharedNotifyConnection.cs:622` listen pass, rows 10, 14, 15 | disposed each pass; their waits are awaited to the end or canceled | per pass | SAFE | Disposing a linked source unregisters it from its parents, so the source itself never leaks; what leaks is a *wait on it* that is neither completed nor canceled, which none of these leaves. |
| 25 | `SemaphoreSlim.WaitAsync(timeout, token)` in loops: `ClaimWorker.cs:648`, `PgCommitOrderStamperWorker.cs:325`, `PerspectiveApplyCoordinator.cs:78` | nothing: awaited; a timed-out waiter is removed | per pass | SAFE | Runtime. |
| 26 | `Task.WaitAsync(timeout, ...)` (10 sites: dispatch deferral, health aggregator, signal bus, run controller, outbox publish, ...) | nothing: the promise unregisters and stops its timer on completion | per call | SAFE | Runtime. The correct shape for "one task, with a deadline". |
| 27 | event subscriptions (`+=`, 40 sites) | paired `-=` (PerspectiveWorker, DLQ, IdleActivityTouchHookBinder, PgWorkNotificationListener, Notice handlers, CompositeSubscription, PgCommitOrderStamper), or source and handler of one lifetime (ClaimWorker, poll sources, transports), or a short-lived source holding a short-lived handler (SubscriptionRetryHelper per attempt, ambient transaction) | per construction or per connection | SAFE | Review. |
| 28 | timers (`CreateTimer`, `PeriodicTimer`, 24 sites) | owned by a field or a `using`, disposed with the owner; `_wakeAfterPredecessorHold` replaces and disposes the previous wake per stream | per owner | SAFE | Review. |

Counts: **LEAK 1** (row 1, on develop; 0 once #2614 merges). **BOUNDED 8** (rows 2-9). **SAFE 20**. No `IChangeToken`
or `IOptionsMonitor.OnChange` callbacks and no `IObservable` subscriptions exist in src/.

### 1.2 Impact

- **Row 1 (LEAK).** About 300 bytes per abandoned wait (the operation, its task, its callback node, its slot), 2 per
  pass, about 4 passes a second per consumer on an idle instance: the measured 630,082 waits in 21.5 h, about 190 MB,
  8 MiB/h per idle service, and roughly one extra pod per service per day under a memory autoscaler. RED proven twice.
- **Row 2 (worst BOUNDED).** About 300 bytes per pending delay (the delay promise, its timer, its registration), held
  30 s per message: 1,000 messages/s holds about 30,000 timers, about 9 MB, and every send pays a timer insert and a
  registration on the caller's token for nothing. Fix: `sendTask.WaitAsync(SendTimeout, timeProvider, ct)`, which
  stops its timer when the send wins, with an injected `TimeProvider`.
- **Rows 3-5.** Small (one live delay per wake within the interval), but they are the same hand-written race that
  produced row 1, and the analyzer below would report them.

### 1.3 How the classifications are held

All runs on the build servers (`remote-build`):

- RED for row 1: #2614's two tests fail on develop (40 waits held after 40 passes; more than 3 with three
  consumers), and the kit's budget test fails on pass 1 with two waits outstanding.
- The new lock tests pass on develop: `RaceLoserReleaseTests` 2/2, `PerspectiveSyncAwaiterAppliedTests` 17/17,
  `HeartbeatWorkerStartupBeatTests` 15/15, `BatchBufferTests` 23/23.
- Mutation check: the five mechanisms rows 10-13 and 16 rely on (`PerStreamSerializer` `tickCts.CancelAsync`,
  `CompositeBatches` `timer.CancelAsync`, the awaiter's `finally` `waitCts.CancelAsync`, `HeartbeatWorker`
  `either.CancelAsync`, `BatchBuffer` `waiter.Detach()`) were deleted on the server copy and the suites rerun: all
  seven new tests failed, each on its own mechanism (for example 20 live deadlines instead of 1).

## 2. The primitive: `WakeSet` (`src/Whizbang.Core/Async/WakeSet.cs`)

```csharp
using var wakes = new WakeSet(timeProvider, stoppingToken);   // one per loop (or per call)
var work   = wakes.Add(workReader);       // ChannelWakeSource<T>: one pending WaitToReadAsync across passes
var drain  = wakes.Add(drainReader);
var signal = wakes.Add(wakeSignal);       // consumed on every wake but a stop
var gate   = wakes.Add(schemaReady.Task); // a task that completes once
while (true) {
  var wake = await wakes.WaitAsync(pollInterval);   // Timeout.InfiniteTimeSpan = none; one timer, re-armed
  if (wake.IsStopped) break;                        // stop or dispose; never throws for a source
  if (wake.Woke(work)) { /* drain */ } if (wake.IsTimeout) { /* tick */ }
}
```

Semantics:

- **Reuse.** Each source keeps one pending wait and asks for a new one only after the last completed, so a pass another
  source ends leaves the wait for the next pass, never a second one (#2613's rule, and #728's for `WakeSignal`).
- **One timer.** The timeout is a single `ITimer` on the injected `TimeProvider`, created once, re-armed with `Change`
  each pass and stopped as soon as another source wins. No per-pass delay exists to be left behind. A fire already under
  way when another source won can land on the next pass and end it as a timeout: one extra idle tick, never a lost
  wake. Guarding against it would mean reading the clock, which fake clocks that fire a timer without moving time (as
  several worker tests do) would defeat.
- **Own token.** Every wait the set makes runs on a token linked to the loop's. Disposing the set cancels it, which
  releases every pending wait where it is held (the channel queue, the signal's waiter, the gate task's continuation).
  A set made per call (`HeartbeatWorker.WaitForNextTickAsync`) leaves nothing behind when the call returns.
- **Result.** `Wake { Reason: Source | Timeout | Stopped, Ready: bitmask }`, `Woke(source)`. Several sources can be
  ready at once. A faulted or canceled source counts as fired, as in `WhenAny`; a completed channel is
  `ChannelWakeSource<T>.IsChannelCompleted`.
- **Levels.** A source wakes every pass until the loop drains, consumes or stops it, exactly as `WhenAny` over the same
  tasks did. Signals are consumed before the loop looks for work.
- **Idle path.** A pass that parks allocates nothing: the pending waits, the timer, the registration and the awaitable
  (`IValueTaskSource<Wake>` over `ManualResetValueTaskSourceCore`) are reused. One continuation per pending wait,
  attached once. Tested: `WakeSetTests.AnIdlePark_AllocatesNothingAsync` (0 bytes).
- **AOT.** No reflection, no dynamic code; generic over the channel item only.
- **Out of scope by design.** Permits (`SemaphoreSlim`): a reused pending acquire that completes *holds a permit*, so
  it is not a wake; see decision 3. One task with a deadline: `Task.WaitAsync(timeout, timeProvider, ct)` already
  leaves nothing behind.

Tests: `tests/Whizbang.Core.Tests/Async/WakeSetTests.cs`, 22 tests, RED against a stub first (22/22 failed), then
22/22 with 100% of `WakeSet.cs` lines and branches covered: which source fired, levels, timeout on the fake clock,
zero and infinite timeouts, channel completion, 50 idle passes held to one channel wait and one timer (budget), no
stale timeout after a channel win, dispose releases the channel wait, the signal waiter and (GC check with positive
control) the gate continuation, signal consume on wake and on timeout, stop and pre-canceled stop, the one timer armed
and stopped, a timer fire with nothing parked, a zero-allocation park, misuse.

## 3. The verification kit (`src/Whizbang.Testing/Async`)

PR #2614's test-local `WaitTrackingReader<T>` and GC checks, made reusable:

| Type | What it counts or checks |
|---|---|
| `CountingChannelReader<T>(inner, onWait?)` | `WaitCalls`, `OutstandingWaits` (neither completed nor canceled). Pass-through for reads, peeks, counts, completion. |
| `CountingTimeProvider(inner)` | `LiveTimers` (made, not disposed), `CreatedTimers`, `Created(n)` (a signal, to step a loop one armed wait at a time). Wraps a `FakeTimeProvider`; time moves on the fake. A losing `Task.Delay` or deadline left running is a live timer. |
| `LoopWaitBudget` | `.AtMost(name, count, k)...RunAsync(passes, pass)`: drives N passes, checks every counter after each, throws `LoopWaitBudgetExceededException` (counter, pass, count, limit), returns the highest count of each (the positive control). |
| `ReleaseCheck.IsReleased(abandon)` | Makes and drops an object in a non-inlined frame, collects, reports whether something still keeps it. For holders no seam can count: token registrations, task continuations. Its remarks say how to make a release synchronous and why every released case needs a kept control. |

Signal and registration counting: `WakeSignal` already exposes `PendingWaiters` (internal, and `Whizbang.Testing` sees
Core's internals), so a budget reads it directly. A `CancellationToken` cannot be wrapped (registration is on the
token's source, with no hook), so registrations are checked with `ReleaseCheck`, as `BatchBufferTests` does.

Tests: `tests/Whizbang.Testing.Tests/Async/LoopWaitProbeTests.cs`, 19, with 100% of the four files' lines and branches. The audit uses the kit: the new RED test for
row 1, rows 10-13 and 16, and `WakeSetTests`.

**A consumer's own hosted service** (once the kit ships, decision 2):

```csharp
[Test]
public async Task IdleLoop_HoldsOneWaitAsync() {
  var fake = new FakeTimeProvider();
  var clock = new CountingTimeProvider(fake);
  var inbox = Channel.CreateUnbounded<Order>();
  var reader = new CountingChannelReader<Order>(inbox.Reader);
  var service = new OrderPump(reader, clock);           // the service takes ChannelReader<T> and TimeProvider
  await service.StartAsync(CancellationToken.None);

  await new LoopWaitBudget()
    .AtMost("inbox waits", () => reader.OutstandingWaits, 1)
    .AtMost("timers", () => clock.LiveTimers, 1)
    .RunAsync(100, async pass => {
      await clock.Created(pass).WaitAsync(TimeSpan.FromSeconds(10));   // the pass is parked
      fake.Advance(TimeSpan.FromSeconds(1));                           // its timeout ends it
    });
}
```

The only requirement on the service is the one the gravity principle already asks for: it takes its channel reader and
its clock by injection.

## 4. Migration (every site)

Shown as verified patches in `plans/loop-waits/`, each applied on the build server, built, and its suite run:

| Patch | Applied on | Result |
|---|---|---|
| `perspective-on-2614.patch` + `perspective-tests-on-2614.patch` | #2614's branch | Core.Component.Tests 5,022 / 5,022 |
| `dlq.patch` + `dlq-tests.patch` | this branch | `DeadLetterRecoveryWorkerTests` 41 / 41 |
| `duty.patch` | this branch | `DutyHolderWorkerTests` 33 / 33 |


- `perspective-on-2614.patch`: `PerspectiveWorker._runChannelConsumerLoopAsync` on top of #2614. `PendingChannelWait`,
  `drainNever`, the per-pass idle delay, the per-pass unconfigured-signal TCS and `AwaitConsumerWakeAsync` all go;
  `Consume()` moves into the set. The signal is always added: only the NOTIFY handler sets it, so with no listener it
  never fires (same behavior). Follow-up in the same PR: delete `PendingChannelWait<T>` and `AwaitConsumerWakeAsync`
  (and their tests), and move #2614's `WaitTrackingReader` tests onto `CountingChannelReader`.
- `dlq.patch`: `DeadLetterRecoveryWorker`. Row 4 goes.
- `perspective-tests-on-2614.patch`: three test clocks that recognized the idle poll by `CreateTimer(PollInterval)`
  (`PollFiringClock`, `HoldClock`, the barrier tests' `ArmedTimerClock`) now also see the set's re-armed timer
  (`Change(PollInterval)`). Without it 11 component tests hang on a poll that is armed but never seen; production
  behavior is unchanged.
- `dlq-tests.patch`: the DLQ tests' `ManualTimeProvider` counts a re-arm as an armed wait, for the same reason.
- `duty.patch`: `DutyHolderWorker`. The per-pass TCS becomes a `WakeSignal`; `Consume()` at the start of each pass keeps
  "a release announced before the pass is served by it"; the timeout is the rest of `RenewInterval` from the pass start,
  as before. Row 5 goes.

Every site:

| Row | Site | Change | Size |
|---|---|---|---|
| 1, 3 | PerspectiveWorker consumer loop | `WakeSet` (patch) | S |
| 2 | ASB publish | `sendTask.WaitAsync(SendTimeout, _timeProvider, ct)`; inject `TimeProvider` | S |
| 4 | DLQ scan loop | `WakeSet` (patch) | XS |
| 5 | DutyHolderWorker | `WakeSet` + `WakeSignal` (patch) | XS |
| 6 | WorkCoordinatorGate | decision 3: keep, with the analyzer's escape hatch, or a permit primitive | S |
| 7 | StartupReadiness probe | `wait.WaitAsync(WaitProbeInterval, _time, ct)` in the loop | XS |
| 8 | CapturingWorkCoordinator | `signalTask.WaitAsync(deadline remaining)` | XS |
| 9 | InstanceExit crash bound | `exiting.WaitAsync(CrashExitBudget, _time)`, sync-over-async kept | XS |
| 10, 15 | PerStreamSerializer, SlidingWindowBatcher windows | one `WakeSet` per drain loop / enumeration, `WaitAsync(remaining)` per window (drops a linked source and a timer per window) | S |
| 11 | CompositeBatches | the pending read is already reused: `WakeSet.Add(Task)` cannot re-arm a `MoveNextAsync`, so keep the canceled-timer shape (accepted by the analyzer) or add a "renewable task" source; decision 6 | XS |
| 12 | PerspectiveSyncAwaiter | accepted shape (per-call source canceled in `finally`); no change | none |
| 13 | HeartbeatWorker tick wait | `using var wakes` per call, `Add(_schemaReady gate task)`, `WaitAsync(wait)` | XS |
| 14 | perspective drain window | as rows 10/15, one set per consumer loop | XS |
| 20, 21 | startup cleared-poll, stage gate | `Task.WaitAsync(timeout)` / accepted shape | XS |
| 22 | Dispatcher single `WhenAny` | escape hatch (no wait raced) or rewrite as a try/await | XS |

## 5. Making it the only path: analyzer WHIZ612

**WHIZ612 (Error, src/ only, RepoAnalyzers):** "A wait is raced in `Task.WhenAny` by hand; use `WakeSet`, or
`Task.WaitAsync` for one task with a deadline." It generalizes WHIZ611 from channel waits to every wait.

Telling a wait from a compute task. Each `WhenAny` argument is traced to its origin within the method (the same local
data flow WHIZ611 uses) and classified:

- **Wait** (the rule applies): `Task.Delay(...)`; a channel `WaitToReadAsync`/`ReadAsync`/`WaitToWriteAsync`/`WriteAsync`
  made into a task; `SemaphoreSlim.WaitAsync(...)`; `TaskCompletionSource[<T>].Task`; `WakeSignal.WaitAsync`;
  `Task.WaitAsync(...)` over any of these; and any call to a member marked `[WaitSource]` (a new attribute, put on
  `ISchemaReadyGate.WaitForReadyAsync`, `IReadModelsReadyGate`, `IStartupStageGate.WaitForAsync`,
  `ISyncEventTracker.WhenAppliedAsync` and similar gates). A wait is something that parks a waiter or a timer in a
  longer-lived object until an event; a compute task is work that finishes on its own.
- **Compute** (not reported): any other `Task`-returning call: a dispatch, a send, a query, a user delegate. A losing
  compute task is abandoned work, which a different rule (unobserved exceptions) covers.

Reported: a `WhenAny` with at least one wait argument, unless every wait argument is in the **accepted shape**: its
token is the `Token` of a local `CancellationTokenSource` the method cancels after `WhenAny` (WHIZ611's rule, unchanged).
Not reported: anything inside `WakeSet` itself. Unknown origins (a parameter, a field) count as compute, so the rule errs
toward missing a case, never toward noise; the docs page lists what it cannot see, as WHIZ611's does.

Escape hatch: member-level `[SuppressMessage("Reliability", "WHIZ612", Justification = "...")]` only (no pragmas, no
file or assembly level), and a repository test that lists every suppression with its justification, so adding one shows
in review. Expected users: `WorkCoordinatorGate` (permits) until decision 3.

WHIZ611 then becomes a subset of WHIZ612 and can be retired into it (its id kept as retired in `diagnostic-ids.txt`).

## 6. Risks

- **Behavior parity.** `WhenAny` returned the first task to complete; `WakeSet` reports every ready source. Loops that
  branch on the winner must use `Woke(...)`; today's loops drain all channels regardless, so none change.
- **Shared `WakeSignal`.** Several consumer loops share `_perspectiveWake`. Its one waiter is governed by the token of
  the set that made it; a set disposed before the others would cancel the shared waiter, and the next set simply asks
  for a new one. Covered by the signal's own reuse, but a test with several consumers belongs in the migration PR.
- **Tests that count armed timers.** A loop on a `WakeSet` makes one timer and re-arms it with `Change`. #2614's
  `ArmingClock` already reports a `Change` as an armed wait; four other test clocks did not, and the migration patches
  them (section 4). A future test clock should count arms, not `CreateTimer` calls; `CountingTimeProvider.Created`
  counts creations, so a loop on a `WakeSet` is stepped by a signal of its own (a counting reader's `onWait`, or a fake
  whose `Change` reports).
- **Faulted channel.** As with `WhenAny`, a faulted or completed channel is ready every pass; the loop must check
  `IsChannelCompleted` or `Completion` and leave (true today as well).
- **ReleaseCheck flakiness.** A release that completes on the thread pool reads as kept for a moment; the kit's remarks
  say how to make it synchronous. `WakeSetTests.Dispose_ReleasesTheSetFromAGateThatNeverOpensAsync` relies on the
  `Task.WaitAsync` promise removing its continuation inline on cancel; watch it in CI.
- **Analyzer reach.** Data flow is per method; a wait made in a helper and raced elsewhere is unknown and not reported.

## 7. Estimate

| Work | Days |
|---|---|
| `WakeSet` hardening and docs page (contributors) | 1 |
| Migrations: rows 1-5, 7-10, 13-15, 20-22 | 2 |
| WHIZ612 + `[WaitSource]` + suppression ledger test + docs page | 1.5 |
| Verification kit docs page; package decision follow-through | 0.5 |
| **Total** | **5** (one bundle PR after #2614) |

## 8. Decisions for the maintainer

1. **Visibility.** `WakeSet` and `WakeSignal` are internal. Make them public so a consumer's hosted service can use the
   same loop, or keep the primitive internal and ship only the verification kit?
2. **Shipping the kit.** `Whizbang.Testing` is `IsPackable=false`. For consumers to use `CountingChannelReader` and
   friends it must be packaged (as is, or as a small `Whizbang.Testing.Async` package).
3. **Permit races.** `WorkCoordinatorGate` races two semaphores. Keep it under a justified WHIZ612 suppression, or add a
   permit primitive ("acquire whichever of these frees first, hand back the other")?
4. **Unknown origins.** WHIZ612 treats an unrecognized `Task` as compute (quiet, can miss). The alternative is to
   require `[WaitSource]` or `[ComputeTask]` on every raced argument (complete, noisier). Recommendation: quiet.
5. **Windows.** Move the per-window batching races (rows 10, 14, 15) to one `WakeSet` per loop (fewer allocations,
   analyzer-clean), or leave them in the accepted shape?
6. **CompositeBatches.** Add a "renewable task" source to `WakeSet` (for `MoveNextAsync`-style reads), or keep its
   accepted shape?
7. **Sequencing.** Land this as one bundle after #2614 (recommended), or fold `WakeSet` into #2614 and drop
   `PendingChannelWait` before it ships?
