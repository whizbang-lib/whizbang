// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The level each of the collective sink's two contained failures logs at (#1976). A failed apply is reported as a
/// perspective failure, so it is retried and dead-lettered at the bound: the attempt is a Warning (EventId 14), and
/// Error is kept for the dead-letter record. A post-apply receptor that throws is not retried, since the apply
/// already committed, so it stays the lifecycle failure it is (EventId 22, Error).
/// </summary>
public partial class PerspectiveWorkerCollectiveSinkTests {

  private const int APPLY_FAILURE_EVENT_ID = 14;
  private const int LIFECYCLE_FAILURE_EVENT_ID = 22;

  [Test]
  [Timeout(30000)]
  public async Task CollectiveSink_DispatchThrows_LogsTheAttemptAtWarningWithItsExceptionAsync(CancellationToken cancellationToken) {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var envelope = _envelope(eventId, new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") });
    var logger = new DeadLetterRecordLogger<PerspectiveWorker>();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new EventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: new ThrowingDispatcher(),
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId)],
      logger: logger);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    // The attempt is logged before the failure is reported, so the report is the signal.
    await coordinator.FirstFailure.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    var attempts = logger.Entries.Where(e => e.EventId == APPLY_FAILURE_EVENT_ID).ToList();
    await Assert.That(attempts.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(attempts.All(e => e.Level == LogLevel.Warning)).IsTrue()
      .Because("a failed collective apply is retried and dead-lettered at the bound, so each attempt is a Warning");
    await Assert.That(attempts.All(e => e.Exception is not null)).IsTrue()
      .Because("the attempt's exception is attached, so the Warning says what failed");
  }

  [Test]
  [Timeout(30000)]
  public async Task CollectiveSink_PostApplyReceptorThrows_LogsTheLifecycleFailureAtErrorAsync(CancellationToken cancellationToken) {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var sinkWork = _sinkWork(streamId);
    var invoker = new ThrowingReceptorInvoker();
    var logger = new DeadLetterRecordLogger<PerspectiveWorker>();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") })] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: new RecordingDispatcher(),
      receptorInvoker: invoker,
      logger: logger);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await invoker.FirstInvoke.WaitAsync(cancellationToken);
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    // The failure is logged on the worker's thread right after the receptor throws; the body finishing is what
    // proves it has been.
    await worker.ExecuteTask!.WaitAsync(cancellationToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    var lifecycle = logger.Entries.Where(e => e.EventId == LIFECYCLE_FAILURE_EVENT_ID).ToList();
    await Assert.That(lifecycle.Count).IsEqualTo(1);
    await Assert.That(lifecycle[0].Level).IsEqualTo(LogLevel.Error)
      .Because("the apply committed and the receptor is not run again, so its failure is final and stays an Error");
    await Assert.That(lifecycle[0].Exception).IsNotNull();
    await Assert.That(logger.Entries.Any(e => e.EventId == APPLY_FAILURE_EVENT_ID)).IsFalse()
      .Because("a lifecycle receptor failure is not a failed apply attempt");
  }
}
