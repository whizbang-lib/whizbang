// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2601: a queued row leased many times with no failure recorded is one nothing ever runs: the dead-letter bound reads
/// failures, so it never applies, and the row was invisible. The drain warns about it once per row and process, naming
/// the perspective, the stream, the event and the leases, and counts it on
/// <c>whizbang.perspective.rows_leased_without_failure</c>, once <see cref="PerspectiveWorkerOptions.LeasedWithoutFailureWarningAttempts"/>
/// leases are reached. A row that failed is the dead-letter bound's, not this warning's.
/// </summary>
public partial class PerspectiveWorkerDeepPathDrainTests {
  private const int LEASED_WITHOUT_FAILURE_EVENT_ID = 94;
  private const string LEASED_WITHOUT_FAILURE_METRIC = "whizbang.perspective.rows_leased_without_failure";

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_ARowLeasedToTheWarningBound_WithNoFailure_IsWarnedOnceAndCountedAsync(CancellationToken cancellationToken) {
    var (stuckStream, stuckEvent, stuckWork) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var (belowStream, belowEvent, belowWork) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var (failedStream, failedEvent, failedWork) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var (laterStream, laterEvent, laterWork) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var stuck = _raw(stuckStream, stuckEvent, stuckWork) with { Attempts = 5 };
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      stuck,
      _raw(belowStream, belowEvent, belowWork) with { Attempts = 4 },
      _raw(failedStream, failedEvent, failedWork) with { Attempts = 9, Failures = 1 }]);
    // The same row again in a later batch, with a row whose completion says that batch was handled.
    coordinator.EnqueueStreamEvents([stuck with { Attempts = 6 }, _raw(laterStream, laterEvent, laterWork)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([
      _envelope(stuckEvent, new DrainDeepEvent("stuck")), _envelope(belowEvent, new DrainDeepEvent("below")),
      _envelope(failedEvent, new DrainDeepEvent("failed"))]);
    eventStore.EnqueueDeserialized([_envelope(stuckEvent, new DrainDeepEvent("stuck")), _envelope(laterEvent, new DrainDeepEvent("later"))]);
    var logger = new FakeLogger<PerspectiveWorker>();
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, harness, _) = _createWorker(coordinator, eventStore, _registry(new DrainRunner()),
      configure: o => {
        o.LeasedWithoutFailureWarningAttempts = 5;
        o.DrainLoopMaxIterations = 1;
      },
      logger: logger, metrics: metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(stuckStream, cts.Token);
    await harness.CompletionCapture.WaitForEventWorkIdsAsync(_signalWait, stuckWork, belowWork, failedWork);
    await harness.EnqueueDrainStreamAsync(laterStream, cts.Token);
    await harness.CompletionCapture.WaitForEventWorkIdsAsync(_signalWait, laterWork);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    var warned = logger.Collector.GetSnapshot().Where(r => r.Id.Id == LEASED_WITHOUT_FAILURE_EVENT_ID).ToList();
    await Assert.That(warned.Count).IsEqualTo(1).Because("the row is warned about once per process, not on every lease");
    await Assert.That(warned[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(warned[0].Message).Contains(PERSPECTIVE).And.Contains(stuckStream.ToString()).And.Contains(stuckEvent.ToString())
      .And.Contains("5 times");
    await Assert.That(meters.GetByName(LEASED_WITHOUT_FAILURE_METRIC).Sum(m => m.Value)).IsEqualTo(1d);
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_TheLeasedWithoutFailureWarning_TurnedOff_WarnsNothingAsync(CancellationToken cancellationToken) {
    var (streamId, eventId, work) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, work) with { Attempts = 500 }]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("stuck"))]);
    var logger = new FakeLogger<PerspectiveWorker>();
    var (worker, harness, _) = _createWorker(coordinator, eventStore, _registry(new DrainRunner()),
      configure: o => o.LeasedWithoutFailureWarningAttempts = null, logger: logger);

    await _drainOnceAsync(worker, harness, streamId, harness.CompletionCapture.WaitForEventWorkIdsAsync(_signalWait, work), cancellationToken);

    await Assert.That(logger.Collector.GetSnapshot().Where(r => r.Id.Id == LEASED_WITHOUT_FAILURE_EVENT_ID)).IsEmpty();
  }
}
