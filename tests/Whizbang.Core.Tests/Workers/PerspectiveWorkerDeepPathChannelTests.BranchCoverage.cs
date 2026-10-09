// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Tracing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The per-event (channel) path's optional collaborators, each exercised present where earlier tests
/// only ran them absent: the perspective meters, the lifecycle-coordinator meters, the completion meter,
/// the trace spans of the normal and rewind paths, and the internal seams the batch delegates to
/// (post-lifecycle firing, detached stages, the affinity gate and the lock keepalive).
/// </summary>
public partial class PerspectiveWorkerDeepPathChannelTests {

  // ------------------------------------------------------------------
  // Perspective meters on the normal path
  // ------------------------------------------------------------------

  [Test]
  public async Task ProcessChannelBatch_WithMetrics_RecordsCheckpointEventLoadRunnerAndThroughputAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.MeteredPerspective";
    var coordinator = new RecordingWorkCoordinator();
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var eventStore = new SequencedEventStore();
    eventStore.EnqueueResponse([_envelope(eventId, new DeepChannelEvent("metered"))]);
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, _, _) = _buildBranchWorker(coordinator, registry, new BranchSetup {
      EventStore = eventStore,
      ReceptorInvoker = new NoOpInvoker(),
      Metrics = metrics
    });

    await worker.ProcessChannelBatchAsync([_branchWork(streamId, perspectiveName)], CancellationToken.None);

    await Assert.That(_branchSum(meters, "whizbang.perspective.streams_updated")).IsEqualTo(1d)
      .Because("one stream was applied, so the streams-updated counter moves by exactly one");
    await Assert.That(_branchSum(meters, "whizbang.perspective.events_processed")).IsEqualTo(1d)
      .Because("the one event read back after the run is the one event the run processed");
    await Assert.That(meters.GetByName("whizbang.perspective.checkpoint.duration")).Count().IsEqualTo(1)
      .Because("the cursor is read once per group and that read is timed");
    await Assert.That(meters.GetByName("whizbang.perspective.event_load.duration")).Count().IsEqualTo(1)
      .Because("the upcoming events are loaded once per group and that load is timed");
    var loadedCounts = meters.GetByName("whizbang.perspective.batch.event_count");
    await Assert.That(loadedCounts).Count().IsEqualTo(1);
    await Assert.That(loadedCounts[0].Value).IsEqualTo(1d)
      .Because("the histogram records how many events the load returned");
    await Assert.That(meters.GetByName("whizbang.perspective.runner.duration")).Count().IsEqualTo(1)
      .Because("the normal path times its one runner call");
  }

  [Test]
  public async Task ProcessChannelBatch_RunnerThrowsWithMetrics_CountsOneErrorAndNoStreamUpdateAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.MeteredFailingPerspective";
    var coordinator = new RecordingWorkCoordinator();
    var runner = new RecordingRunner { RunException = new InvalidOperationException("metered apply failed") };
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, _, _) = _buildBranchWorker(coordinator, registry, new BranchSetup { Metrics = metrics });

    await worker.ProcessChannelBatchAsync([_branchWork(streamId, perspectiveName)], CancellationToken.None);

    await Assert.That(_branchSum(meters, "whizbang.perspective.errors")).IsEqualTo(1d)
      .Because("a contained group failure is counted once on the error meter");
    await Assert.That(_branchSum(meters, "whizbang.perspective.streams_updated")).IsEqualTo(0d)
      .Because("a group that failed did not update its stream");
    await Assert.That(coordinator.Failures.Count).IsEqualTo(1);
    coordinator.Failures.TryPeek(out var failure);
    await Assert.That(failure?.Error).IsEqualTo("metered apply failed");
  }

  // ------------------------------------------------------------------
  // Trace spans on the normal path
  // ------------------------------------------------------------------

  [Test]
  [NotInParallel("WhizbangActivityListener")]
  public async Task ProcessChannelBatch_UnderAnAmbientActivity_ParentsThePerspectiveSpanToItAsync() {
    var stopped = new ConcurrentBag<Activity>();
    using var listener = _listenToWhizbangTracing(stopped);
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.AmbientPerspective";
    var coordinator = new RecordingWorkCoordinator();
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var logger = new FakeLogger<PerspectiveWorker>();
    // No event store: the group loads nothing, so the span reports zero events loaded.
    var (worker, _, _) = _buildBranchWorker(coordinator, registry, new BranchSetup {
      Tracing = _perspectiveTracing(),
      Logger = logger
    });

    using var ambient = new Activity("branch-coverage-ambient").Start();
    await worker.ProcessChannelBatchAsync([_branchWork(streamId, perspectiveName)], CancellationToken.None);
    ambient.Stop();

    var streamTag = streamId.ToString();
    var perspectiveSpan = stopped.Single(a => a.OperationName == $"Perspective {perspectiveName}" && _tag(a, "whizbang.stream.id") == streamTag);
    await Assert.That(perspectiveSpan.TraceId).IsEqualTo(ambient.TraceId)
      .Because("with batch spans off, the ambient activity is the parent the batch hands every group");
    await Assert.That(perspectiveSpan.ParentSpanId).IsEqualTo(ambient.SpanId);
    await Assert.That(_tag(perspectiveSpan, "whizbang.perspective.events_loaded")).IsEqualTo("0")
      .Because("nothing was loaded, and the span says so rather than omitting the count");

    var runSpan = stopped.Single(a => a.OperationName == "Perspective RunAsync" && _tag(a, "whizbang.stream.id") == streamTag);
    await Assert.That(_tag(runSpan, "whizbang.perspective.last_processed_event_id")).IsEqualTo("null")
      .Because("a stream with no cursor has never been processed");
    await Assert.That(logger.Collector.GetSnapshot().Any(r =>
      r.Message.Contains("last processed event: null (never processed)", StringComparison.Ordinal))).IsTrue()
      .Because("the cursor log names the never-processed case in words, not as an empty id");
  }

  [Test]
  [NotInParallel("WhizbangActivityListener")]
  public async Task ProcessChannelBatch_CursorAndUntracedEvent_TagsTheCursorAndTheMissingTraceParentAsync() {
    var stopped = new ConcurrentBag<Activity>();
    using var listener = _listenToWhizbangTracing(stopped);
    var streamId = Guid.CreateVersion7();
    var cursorEventId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.CursorPerspective";
    var coordinator = new RecordingWorkCoordinator();
    coordinator.CursorOverrides[(perspectiveName, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = cursorEventId,
      Status = PerspectiveProcessingStatus.None
    };
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var eventStore = new SequencedEventStore();
    eventStore.EnqueueResponse([_envelope(Guid.CreateVersion7(), new DeepChannelEvent("untraced"))]);
    var logger = new FakeLogger<PerspectiveWorker>();
    var (worker, _, _) = _buildBranchWorker(coordinator, registry, new BranchSetup {
      EventStore = eventStore,
      Tracing = _perspectiveTracing(),
      Logger = logger
    });

    await worker.ProcessChannelBatchAsync([_branchWork(streamId, perspectiveName)], CancellationToken.None);

    var streamTag = streamId.ToString();
    var perspectiveSpan = stopped.Single(a => a.OperationName == $"Perspective {perspectiveName}" && _tag(a, "whizbang.stream.id") == streamTag);
    await Assert.That(_tag(perspectiveSpan, "whizbang.perspective.first_event_traceparent")).IsEqualTo("(none)")
      .Because("an event that carried no trace parent is reported as having none, not as an empty string");
    var runSpan = stopped.Single(a => a.OperationName == "Perspective RunAsync" && _tag(a, "whizbang.stream.id") == streamTag);
    await Assert.That(_tag(runSpan, "whizbang.perspective.last_processed_event_id")).IsEqualTo(cursorEventId.ToString())
      .Because("the run span names the cursor the runner was handed");
    await Assert.That(logger.Collector.GetSnapshot().Any(r =>
      r.Message.Contains($"last processed event: {cursorEventId}", StringComparison.Ordinal))).IsTrue()
      .Because("the cursor log names the event the stream was last processed to");
  }

  // ------------------------------------------------------------------
  // Rewind path: meters and spans
  // ------------------------------------------------------------------

  [Test]
  [NotInParallel("WhizbangActivityListener")]
  public async Task ProcessChannelBatch_RewindWithMetricsAndSpans_RecordsRewindMetersAndSnapshotReplaySourceAsync() {
    var stopped = new ConcurrentBag<Activity>();
    using var listener = _listenToWhizbangTracing(stopped);
    var streamId = Guid.CreateVersion7();
    var triggerEventId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.MeteredRewindPerspective";
    var coordinator = _rewindCoordinator(streamId, perspectiveName, triggerEventId);
    var runner = new RecordingRunner();
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, _, _) = _buildBranchWorker(coordinator, registry, new BranchSetup {
      Tracing = _perspectiveTracing(),
      Metrics = metrics,
      // The default IsConfigured is true: a snapshot store is available to the rewind.
      SnapshotStore = new NoSnapshotStore()
    });

    await worker.ProcessChannelBatchAsync([
      _branchWork(streamId, perspectiveName, PerspectiveProcessingStatus.RewindRequired),
      _branchWork(streamId, perspectiveName, PerspectiveProcessingStatus.RewindRequired)
    ], CancellationToken.None);

    await Assert.That(runner.RunCallCount).IsEqualTo(1)
      .Because("the rewind runs the stream once, whatever the number of work items behind it");
    var rewinds = meters.GetByName("whizbang.perspective.rewinds")
      .Where(m => m.Tags.GetValueOrDefault("perspective_name") == perspectiveName).ToList();
    await Assert.That(rewinds).Count().IsEqualTo(1);
    await Assert.That(rewinds[0].Value).IsEqualTo(1d);
    await Assert.That(rewinds[0].Tags["has_snapshot"]).IsEqualTo(bool.TrueString)
      .Because("the rewind counter is split by whether a snapshot store was available");
    await Assert.That(meters.GetByName("whizbang.perspective.rewind.duration")
      .Count(m => m.Tags.GetValueOrDefault("perspective_name") == perspectiveName)).IsEqualTo(1);
    var replayed = meters.GetByName("whizbang.perspective.rewind.events_replayed")
      .Where(m => m.Tags.GetValueOrDefault("perspective_name") == perspectiveName).ToList();
    await Assert.That(replayed).Count().IsEqualTo(1);
    await Assert.That(replayed[0].Value).IsEqualTo(0d)
      .Because("the runner reported no events processed, and the histogram records exactly that");
    var behind = meters.GetByName("whizbang.perspective.rewind.events_behind")
      .Where(m => m.Tags.GetValueOrDefault("perspective_name") == perspectiveName).ToList();
    await Assert.That(behind).Count().IsEqualTo(1);
    await Assert.That(behind[0].Value).IsEqualTo(2d)
      .Because("two work items were queued behind the cursor when the rewind was triggered");
    await Assert.That(meters.GetByName("whizbang.perspective.runner.duration")).Count().IsEqualTo(1);

    var rewindSpan = stopped.Single(a => a.OperationName == "Perspective RewindAndRunAsync"
      && _tag(a, "whizbang.stream.id") == streamId.ToString());
    await Assert.That(_tag(rewindSpan, "whizbang.perspective.name")).IsEqualTo(perspectiveName);
    await Assert.That(_tag(rewindSpan, "whizbang.perspective.rewind_trigger_event_id")).IsEqualTo(triggerEventId.ToString());
    await Assert.That(_tag(rewindSpan, "whizbang.perspective.status")).IsEqualTo(nameof(PerspectiveProcessingStatus.Completed));
    await Assert.That(_tag(rewindSpan, "whizbang.perspective.rewind.events_behind")).IsEqualTo("2");
    await Assert.That(_tag(rewindSpan, "whizbang.perspective.rewind.events_replayed")).IsEqualTo("0");
    await Assert.That(_tag(rewindSpan, "whizbang.perspective.rewind.has_snapshot")).IsEqualTo(bool.TrueString);
    await Assert.That(_tag(rewindSpan, "whizbang.perspective.rewind.replay_source")).IsEqualTo("snapshot")
      .Because("with a snapshot store available the replay is reported as snapshot-sourced");
  }

  [Test]
  [NotInParallel("WhizbangActivityListener")]
  public async Task ProcessChannelBatch_RewindWithoutSnapshotStore_TagsAFullReplaySourceAsync() {
    var stopped = new ConcurrentBag<Activity>();
    using var listener = _listenToWhizbangTracing(stopped);
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.FullRewindPerspective";
    var coordinator = _rewindCoordinator(streamId, perspectiveName, Guid.CreateVersion7());
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var (worker, _, _) = _buildBranchWorker(coordinator, registry, new BranchSetup { Tracing = _perspectiveTracing() });

    await worker.ProcessChannelBatchAsync(
      [_branchWork(streamId, perspectiveName, PerspectiveProcessingStatus.RewindRequired)], CancellationToken.None);

    var rewindSpan = stopped.Single(a => a.OperationName == "Perspective RewindAndRunAsync"
      && _tag(a, "whizbang.stream.id") == streamId.ToString());
    await Assert.That(_tag(rewindSpan, "whizbang.perspective.rewind.has_snapshot")).IsEqualTo(bool.FalseString);
    await Assert.That(_tag(rewindSpan, "whizbang.perspective.rewind.replay_source")).IsEqualTo("full")
      .Because("with no snapshot store the rewind can only replay the stream from its first event");
    await Assert.That(_tag(rewindSpan, "whizbang.perspective.rewind.events_behind")).IsEqualTo("1");
  }

  [Test]
  [NotInParallel("WhizbangActivityListener")]
  public async Task ProcessChannelBatch_RewindThrowsWithMetricsAndSpans_CountsTheErrorAndFailsTheSpanAsync() {
    var stopped = new ConcurrentBag<Activity>();
    using var listener = _listenToWhizbangTracing(stopped);
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.FailingRewindPerspective";
    var coordinator = _rewindCoordinator(streamId, perspectiveName, Guid.CreateVersion7());
    var runner = new RecordingRunner { RunException = new InvalidOperationException("rewind replay failed") };
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, _, _) = _buildBranchWorker(coordinator, registry, new BranchSetup {
      Tracing = _perspectiveTracing(),
      Metrics = metrics
    });

    await worker.ProcessChannelBatchAsync(
      [_branchWork(streamId, perspectiveName, PerspectiveProcessingStatus.RewindRequired)], CancellationToken.None);

    await Assert.That(_branchSum(meters, "whizbang.perspective.errors")).IsEqualTo(1d)
      .Because("an isolated rewind failure is still a processing error and is counted");
    await Assert.That(meters.GetByName("whizbang.perspective.rewinds")
      .Count(m => m.Tags.GetValueOrDefault("perspective_name") == perspectiveName)).IsEqualTo(0)
      .Because("a rewind that failed did not complete, so it is not counted as one");
    var rewindSpan = stopped.Single(a => a.OperationName == "Perspective RewindAndRunAsync"
      && _tag(a, "whizbang.stream.id") == streamId.ToString());
    await Assert.That(rewindSpan.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(rewindSpan.StatusDescription).IsEqualTo("rewind replay failed");
    await Assert.That(_tag(rewindSpan, "whizbang.perspective.rewind.error")).IsEqualTo("rewind replay failed")
      .Because("the span carries the failure so a trace shows why the rewind stopped");
  }

  // ------------------------------------------------------------------
  // Completion meter and the affinity gate
  // ------------------------------------------------------------------

  [Test]
  public async Task ProcessChannelBatch_WithCompletionMeter_RecordsEveryClaimedWorkItemAsDrainedAsync() {
    const string perspectiveName = "Deep.MeteredDrainPerspective";
    var coordinator = new RecordingWorkCoordinator();
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var completionMeter = new WorkCompletionMeter();
    var (worker, _, _) = _buildBranchWorker(coordinator, registry, new BranchSetup { CompletionMeter = completionMeter });

    await worker.ProcessChannelBatchAsync([
      _branchWork(Guid.CreateVersion7(), perspectiveName),
      _branchWork(Guid.CreateVersion7(), perspectiveName)
    ], CancellationToken.None);

    await Assert.That(completionMeter.ReadAndReset()).IsEqualTo(2L)
      .Because("a finished batch stops occupying the instance, so every work item it carried counts as drained");
  }

  [Test]
  public async Task WithStreamAffinityGate_ContendedWithoutASubscriber_WaitsForTheHolderAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.ContendedPerspective";
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var (worker, _, _) = _buildBranchWorker(new RecordingWorkCoordinator(), registry, new BranchSetup());
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var order = new ConcurrentQueue<string>();

    var holder = worker.WithStreamAffinityGateAsync(streamId, perspectiveName, async _ => {
      order.Enqueue("first entered");
      await release.Task;
      order.Enqueue("first left");
    }, CancellationToken.None);
    var contender = worker.WithStreamAffinityGateAsync(streamId, perspectiveName, _ => {
      order.Enqueue("second entered");
      return Task.CompletedTask;
    }, CancellationToken.None);

    await Assert.That(contender.IsCompleted).IsFalse()
      .Because("the gate is held, so the second apply parks even when nobody listens for contention");
    release.TrySetResult();
    await Task.WhenAll(holder, contender).WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(string.Join("|", order)).IsEqualTo("first entered|first left|second entered")
      .Because("one applier per stream at a time: the contender runs only after the holder left");
  }

  [Test]
  public async Task WithStreamAffinityGate_ContendedWithASubscriber_ReportsTheContendedKeyOnceAndStillWaitsAsync() {
    // The non-null side of the contention notification. Integration tests take it too, but coverage is
    // compared per test project, so this project has to take both sides itself.
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.ContendedObservedPerspective";
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var (worker, _, _) = _buildBranchWorker(new RecordingWorkCoordinator(), registry, new BranchSetup());
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var contended = new ConcurrentQueue<(Guid StreamId, string PerspectiveName)>();
    worker.OnStreamAffinityGateContended += contended.Enqueue;
    var secondEntered = false;

    var holder = worker.WithStreamAffinityGateAsync(streamId, perspectiveName, _ => release.Task, CancellationToken.None);

    await Assert.That(contended.IsEmpty).IsTrue()
      .Because("taking a free gate is not contention");

    var contender = worker.WithStreamAffinityGateAsync(streamId, perspectiveName, _ => {
      secondEntered = true;
      return Task.CompletedTask;
    }, CancellationToken.None);

    await Assert.That(contended.Count).IsEqualTo(1);
    await Assert.That(contended.Single()).IsEqualTo((streamId, perspectiveName))
      .Because("a held gate reports the contended (stream, perspective) key to the subscriber before parking");
    await Assert.That(secondEntered).IsFalse()
      .Because("reporting contention does not let the contender past the holder");

    release.TrySetResult();
    await Task.WhenAll(holder, contender).WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(secondEntered).IsTrue();
    await Assert.That(contended.Count).IsEqualTo(1)
      .Because("only the one parked caller was contended");
  }

  [Test]
  public async Task WithStreamAffinityGate_BodyHandsOverAnApplyStillRunning_HoldsTheGateUntilThatApplyEndsAsync() {
    // The drain body stops waiting for an apply whose lease ran out, but the apply keeps running. The gate
    // belongs to the apply, not to the consumer: it stays held after the body returns, until the apply ends.
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.AbandonedApplyPerspective";
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var (worker, _, _) = _buildBranchWorker(new RecordingWorkCoordinator(), registry, new BranchSetup());
    var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondEntered = false;

    await worker.WithStreamAffinityGateAsync(streamId, perspectiveName, keepHeldUntil => {
      keepHeldUntil(apply.Task);
      return Task.CompletedTask;
    }, CancellationToken.None);

    var hold = worker.SnapshotAffinityHolds(TimeSpan.Zero).Single();
    await Assert.That((hold.StreamId, hold.PerspectiveName)).IsEqualTo((streamId, perspectiveName))
      .Because("the body returned, but the apply it handed over is still running and still holds the gate");
    await Assert.That(hold.Path).IsEqualTo("drain-abandoned")
      .Because("the watchdog names a hold its consumer has left, so a hung abandoned apply reads as one");

    var contender = worker.WithStreamAffinityGateAsync(streamId, perspectiveName, _ => {
      secondEntered = true;
      return Task.CompletedTask;
    }, CancellationToken.None);
    await Assert.That(contender.IsCompleted).IsFalse()
      .Because("a second applier parks behind the running apply even though its consumer has moved on");
    await Assert.That(secondEntered).IsFalse();

    apply.TrySetResult();
    await contender.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(secondEntered).IsTrue()
      .Because("the gate is released the moment the handed-over apply ends");
    await Assert.That(worker.SnapshotAffinityHolds(TimeSpan.Zero)).IsEmpty();
  }

  [Test]
  public async Task WithStreamAffinityGate_HandedOverApplyFails_StillReleasesTheGateAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.AbandonedFailingApplyPerspective";
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var (worker, _, _) = _buildBranchWorker(new RecordingWorkCoordinator(), registry, new BranchSetup());
    var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    await worker.WithStreamAffinityGateAsync(streamId, perspectiveName, keepHeldUntil => {
      keepHeldUntil(apply.Task);
      return Task.CompletedTask;
    }, CancellationToken.None);
    var contender = worker.WithStreamAffinityGateAsync(streamId, perspectiveName, _ => Task.CompletedTask, CancellationToken.None);

    apply.TrySetException(new InvalidOperationException("the abandoned apply failed"));
    await contender.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(contender.IsCompletedSuccessfully).IsTrue()
      .Because("an abandoned apply that fails has ended all the same, so its gate goes free");
  }

  [Test]
  public async Task WithStreamAffinityGate_HandedOverApplyAlreadyEnded_ReleasesTheGateOnReturnAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.FinishedHandOverPerspective";
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var (worker, _, _) = _buildBranchWorker(new RecordingWorkCoordinator(), registry, new BranchSetup());

    await worker.WithStreamAffinityGateAsync(streamId, perspectiveName, keepHeldUntil => {
      keepHeldUntil(Task.CompletedTask);
      return Task.CompletedTask;
    }, CancellationToken.None);

    await Assert.That(worker.SnapshotAffinityHolds(TimeSpan.Zero)).IsEmpty()
      .Because("an apply that has already ended holds nothing past the body");
  }

  [Test]
  public async Task OnCursorCacheStreamsEvicted_GateHeldByARunningApply_StaysAndKeepsSerializingAsync() {
    // The cursor cache evicts a stream after its cursor has been idle for a while, which a long or hung
    // apply can outlast. Dropping the held gate let the next applier take a fresh one and run concurrently.
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.EvictedWhileHeldPerspective";
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var (worker, _, _) = _buildBranchWorker(new RecordingWorkCoordinator(), registry, new BranchSetup());
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondEntered = false;

    var holder = worker.WithStreamAffinityGateAsync(streamId, perspectiveName, _ => release.Task, CancellationToken.None);
    worker.OnCursorCacheStreamsEvicted([streamId]);

    await Assert.That(worker.HasStreamAffinityGate(streamId, perspectiveName)).IsTrue()
      .Because("a gate an apply is holding is never evicted out from under it");
    var contender = worker.WithStreamAffinityGateAsync(streamId, perspectiveName, _ => {
      secondEntered = true;
      return Task.CompletedTask;
    }, CancellationToken.None);
    await Assert.That(secondEntered).IsFalse()
      .Because("the next applier still parks behind the running apply");

    release.TrySetResult();
    await Task.WhenAll(holder, contender).WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(secondEntered).IsTrue();
  }

  // ------------------------------------------------------------------
  // Post-lifecycle firing (internal seam)
  // ------------------------------------------------------------------

  [Test]
  public async Task FirePostLifecycleDetached_WithCoordinator_FiresOnlyCompleteTrackedEventsAndCountsThemAsync() {
    var coordinator = new LifecycleMarkingCoordinator();
    var registry = new SingleRunnerRegistry("Deep.LifecyclePerspective", new RecordingRunner(), [typeof(DeepChannelEvent)]);
    using var meterFactory = new TestMeterFactory();
    var coordinatorMetrics = new LifecycleCoordinatorMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, _, provider) = _buildBranchWorker(coordinator, registry, new BranchSetup { CoordinatorMetrics = coordinatorMetrics });

    var incomplete = Guid.CreateVersion7();
    var untracked = Guid.CreateVersion7();
    var fires = Guid.CreateVersion7();
    var throws = Guid.CreateVersion7();
    var lifecycle = new BranchLifecycleCoordinator();
    lifecycle.Incomplete.Add(incomplete);
    lifecycle.Untracked.Add(untracked);
    lifecycle.FailOnAdvance.Add(throws);
    var batch = new ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();
    foreach (var id in new[] { incomplete, untracked, fires, throws }) {
      batch[id] = (_envelope(id, new DeepChannelEvent("post-lifecycle")), Guid.CreateVersion7());
    }

    await worker.FirePostLifecycleDetachedAsync(batch, lifecycle, receptorInvoker: null, [], provider, CancellationToken.None);

    await Assert.That(lifecycle.Advanced.ContainsKey(incomplete)).IsFalse()
      .Because("an event some perspective has not finished stays tracked for a later batch");
    await Assert.That(string.Join(",", lifecycle.Advanced[fires])).IsEqualTo(string.Join(",", _terminalStages))
      .Because("a complete, tracked event fires every terminal stage in order");
    await Assert.That(coordinator.RecordedLifecycleCompletions.Count).IsEqualTo(1)
      .Because("only the event whose stages all fired gets a durable completion marker");
    await Assert.That(coordinator.RecordedLifecycleCompletions).Contains(fires);
    await Assert.That(_branchSum(meters, "whizbang.lifecycle_coordinator.post_all_perspectives_fired")).IsEqualTo(1d);
    await Assert.That(_branchSum(meters, "whizbang.lifecycle_coordinator.post_lifecycle_fired")).IsEqualTo(1d);
    await Assert.That(_branchSum(meters, "whizbang.lifecycle_coordinator.post_lifecycle_errors")).IsEqualTo(1d)
      .Because("the event whose stage threw is isolated and counted, and the others still ran");
  }

  [Test]
  public async Task FirePostLifecycleDetached_WithCoordinatorWhoseEveryStepSuspends_StillFiresEveryStageAndRecordsCompletionAsync() {
    // Every await inside the per-event try suspends once (security context, the four stage advances and the
    // completion record), so the post-lifecycle loop resumes into that try at each step. Completed-task fakes
    // never take those resume paths.
    var coordinator = new LifecycleMarkingCoordinator { CompleteAsynchronously = true };
    var security = new YieldingSecurityContextProvider();
    var registry = new SingleRunnerRegistry("Deep.SuspendingLifecyclePerspective", new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var (worker, _, provider) = _buildBranchWorker(coordinator, registry, new BranchSetup {
      Services = services => services.AddSingleton<Whizbang.Core.Security.IMessageSecurityContextProvider>(security)
    });

    var incomplete = Guid.CreateVersion7();
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    var lifecycle = new BranchLifecycleCoordinator { AdvanceAsynchronously = true };
    lifecycle.Incomplete.Add(incomplete);
    var batch = new ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();
    foreach (var id in new[] { incomplete, first, second }) {
      batch[id] = (_envelope(id, new DeepChannelEvent("suspending")), Guid.CreateVersion7());
    }

    await worker.FirePostLifecycleDetachedAsync(batch, lifecycle, receptorInvoker: null, [], provider, CancellationToken.None);

    await Assert.That(security.Calls).IsEqualTo(2)
      .Because("the security context is established for each complete event and never for the incomplete one");
    await Assert.That(string.Join(",", lifecycle.Advanced[first])).IsEqualTo(string.Join(",", _terminalStages))
      .Because("an advance that suspends still fires every terminal stage, in order");
    await Assert.That(string.Join(",", lifecycle.Advanced[second])).IsEqualTo(string.Join(",", _terminalStages));
    await Assert.That(lifecycle.Advanced.ContainsKey(incomplete)).IsFalse();
    await Assert.That(coordinator.RecordedLifecycleCompletions.Count).IsEqualTo(2)
      .Because("the loop resumes after each suspended record and moves on to the next event");
    await Assert.That(coordinator.RecordedLifecycleCompletions).Contains(first);
    await Assert.That(coordinator.RecordedLifecycleCompletions).Contains(second);
  }

  private static readonly LifecycleStage[] _terminalStages = [
    LifecycleStage.PostAllPerspectivesDetached,
    LifecycleStage.PostAllPerspectivesInline,
    LifecycleStage.PostLifecycleDetached,
    LifecycleStage.PostLifecycleInline
  ];

  [Test]
  public async Task FirePostLifecycleDetached_FallbackWithAnIsNewMap_MarksAlreadyProcessedEventsAsReplayAsync() {
    var registry = new SingleRunnerRegistry("Deep.FallbackMapPerspective", new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var (worker, _, provider) = _buildBranchWorker(new RecordingWorkCoordinator(), registry, new BranchSetup());
    var replayed = Guid.CreateVersion7();
    var unlisted = Guid.CreateVersion7();
    var batch = _postLifecycleBatch(replayed, unlisted);
    var invoker = new ContextCapturingInvoker();

    await worker.FirePostLifecycleDetachedAsync(
      batch, lifecycleCoordinator: null, invoker, [], provider, CancellationToken.None,
      batchIsNewByEventId: new Dictionary<Guid, bool> { [replayed] = false });

    var replayedContext = invoker.ContextAt(replayed, LifecycleStage.PostLifecycleInline);
    await Assert.That(replayedContext).IsNotNull();
    await Assert.That(replayedContext!.IsNewEvent).IsFalse()
      .Because("the batch recorded this event as already processed, so it is not new");
    await Assert.That(replayedContext.ProcessingMode).IsEqualTo(ProcessingMode.Replay)
      .Because("a not-new event fires in replay mode so non-idempotent receptors are suppressed");
    var unlistedContext = invoker.ContextAt(unlisted, LifecycleStage.PostLifecycleInline);
    await Assert.That(unlistedContext).IsNotNull();
    await Assert.That(unlistedContext!.IsNewEvent).IsTrue()
      .Because("an event the map does not mention is new by default");
    await Assert.That(unlistedContext.ProcessingMode).IsNull();
  }

  [Test]
  public async Task FirePostLifecycleDetached_FallbackWithoutAnIsNewMap_TreatsEveryEventAsNewAsync() {
    var registry = new SingleRunnerRegistry("Deep.FallbackNoMapPerspective", new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var (worker, _, provider) = _buildBranchWorker(new RecordingWorkCoordinator(), registry, new BranchSetup());
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    var invoker = new ContextCapturingInvoker();

    await worker.FirePostLifecycleDetachedAsync(
      _postLifecycleBatch(first, second), lifecycleCoordinator: null, invoker, [], provider, CancellationToken.None);

    foreach (var id in new[] { first, second }) {
      var context = invoker.ContextAt(id, LifecycleStage.PostLifecycleInline);
      await Assert.That(context).IsNotNull();
      await Assert.That(context!.IsNewEvent).IsTrue()
        .Because("with no map there is no record of prior processing, so every event is new");
      await Assert.That(context.ProcessingMode).IsNull();
    }
  }

  // ------------------------------------------------------------------
  // Detached stages whose scope has no receptor invoker
  // ------------------------------------------------------------------

  [Test]
  public async Task FireDetachedStageStatic_NoInvokerRegistered_CompletesWithoutInvokingOrLoggingAsync() {
    var logger = new FakeLogger<PerspectiveWorker>();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<ILogger<PerspectiveWorker>>(logger);
    var serviceProvider = services.BuildServiceProvider();
    var eventId = Guid.CreateVersion7();

    var stage = PerspectiveWorker.FireDetachedStageStaticAsync(
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      _envelope(eventId, new DeepChannelEvent("no-invoker")),
      LifecycleStage.PostLifecycleDetached,
      new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PostLifecycleDetached,
        MessageSource = MessageSource.Local,
        AttemptNumber = 1
      });
    await stage.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(stage.IsCompletedSuccessfully).IsTrue();
    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Error)).IsFalse()
      .Because("a host with no receptors has nothing to fire; reaching for one anyway would fail and log a "
        + "detached-stage error for a stage that was never meant to run");
  }

  [Test]
  public async Task ProcessChannelBatch_DetachedScopeHasNoInvoker_SkipsTheDetachedPrePerspectiveStageAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.DetachedScopePerspective";
    var coordinator = new RecordingWorkCoordinator();
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var eventStore = new SequencedEventStore();
    eventStore.EnqueueResponse([_envelope(Guid.CreateVersion7(), new DeepChannelEvent("detached"))]);
    var invoker = new StageRecordingInvoker();
    var logger = new FakeLogger<PerspectiveWorker>();
    // The batch scope and the group scope resolve an invoker; the detached stage's own scope, created in
    // the group's flow after it, does not (see _invokerForTheFirstTwoResolutionsInAFlow).
    var (worker, _, _) = _buildBranchWorker(coordinator, registry, new BranchSetup {
      EventStore = eventStore,
      Logger = logger,
      Services = services => services.AddScoped<IReceptorInvoker>(_ => _invokerForTheFirstTwoResolutionsInAFlow(invoker))
    });

    await worker.ProcessChannelBatchAsync([_branchWork(streamId, perspectiveName)], CancellationToken.None);
    await worker.DrainDetachedAsync();

    await Assert.That(invoker.HasFired(LifecycleStage.PrePerspectiveInline)).IsTrue()
      .Because("the group's own scope had an invoker, so the inline pre-perspective stage ran through it");
    await Assert.That(invoker.HasFired(LifecycleStage.PrePerspectiveDetached)).IsFalse()
      .Because("the detached stage's scope had no invoker, so there was nothing to run the stage on");
    await Assert.That(logger.Collector.GetSnapshot().Any(r =>
      r.Level == LogLevel.Error && r.Message.Contains("Detached lifecycle stage", StringComparison.Ordinal))).IsFalse()
      .Because("a missing invoker is a skip, not a failed stage");
  }

  private static readonly AsyncLocal<int> _invokerResolutionsInFlow = new();

  /// <summary>
  /// Returns the invoker for the first two resolutions in an execution flow and null after that. The
  /// counter is set synchronously from the resolving call, so it persists in that caller's flow and in any
  /// task it starts afterwards, but never leaks back into its own caller: the batch scope resolves first,
  /// the group scope (whose flow inherits the batch's count) second, and the detached task the group
  /// starts afterwards third.
  /// </summary>
  private static IReceptorInvoker _invokerForTheFirstTwoResolutionsInAFlow(IReceptorInvoker invoker) {
    var seen = _invokerResolutionsInFlow.Value;
    _invokerResolutionsInFlow.Value = seen + 1;
    return seen < 2 ? invoker : null!;
  }

  // ------------------------------------------------------------------
  // Lock keepalive and idle accounting
  // ------------------------------------------------------------------

  [Test]
  public async Task StartLockKeepalive_TokenCanceledDuringARenewal_StopsAfterThatRenewalAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.KeepalivePerspective";
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    var locker = new CancelOnRenewLocker();
    var (worker, _, _) = _buildBranchWorker(new RecordingWorkCoordinator(), registry, new BranchSetup {
      StreamLocker = locker,
      StreamLockOptions = new PerspectiveStreamLockOptions { KeepAliveInterval = TimeSpan.Zero }
    });
    using var cts = new CancellationTokenSource();
    locker.OnRenew = cts.Cancel;

    await worker.StartLockKeepaliveAsync(streamId, perspectiveName, cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(locker.RenewCount).IsEqualTo(1)
      .Because("once the keepalive's token is canceled it stops, even when the renewal itself returned normally");
  }

  [Test]
  public async Task Worker_IdleTicksWithMetrics_CountEveryEmptyPollAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.IdlePerspective";
    var coordinator = new RecordingWorkCoordinator();
    var registry = new SingleRunnerRegistry(perspectiveName, new RecordingRunner(), [typeof(DeepChannelEvent)]);
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var workerOptions = new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 20, MaxConcurrentDrainConsumers = 1 };
    var (worker, harness, _) = _buildBranchWorker(coordinator, registry, new BranchSetup {
      Metrics = metrics,
      WorkerOptions = workerOptions
    });
    var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idle.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueWorkAsync(_branchWork(streamId, perspectiveName), cts.Token);
    await idle.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(_branchSum(meters, "whizbang.perspective.empty_batches")).IsGreaterThanOrEqualTo(workerOptions.IdleThresholdPolls)
      .Because("the worker goes idle only after IdleThresholdPolls empty polls, and each of them is counted");
  }

  // ------------------------------------------------------------------
  // Helpers
  // ------------------------------------------------------------------

  private sealed record BranchSetup {
    public IEventStore? EventStore { get; init; }
    public IReceptorInvoker? ReceptorInvoker { get; init; }
    public TracingOptions? Tracing { get; init; }
    public PerspectiveMetrics? Metrics { get; init; }
    public LifecycleCoordinatorMetrics? CoordinatorMetrics { get; init; }
    public WorkCompletionMeter? CompletionMeter { get; init; }
    public ILogger<PerspectiveWorker>? Logger { get; init; }
    public IPerspectiveSnapshotStore? SnapshotStore { get; init; }
    public IPerspectiveStreamLocker? StreamLocker { get; init; }
    public PerspectiveStreamLockOptions? StreamLockOptions { get; init; }
    public Action<IServiceCollection>? Services { get; init; }
    public PerspectiveWorkerOptions? WorkerOptions { get; init; }
  }

  private static (PerspectiveWorker Worker, PerspectiveWorkerTestHarness Harness, ServiceProvider Provider) _buildBranchWorker(
      IWorkCoordinator coordinator, IPerspectiveRunnerRegistry registry, BranchSetup setup) {
    var instanceProvider = new FakeInstanceProvider();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton(coordinator);
    services.AddSingleton(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    if (setup.EventStore is not null) {
      services.AddSingleton(setup.EventStore);
    }
    if (setup.ReceptorInvoker is not null) {
      services.AddSingleton(setup.ReceptorInvoker);
    }
    setup.Services?.Invoke(services);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var options = setup.WorkerOptions ?? new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 };
    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(options),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(setup.Tracing ?? new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: setup.Logger ?? NullLogger<PerspectiveWorker>.Instance,
      snapshotStore: setup.SnapshotStore ?? NullPerspectiveSnapshotStore.Instance,
      streamLocker: setup.StreamLocker ?? NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(setup.StreamLockOptions ?? new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      leaseRenewalChannel: harness.LeaseRenewalCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor(options),
      metrics: setup.Metrics,
      coordinatorMetrics: setup.CoordinatorMetrics,
      completionMeter: setup.CompletionMeter);
    return (worker, harness, serviceProvider);
  }

  private static PerspectiveWork _branchWork(
      Guid streamId, string perspectiveName, PerspectiveProcessingStatus status = PerspectiveProcessingStatus.None) => new() {
        WorkId = Guid.CreateVersion7(),
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastProcessedEventId = null,
        PartitionNumber = 1,
        Status = status
      };

  private static RecordingWorkCoordinator _rewindCoordinator(Guid streamId, string perspectiveName, Guid triggerEventId) {
    var coordinator = new RecordingWorkCoordinator();
    coordinator.CursorOverrides[(perspectiveName, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = Guid.CreateVersion7(),
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = triggerEventId
    };
    return coordinator;
  }

  private static ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> _postLifecycleBatch(params Guid[] eventIds) {
    var batch = new ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();
    foreach (var id in eventIds) {
      batch[id] = (_envelope(id, new DeepChannelEvent("fallback")), Guid.CreateVersion7());
    }
    return batch;
  }

  private static TracingOptions _perspectiveTracing() => new() {
    EnableWorkerBatchSpans = false,
    Verbosity = TraceVerbosity.Normal,
    Components = TraceComponents.Perspectives
  };

  private static ActivityListener _listenToWhizbangTracing(ConcurrentBag<Activity> stopped) {
    // Resolve the source name before registering: see Worker_BatchSpansWithListener_SetsBatchTagsAndExtractsTraceContextAsync.
    var tracingSourceName = WhizbangActivitySource.Tracing.Name;
    var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == tracingSourceName,
      Sample = (ref _) => ActivitySamplingResult.AllData,
      ActivityStopped = stopped.Add,
    };
    ActivitySource.AddActivityListener(listener);
    return listener;
  }

  private static string? _tag(Activity activity, string key) => activity.GetTagItem(key)?.ToString();

  private static double _branchSum(MetricAssertionHelper meters, string instrumentName) =>
    meters.GetByName(instrumentName).Sum(m => m.Value);

  // ------------------------------------------------------------------
  // Fakes
  // ------------------------------------------------------------------

  /// <summary>Records durable lifecycle completion markers; everything else is inert.</summary>
  private sealed class LifecycleMarkingCoordinator : IWorkCoordinator {
    public ConcurrentQueue<Guid> RecordedLifecycleCompletions { get; } = new();
    /// <summary>When true, recording a completion suspends before it finishes, so the caller resumes.</summary>
    public bool CompleteAsynchronously { get; init; }

    public Task RecordLifecycleCompletionAsync(Guid eventId, CancellationToken cancellationToken = default) {
      RecordedLifecycleCompletions.Enqueue(eventId);
      return CompleteAsynchronously ? _yieldOnceAsync() : Task.CompletedTask;
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>A lifecycle coordinator whose per-event answers a test scripts.</summary>
  private sealed class BranchLifecycleCoordinator : ILifecycleCoordinator {
    public HashSet<Guid> Incomplete { get; } = [];
    public HashSet<Guid> Untracked { get; } = [];
    public HashSet<Guid> FailOnAdvance { get; } = [];
    public ConcurrentDictionary<Guid, ConcurrentQueue<LifecycleStage>> Advanced { get; } = new();
    /// <summary>When true, every stage advance suspends before it finishes, so the caller resumes.</summary>
    public bool AdvanceAsynchronously { get; init; }

    public ILifecycleTracking BeginTracking(Guid eventId, IMessageEnvelope envelope, LifecycleStage entryStage, MessageSource source, Guid? streamId = null, Type? perspectiveType = null) =>
      new BranchTracking(this, eventId);

    public ILifecycleTracking? GetTracking(Guid eventId) =>
      Untracked.Contains(eventId) ? null : new BranchTracking(this, eventId);

    public void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources) { }
    public ValueTask SignalSegmentCompleteAsync(Guid eventId, PostLifecycleCompletionSource source, IServiceProvider scopedProvider, CancellationToken ct) =>
      ValueTask.CompletedTask;
    public void AbandonTracking(Guid eventId) { }
    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) { }
    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) => false;
    public bool AreAllPerspectivesComplete(Guid eventId) => !Incomplete.Contains(eventId);
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) => 0;

    private sealed class BranchTracking(BranchLifecycleCoordinator owner, Guid eventId) : ILifecycleTracking {
      public Guid EventId => eventId;
      public LifecycleStage CurrentStage => LifecycleStage.PrePerspectiveDetached;
      public bool IsComplete => false;

      public ValueTask AdvanceToAsync(LifecycleStage stage, IServiceProvider scopedProvider, CancellationToken ct) {
        if (owner.FailOnAdvance.Contains(eventId)) {
          throw new InvalidOperationException($"scripted post-lifecycle failure at {stage}");
        }
        owner.Advanced.GetOrAdd(eventId, static _ => new ConcurrentQueue<LifecycleStage>()).Enqueue(stage);
        return owner.AdvanceAsynchronously ? new ValueTask(_yieldOnceAsync()) : ValueTask.CompletedTask;
      }

      public ValueTask DrainDetachedAsync() => ValueTask.CompletedTask;
    }
  }

  /// <summary>Completes after one real suspension, so the awaiting state machine takes its resume path.</summary>
  private static async Task _yieldOnceAsync() => await Task.Yield();

  /// <summary>A security provider that establishes no context, after suspending once, and counts its calls.</summary>
  private sealed class YieldingSecurityContextProvider : Whizbang.Core.Security.IMessageSecurityContextProvider {
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    public async ValueTask<Whizbang.Core.Security.IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope, IServiceProvider scopedProvider, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _calls);
      await Task.Yield();
      return null;
    }
  }

  /// <summary>Captures the lifecycle context each event was invoked with, per stage.</summary>
  private sealed class ContextCapturingInvoker : IReceptorInvoker {
    private readonly ConcurrentDictionary<(Guid EventId, LifecycleStage Stage), ILifecycleContext?> _contexts = new();

    public ILifecycleContext? ContextAt(Guid eventId, LifecycleStage stage) =>
      _contexts.TryGetValue((eventId, stage), out var context) ? context : null;

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      _contexts[(envelope.MessageId.Value, stage)] = context;
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>A configured locker whose renewal runs a test hook and is counted.</summary>
  private sealed class CancelOnRenewLocker : IPerspectiveStreamLocker {
    private int _renewCount;
    public int RenewCount => Volatile.Read(ref _renewCount);
    public Action? OnRenew { get; set; }

    public Task<bool> TryAcquireLockAsync(Guid streamId, string perspectiveName, Guid instanceId, string reason, CancellationToken ct = default) =>
      Task.FromResult(true);

    public Task RenewLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) {
      Interlocked.Increment(ref _renewCount);
      OnRenew?.Invoke();
      return Task.CompletedTask;
    }

    public Task ReleaseLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
  }
}
