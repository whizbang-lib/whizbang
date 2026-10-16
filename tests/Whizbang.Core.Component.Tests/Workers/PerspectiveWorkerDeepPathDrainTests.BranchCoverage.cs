// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Drain-path outcomes earlier tests left untaken: the perspective meters on a successful drain and on a
/// lease deadline, a stored-form refusal that carries no path, an inversion against a cursor that has a
/// commit sequence but no event id, a perspective whose runner registry is gone by the time its group
/// runs, and a pending event the raw-row lookup has no row for.
/// </summary>
public partial class PerspectiveWorkerDeepPathDrainTests {

  [Test]
  public async Task DrainMode_WithMetrics_CountsTheUpdatedStreamAndEveryAppliedEventAsync() {
    var streamId = Guid.CreateVersion7();
    var firstEvent = Guid.CreateVersion7();
    var secondEvent = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, firstEvent, Guid.CreateVersion7()),
      _raw(streamId, secondEvent, Guid.CreateVersion7())
    ]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([
      _envelope(firstEvent, new DrainDeepEvent("first")),
      _envelope(secondEvent, new DrainDeepEvent("second"))
    ]);
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(new DrainRunner()),
      configure: opts => opts.DrainLoopMaxIterations = 1,
      metrics: metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(20));
    await cts.CancelAsync();
    // The meters move after the completion is reported, on the same pass: the body has to have finished.
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(meters.GetByName("whizbang.perspective.streams_updated").Sum(m => m.Value)).IsEqualTo(1d)
      .Because("one stream was drained, so the streams-updated counter moves by one");
    await Assert.That(meters.GetByName("whizbang.perspective.events_processed").Sum(m => m.Value)).IsEqualTo(2d)
      .Because("both drained events were applied, and the counter moves by the number applied");
  }

  [Test]
  public async Task DrainMode_InversionRewind_IsCountedLikeAnyOtherRewindAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    // A cursor ahead of the arriving event, so the drain's inversion detector fires and the live
    // apply path rewinds in place rather than handing the stream to the rewind work-item path.
    coordinator.CursorOverrides[(PERSPECTIVE, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = PERSPECTIVE,
      LastEventId = Guid.CreateVersion7(),
      LastCommitSequence = 100,
      Status = PerspectiveProcessingStatus.None
    };
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7(), commitSequence: 50)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("behind"))]);
    var runner = new DrainRunner();
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner),
      configure: opts => opts.DrainLoopMaxIterations = 1,
      metrics: metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(20));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(runner.RewindCalls.Count).IsEqualTo(1)
      .Because("the arriving event is behind the cursor, so this drain rewinds the stream");
    await Assert.That(meters.GetByName("whizbang.perspective.rewinds").Sum(m => m.Value)).IsEqualTo(1d)
      .Because("a rewind is a rewind wherever it is triggered from. Counting only the ones that come "
             + "through the rewind work-item path leaves the counter reading zero while rewinds run, "
             + "which reads to an operator as a rewind path that never fires");
    await Assert.That(meters.GetByName("whizbang.perspective.rewind.duration").Count).IsEqualTo(1)
      .Because("an unrecorded histogram is absent rather than zero, so this rewind does not appear at "
             + "all in the duration series an operator would check first");
    await Assert.That(meters.GetByName("whizbang.perspective.rewind.events_replayed").Count).IsEqualTo(1)
      .Because("how much a rewind replayed is the measure of what it cost, and it is missing for "
             + "every rewind the live path takes");
  }

  [Test]
  public async Task DrainMode_AfterARewind_TheCommitSequenceCursorMovesWithTheEventIdCursorAsync() {
    var streamId = Guid.CreateVersion7();
    var lateEvent = Guid.CreateVersion7();
    var laterEvent = Guid.CreateVersion7();
    var frontierEvent = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    // The persisted position is commit sequence 100.
    coordinator.CursorOverrides[(PERSPECTIVE, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = PERSPECTIVE,
      LastEventId = Guid.CreateVersion7(),
      LastCommitSequence = 100,
      Status = PerspectiveProcessingStatus.None
    };
    // First pass: an event below the position, so the detector fires and the stream rewinds.
    coordinator.EnqueueStreamEvents([_raw(streamId, lateEvent, Guid.CreateVersion7(), commitSequence: 50)]);
    // Second pass: an event ABOVE the old position and BELOW where the rewind left the stream. It is
    // out of order against the position the rewind established, and only against that one.
    coordinator.EnqueueStreamEvents([_raw(streamId, laterEvent, Guid.CreateVersion7(), commitSequence: 150)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(lateEvent, new DrainDeepEvent("late"))]);
    eventStore.EnqueueDeserialized([_envelope(laterEvent, new DrainDeepEvent("later"))]);
    // The rewind finishes at the stream's frontier, commit sequence 200, which is not a row this
    // batch claimed. That is what makes the caller's own lookup come back empty.
    var runner = new DrainRunner { Frontier = (frontierEvent, 200) };
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner),
      // Refetch after a single-event pass too; the default skips it, and one event per pass is
      // exactly the shape this test needs.
      configure: opts => opts.DrainLoopRefetchMinBatch = 1);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    // The drain loop re-fetches after each pass and stops when a fetch comes back empty, so the two
    // enqueued responses are two passes and the third call ends the loop. The second pass runs against
    // the cursor the first pass just set, which is the thing under test.
    await coordinator.WaitForStreamEventsCallsAsync(3, TimeSpan.FromSeconds(20));
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(20));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(runner.RewindCalls.Count).IsEqualTo(2)
      .Because("the rewind moved the stream to commit sequence 200, so the second event at 150 is out "
             + "of order against the new position and has to rewind too. Judged against the position "
             + "the rewind replaced, 150 looks like ordinary forward progress, no rewind runs, and the "
             + "runner's idempotency filter drops it as already applied. The row is then permanently "
             + "short of that event with nothing pending to repair it");
  }

  [Test]
  public async Task DrainMode_LeaseDeadlineWithMetrics_CountsOneErrorAsync() {
    var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("hung"))]);
    var runner = new DrainRunner { BlockUntilCanceled = true };
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner),
      timeProvider: fakeTime,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 4 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 5 }),
      metrics: metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
    fakeTime.Advance(TimeSpan.FromSeconds(10));
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    coordinator.Failures.TryPeek(out var failure);
    await Assert.That(failure?.Error ?? string.Empty).Contains("Lease deadline exceeded");
    await Assert.That(meters.GetByName("whizbang.perspective.errors").Sum(m => m.Value)).IsEqualTo(1d)
      .Because("a lease deadline is a processing error and is counted before the failure is reported");
  }

  [Test]
  public async Task DrainMode_ApplyOutlivesItsLease_HoldsTheGateUntilTheApplyEnds_AndTheNextDrainSkipsItAsync() {
    // The lease executor stops waiting for an apply whose lease ran out, and the consumer moves on, but
    // the apply keeps running and has not yet marked its events processed. Its gate must stay held until
    // it ends: released with the consumer, a second drain of the stream applied the same event again.
    var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var workId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    var eventStore = new DrainEventStore();
    // Both drains fetch the row: it stays pending until the completion flush deletes it.
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, workId)]);
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, workId)]);
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("abandoned"))]);
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("abandoned"))]);
    var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runner = new DrainRunner { FirstApplyHangsUntil = hang.Task };
    var drainChannel = new DrainedSignalingChannel(streamId);
    var (worker, _, _) = _createWorker(
      coordinator, eventStore, _registry(runner),
      configure: opts => opts.DrainLoopMaxIterations = 1,
      cooldownCache: new RecentlyProcessedEventCache(new SystemTimeProvider()),
      timeProvider: fakeTime,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 4 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 5 }),
      drainChannel: drainChannel);
    var releases = new System.Collections.Concurrent.ConcurrentQueue<PerspectiveWorker.AffinityGateRelease>();
    worker.OnStreamAffinityGateReleased += releases.Enqueue;

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    try {
      await drainChannel.WriteAsync(streamId, cts.Token);
      await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
      // Past the one-second lease window: the executor abandons the apply and the consumer finishes the stream.
      fakeTime.Advance(TimeSpan.FromSeconds(10));
      await drainChannel.FirstDrained.WaitAsync(TimeSpan.FromSeconds(10));

      var holds = worker.SnapshotAffinityHolds(TimeSpan.Zero);
      await Assert.That(holds.Count).IsEqualTo(1)
        .Because("the consumer has moved on, but the apply it abandoned is still running and still holds the gate");
      await Assert.That((holds[0].StreamId, holds[0].PerspectiveName)).IsEqualTo((streamId, PERSPECTIVE));
      await Assert.That(holds[0].Path).IsEqualTo("drain-abandoned")
        .Because("the hold names the abandoned apply, so the watchdog reports a hung one for what it is");
    } finally {
      hang.TrySetResult();
    }

    await drainChannel.WriteAsync(streamId, cts.Token);
    await drainChannel.SecondDrained.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1)
      .Because("the second drain ran only after the abandoned apply ended and marked the event processed");
    await Assert.That(releases.ToArray()).IsEquivalentTo(
      [
        new PerspectiveWorker.AffinityGateRelease(streamId, PERSPECTIVE, Abandoned: true),
        new PerspectiveWorker.AffinityGateRelease(streamId, PERSPECTIVE, Abandoned: false)
      ], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the abandoned apply released the gate when it ended, then the second drain took and released it");
    await Assert.That(worker.SnapshotAffinityHolds(TimeSpan.Zero)).IsEmpty()
      .Because("the gate went free when the abandoned apply ended");
  }

  [Test]
  public async Task Stop_WhileAnAbandonedApplyStillRuns_WaitsForItToEndAndReleaseItsGateAsync() {
    // Stopping cancels the lease token too (it is linked to the worker's), so the executor abandons an
    // apply still in the runner. The worker must not finish stopping while that apply runs: a stopped
    // worker that still held a gate, or still had an apply of its running, is not stopped.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("stopping"))]);
    var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runner = new DrainRunner { FirstApplyHangsUntil = hang.Task };
    var drainChannel = new DrainedSignalingChannel(streamId);
    var logger = new CapturingLogger<PerspectiveWorker>();
    var (worker, _, _) = _createWorker(
      coordinator, eventStore, _registry(runner),
      configure: opts => opts.DrainLoopMaxIterations = 1,
      cooldownCache: new RecentlyProcessedEventCache(new SystemTimeProvider()),
      logger: logger,
      drainChannel: drainChannel);
    var released = new TaskCompletionSource<PerspectiveWorker.AffinityGateRelease>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnStreamAffinityGateReleased += r => released.TrySetResult(r);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var waiting = logger.WaitForAsync(e => e.Message.Contains("waiting for 1 abandoned apply", StringComparison.Ordinal));
    try {
      await drainChannel.WriteAsync(streamId, cts.Token);
      await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
      await cts.CancelAsync();
      // The consumer has left the gate scope while the runner still hangs: the apply was abandoned.
      await drainChannel.FirstDrained.WaitAsync(TimeSpan.FromSeconds(10));

      // Either the worker finishes stopping, or it says it is waiting for the abandoned apply. The apply
      // cannot end until this test releases it, so only the second is correct.
      var first = await Task.WhenAny(worker.ExecuteTask!, waiting).WaitAsync(TimeSpan.FromSeconds(10));
      await Assert.That(first == waiting).IsTrue()
        .Because("the worker waits for an abandoned apply to end before it finishes stopping");
      await Assert.That(worker.SnapshotAffinityHolds(TimeSpan.Zero).Single().Path).IsEqualTo("drain-abandoned");
    } finally {
      hang.TrySetResult();
    }

    var release = await released.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(release).IsEqualTo(new PerspectiveWorker.AffinityGateRelease(streamId, PERSPECTIVE, Abandoned: true))
      .Because("the gate went free when the abandoned apply ended, and the release says it was abandoned");
    await Assert.That(worker.SnapshotAffinityHolds(TimeSpan.Zero)).IsEmpty()
      .Because("a stopped worker holds no gate");
  }

  [Test]
  public async Task Stop_WhileAnAbandonedApplyStillRuns_KeepsItsScopeAliveUntilTheApplyEndsAsync() {
    // The abandoned apply still uses the drain group's scope: its runner, its work coordinator and, in a
    // real host, a DbContext whose command may be in flight. Disposing the scope when the consumer leaves
    // closes that connection under the running command (#1382); the scope must outlive the apply.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("scope-at-stop"))]);
    var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runner = new DrainRunner { FirstApplyHangsUntil = hang.Task };
    var probes = new ScopeProbes(_registry(runner));
    var drainChannel = new DrainedSignalingChannel(streamId);
    var (worker, _, _) = _createWorker(
      coordinator, eventStore, _registry(runner),
      configure: opts => opts.DrainLoopMaxIterations = 1,
      cooldownCache: new RecentlyProcessedEventCache(new SystemTimeProvider()),
      registryFactory: probes.Create,
      drainChannel: drainChannel);
    var released = new TaskCompletionSource<PerspectiveWorker.AffinityGateRelease>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnStreamAffinityGateReleased += r => released.TrySetResult(r);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    ScopeProbes.Probe applyScope;
    try {
      await drainChannel.WriteAsync(streamId, cts.Token);
      await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
      applyScope = probes.Resolved.Single(p => p.RunnerRequested);
      await cts.CancelAsync();
      // The consumer has left: the apply was abandoned and is still in the runner.
      await drainChannel.FirstDrained.WaitAsync(TimeSpan.FromSeconds(10));

      await Assert.That(applyScope.Disposals).IsEqualTo(0)
        .Because("the abandoned apply is still running on the drain group's scope, which must not be disposed under it");
    } finally {
      hang.TrySetResult();
    }

    await released.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(applyScope.Disposals).IsEqualTo(1)
      .Because("the scope is disposed once, after the abandoned apply ended");
    await Assert.That(applyScope.DisposedWhileApplyRunning).IsFalse()
      .Because("disposal waited for the apply to leave the runner");
  }

  [Test]
  public async Task DrainMode_ApplyOutlivesItsLease_KeepsItsScopeAliveUntilTheApplyEndsAsync() {
    // The same hand-off in steady state: the lease runs out, the executor abandons the apply and the
    // consumer moves on, but the apply keeps running on the group's scope (#1382).
    var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("scope-at-lease-expiry"))]);
    var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runner = new DrainRunner { FirstApplyHangsUntil = hang.Task };
    var probes = new ScopeProbes(_registry(runner));
    var drainChannel = new DrainedSignalingChannel(streamId);
    var (worker, _, _) = _createWorker(
      coordinator, eventStore, _registry(runner),
      configure: opts => opts.DrainLoopMaxIterations = 1,
      cooldownCache: new RecentlyProcessedEventCache(new SystemTimeProvider()),
      timeProvider: fakeTime,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 4 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 5 }),
      registryFactory: probes.Create,
      drainChannel: drainChannel);
    var released = new TaskCompletionSource<PerspectiveWorker.AffinityGateRelease>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnStreamAffinityGateReleased += r => released.TrySetResult(r);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    ScopeProbes.Probe applyScope;
    try {
      await drainChannel.WriteAsync(streamId, cts.Token);
      await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
      applyScope = probes.Resolved.Single(p => p.RunnerRequested);
      fakeTime.Advance(TimeSpan.FromSeconds(10));
      await drainChannel.FirstDrained.WaitAsync(TimeSpan.FromSeconds(10));

      await Assert.That(applyScope.Disposals).IsEqualTo(0)
        .Because("the apply the lease abandoned still runs on the drain group's scope");
    } finally {
      hang.TrySetResult();
    }

    await released.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(applyScope.Disposals).IsEqualTo(1)
      .Because("the scope is disposed once, after the abandoned apply ended");
    await Assert.That(applyScope.DisposedWhileApplyRunning).IsFalse();
  }

  /// <summary>
  /// The runner registry as a transient, so each resolution belongs to the scope that resolved it and is
  /// disposed exactly when that scope is. The worker resolves it from the drain group's scope to get the
  /// runner, which makes it a probe for that scope's lifetime.
  /// </summary>
  private sealed class ScopeProbes(IPerspectiveRunnerRegistry inner) {
    private readonly System.Collections.Concurrent.ConcurrentQueue<Probe> _resolved = new();
    private int _appliesRunning;

    public IReadOnlyList<Probe> Resolved => [.. _resolved];

    /// <summary>The factory the worker's container calls for each resolution.</summary>
    public Func<IServiceProvider, IPerspectiveRunnerRegistry> Create => _ => {
      var probe = new Probe(inner, this);
      _resolved.Enqueue(probe);
      return probe;
    };

    public sealed class Probe(IPerspectiveRunnerRegistry inner, ScopeProbes owner) : IPerspectiveRunnerRegistry, IAsyncDisposable {
      private int _disposals;
      public bool RunnerRequested { get; private set; }
      public int Disposals => Volatile.Read(ref _disposals);
      public bool DisposedWhileApplyRunning { get; private set; }

      public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) {
        RunnerRequested = true;
        var runner = inner.GetRunner(perspectiveName, serviceProvider);
        return runner is null ? null : new TrackingRunner(runner, owner);
      }

      public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => inner.GetRegisteredPerspectives();
      public IReadOnlyList<Type> GetEventTypes() => inner.GetEventTypes();
      public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => inner.LifecycleStagesWithReceptors;

      public ValueTask DisposeAsync() {
        if (Volatile.Read(ref owner._appliesRunning) > 0) {
          DisposedWhileApplyRunning = true;
        }
        Interlocked.Increment(ref _disposals);
        return ValueTask.CompletedTask;
      }
    }

    /// <summary>Counts applies inside the runner so a disposal can say whether one was still running.</summary>
    private sealed class TrackingRunner(IPerspectiveRunner inner, ScopeProbes owner) : IPerspectiveRunner {
      public Type PerspectiveType => inner.PerspectiveType;

      public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
        inner.RunAsync(streamId, perspectiveName, lastProcessedEventId, cancellationToken);

      public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        inner.RewindAndRunAsync(streamId, perspectiveName, triggeringEventId, cancellationToken);

      public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
        inner.BootstrapSnapshotAsync(streamId, perspectiveName, lastProcessedEventId, cancellationToken);

      public async Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
        Interlocked.Increment(ref owner._appliesRunning);
        try {
          return await inner.RunWithEventsAsync(streamId, perspectiveName, lastProcessedEventId, events, cancellationToken);
        } finally {
          Interlocked.Decrement(ref owner._appliesRunning);
        }
      }
    }
  }

  /// <summary>
  /// Drain channel that signals each time a consumer finishes the watched stream. <c>MarkDrained</c> runs in
  /// the consumer's <c>finally</c>, after the affinity gate scope, so it proves the consumer left that scope.
  /// </summary>
  private sealed class DrainedSignalingChannel(Guid watchedStreamId) : IPerspectiveDrainChannel {
    private readonly PerspectiveDrainChannel _inner = new();
    private readonly TaskCompletionSource _firstDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _drained;

    public Task FirstDrained => _firstDrained.Task;
    public Task SecondDrained => _secondDrained.Task;

    public System.Threading.Channels.ChannelReader<Guid> Reader => _inner.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) => _inner.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _inner.TryWrite(streamId);
    public bool IsInFlight(Guid streamId) => _inner.IsInFlight(streamId);
    public void MarkDraining(Guid streamId) => _inner.MarkDraining(streamId);

    public void MarkDrained(Guid streamId) {
      _inner.MarkDrained(streamId);
      if (streamId != watchedStreamId) {
        return;
      }
      if (Interlocked.Increment(ref _drained) == 1) {
        _firstDrained.TrySetResult();
      } else {
        _secondDrained.TrySetResult();
      }
    }
  }

  [Test]
  public async Task DrainMode_StoredFormRefusalWithoutAPath_SaysThePathWasNotReportedAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var workId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, workId)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("pathless"))]);
    var (runner, registry) = _storedFormRunner();
    // A reader that refuses a value without the serializer having added a path.
    runner.NextException = new JsonException("the stored value is not a readable temporal");
    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.FailureCapture.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    var (_, parked) = harness.FailureCapture.Items.Single();
    await Assert.That(parked.MessageId).IsEqualTo(workId);
    await Assert.That(parked.Reason).IsEqualTo(MessageFailureReason.SerializationError)
      .Because("a refusal with no path is still a stored form no reader takes");
    await Assert.That(parked.Error).IsEqualTo("Stored form unreadable at (path not reported): the stored value is not a readable temporal")
      .Because("the message says the path is unknown rather than printing an empty location");
  }

  [Test]
  public async Task DrainMode_InversionAgainstACursorWithNoEventId_ReportsAnEmptyCachedCursorAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    // The persisted cursor has a commit sequence but no event id: the commit-sequence detector decides,
    // and the cursor event id it logs is the empty one.
    coordinator.CursorOverrides[(PERSPECTIVE, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = PERSPECTIVE,
      LastEventId = null,
      LastCommitSequence = 100,
      Status = PerspectiveProcessingStatus.None
    };
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7(), commitSequence: 50)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("behind"))]);
    var runner = new DrainRunner();
    var logger = new WarningCapturingLogger();
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner),
      configure: opts => opts.DrainLoopMaxIterations = 1,
      logger: logger);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(20));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(runner.RewindCalls.Count).IsEqualTo(1)
      .Because("commit sequence 50 is behind the cursor's 100, so the drain rewinds");
    runner.RewindCalls.TryPeek(out var rewind);
    await Assert.That(rewind.TriggerEventId).IsEqualTo(eventId);
    var triggered = logger.Warnings.Where(w => w.StartsWith("Cursor inversion detected", StringComparison.Ordinal)).ToList();
    await Assert.That(triggered).Count().IsEqualTo(1);
    await Assert.That(triggered[0]).Contains($"cached cursor {Guid.Empty}")
      .Because("a cursor with no event id is reported as the empty id, not left out of the warning");
  }

  [Test]
  public async Task DrainMode_RunnerRegistryGoneWhenTheGroupRuns_SkipsThePerspectiveWithoutFailingAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("orphaned"))]);
    var runner = new DrainRunner();
    var registry = _registry(runner);
    // Startup resolves the registry (and builds the perspective map from it); the drain group's scope,
    // the only other resolution, finds none.
    var resolutions = 0;
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, registry,
      configure: opts => opts.DrainLoopMaxIterations = 1,
      registryFactory: _ => Interlocked.Increment(ref resolutions) == 1 ? registry : null!);
    var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => cycleComplete.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await cycleComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(eventStore.DeserializeCallCount).IsGreaterThanOrEqualTo(1)
      .Because("the drain fetched and read the stream before reaching the perspective's group");
    await Assert.That(Volatile.Read(ref resolutions)).IsGreaterThanOrEqualTo(2)
      .Because("the group looked the registry up again in its own scope");
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0)
      .Because("with no registry there is no runner, and the group is skipped");
    await Assert.That(coordinator.Failures.Count).IsEqualTo(0)
      .Because("a missing runner is a skip, not a perspective failure");
    await Assert.That(coordinator.Completions.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FindCursorInversionAnchorByCommitSequence_EventWithNoRawRow_IsSkippedAsync() {
    var streamId = Guid.CreateVersion7();
    var unmatched = Guid.CreateVersion7();
    var violator = Guid.CreateVersion7();
    var rawByEventId = new List<StreamEventData> { _raw(streamId, violator, Guid.CreateVersion7(), commitSequence: 5) }
      .ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> {
      _envelope(unmatched, new DrainDeepEvent("no row")),
      _envelope(violator, new DrainDeepEvent("behind"))
    };

    var anchor = PerspectiveWorker._findCursorInversionAnchorByCommitSequence(events, rawByEventId, cachedCommitSequence: 10);
    var onlyUnmatched = PerspectiveWorker._findCursorInversionAnchorByCommitSequence([.. events.Take(1)], rawByEventId, cachedCommitSequence: 10);

    await Assert.That(anchor).IsEqualTo(violator)
      .Because("an event the lookup has no row for has no commit sequence to compare and is skipped, not fatal");
    await Assert.That(onlyUnmatched).IsNull()
      .Because("with nothing comparable there is no inversion");
  }
}
