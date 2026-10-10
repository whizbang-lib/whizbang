// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The collective sink's two contained failures, counted on the perspective error meter: an apply that
/// throws, and a post-apply receptor that throws after the apply committed.
/// </summary>
public partial class PerspectiveWorkerCollectiveSinkTests {

  [Test]
  public async Task CollectiveSink_DispatchThrowsWithMetrics_CountsOneErrorAsync() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var envelope = _envelope(eventId, new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") });
    var dispatcher = new ThrowingDispatcher();
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new EventStore { Envelopes = { [streamId] = [envelope] }, Deserialized = [envelope] },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [streamId],
      streamEvents: [_raw(streamId, eventId)],
      metrics: metrics);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    // The error is counted before the failure is reported, so the report is the signal.
    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(coordinator.ReportedFailures[0].PerspectiveName).IsEqualTo(CollectiveRouting.SINK_PERSPECTIVE_NAME);
    await Assert.That(meters.GetByName("whizbang.perspective.errors").Sum(m => m.Value)).IsGreaterThanOrEqualTo(1d)
      .Because("a failed collective apply is contained, and containing it still counts it as an error");
  }

  [Test]
  public async Task CollectiveSink_PostApplyReceptorThrowsWithMetrics_CountsTheErrorAndKeepsTheCompletionAsync() {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var sinkWork = _sinkWork(streamId);
    var dispatcher = new RecordingDispatcher();
    var invoker = new ThrowingReceptorInvoker();
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [sinkWork],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") })] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      receptorInvoker: invoker,
      metrics: metrics);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await invoker.FirstInvoke.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.FirstEventWorkId.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    // The error is counted on the worker's thread right after the receptor throws; the body finishing is
    // what proves it has been.
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(sinkWork.WorkId)
      .Because("the apply committed, so its row is still completed");
    await Assert.That(meters.GetByName("whizbang.perspective.errors").Sum(m => m.Value)).IsEqualTo(1d)
      .Because("the throwing post-apply receptor is isolated per event and counted once");
  }
}
