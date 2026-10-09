// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
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
    await Assert.That(worker.SnapshotAffinityHolds(TimeSpan.Zero)).IsEmpty()
      .Because("the gate went free when the abandoned apply ended");
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
