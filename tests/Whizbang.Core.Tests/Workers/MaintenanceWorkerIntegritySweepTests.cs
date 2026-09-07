using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A stream-integrity feature that is off leaves nothing behind. Every maintenance cycle the worker asks
/// <see cref="IntegrityTraffic"/> which control-plane types belong to features that are off under the
/// current <see cref="StreamIntegrityOptions"/> and discards their pending rows from the inbox
/// (<see cref="IWorkCoordinator.DiscardPendingInboxMessagesAsync"/>) and the outbox
/// (<see cref="IWorkCoordinator.DiscardPendingOutboxMessagesAsync"/>). Report-only is the default, so
/// repair traffic is swept out of the box; with everything on nothing is touched. The sweep is
/// best-effort like every other maintenance step: a failing sweep is logged with its consequence and
/// the cycle continues.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerIntegritySweepTests {
  private sealed class SweepCoordinator : IWorkCoordinator {
    public Exception? DiscardThrows { get; init; }
    public long InboxDiscarded { get; init; }
    public long OutboxDiscarded { get; init; }
    public List<IReadOnlyList<string>> InboxCalls { get; } = [];
    public List<IReadOnlyList<string>> OutboxCalls { get; } = [];
    public int MaintenanceCalls;

    public Task<long> DiscardPendingInboxMessagesAsync(
        IReadOnlyList<string> messageTypeNames, CancellationToken cancellationToken = default) {
      lock (InboxCalls) { InboxCalls.Add(messageTypeNames); }
      return DiscardThrows is not null ? Task.FromException<long>(DiscardThrows) : Task.FromResult(InboxDiscarded);
    }
    public Task<long> DiscardPendingOutboxMessagesAsync(
        IReadOnlyList<string> messageTypeNames, CancellationToken cancellationToken = default) {
      lock (OutboxCalls) { OutboxCalls.Add(messageTypeNames); }
      return DiscardThrows is not null ? Task.FromException<long>(DiscardThrows) : Task.FromResult(OutboxDiscarded);
    }
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref MaintenanceCalls);
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] m, int partitionCount, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private static StreamIntegrityOptions _everythingOn() => new() {
    RepairMode = IntegrityRepairMode.AutoRepairCapped,
    CheckpointsEnabled = true,
    GapDetectionEnabled = true,
    AuditEnabled = true,
    PublishReportEvents = true,
  };

  private static (MaintenanceWorker Worker, CapturingLogger<MaintenanceWorker> Logger) _build(
      SweepCoordinator coord, StreamIntegrityOptions? integrity, StreamIntegrityMetrics? metrics = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (integrity is not null) {
      services.AddSingleton(Options.Create(integrity));
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new CapturingLogger<MaintenanceWorker>();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      logger);
    return (worker, logger);
  }

  [Test]
  public async Task ReportOnly_DiscardsParkedRepairRowsFromBothTablesAndLogsTheCountsAsync() {
    var coord = new SweepCoordinator { InboxDiscarded = 3, OutboxDiscarded = 2 };
    var options = _everythingOn();
    options.RepairMode = IntegrityRepairMode.ReportOnly;
    var (worker, logger) = _build(coord, options);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.InboxCalls.Count).IsEqualTo(1);
    await Assert.That(coord.InboxCalls[0]).IsEquivalentTo(IntegrityTraffic.InboxTypesToDiscard(options))
      .Because("the sweep names exactly the types of features that are off; with only repair off, that is repair traffic");
    await Assert.That(coord.OutboxCalls.Count).IsEqualTo(1);
    await Assert.That(coord.OutboxCalls[0]).IsEquivalentTo(IntegrityTraffic.OutboxTypesToDiscard(options));
    var entries = logger.Snapshot().Where(e => e.Level == LogLevel.Information && e.Message.Contains("Discarded", StringComparison.Ordinal)).ToList();
    await Assert.That(entries.Any(e => e.Message.Contains("Discarded 3", StringComparison.Ordinal) && e.Message.Contains("inbox", StringComparison.Ordinal))).IsTrue()
      .Because("an operator must see rows being dropped, from which table, and why");
    await Assert.That(entries.Any(e => e.Message.Contains("Discarded 2", StringComparison.Ordinal) && e.Message.Contains("outbox", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task EverythingOn_SweepsNothingAsync() {
    var coord = new SweepCoordinator { InboxDiscarded = 3, OutboxDiscarded = 3 };
    var (worker, _) = _build(coord, _everythingOn());

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.InboxCalls).IsEmpty()
      .Because("a feature that is on owns its traffic; the sweep must not race the repair or detection paths");
    await Assert.That(coord.OutboxCalls).IsEmpty();
  }

  [Test]
  public async Task CheckpointsOff_SweepsUnpublishedCheckpointsFromTheOutboxAsync() {
    var coord = new SweepCoordinator { OutboxDiscarded = 7 };
    var options = _everythingOn();
    options.CheckpointsEnabled = false;
    var (worker, logger) = _build(coord, options);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.InboxCalls).IsEmpty();
    await Assert.That(coord.OutboxCalls.Count).IsEqualTo(1);
    await Assert.That(coord.OutboxCalls[0]).IsEquivalentTo(IntegrityTraffic.OutboxTypesToDiscard(options));
    await Assert.That(logger.Snapshot().Any(e => e.Message.Contains("Discarded 7", StringComparison.Ordinal) && e.Message.Contains("outbox", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task NoIntegrityOptionsRegistered_SweepsAsTheDefaultsAsync() {
    var coord = new SweepCoordinator();
    var (worker, _) = _build(coord, integrity: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.InboxCalls.Count).IsEqualTo(1)
      .Because("absent options read as the defaults: report-only, so a bundle broadcast by a peer must not be folded in");
    await Assert.That(coord.InboxCalls[0]).IsEquivalentTo(IntegrityTraffic.InboxTypesToDiscard(null));
    await Assert.That(coord.OutboxCalls.Count).IsEqualTo(1);
  }

  [Test]
  public async Task NothingPending_LogsNothingAsync() {
    var coord = new SweepCoordinator { InboxDiscarded = 0, OutboxDiscarded = 0 };
    var (worker, logger) = _build(coord, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.InboxCalls.Count).IsEqualTo(1);
    await Assert.That(logger.Snapshot().Any(e => e.Message.Contains("Discarded", StringComparison.Ordinal))).IsFalse()
      .Because("a quiet sweep is the steady state; logging it every cycle is noise");
  }

  [Test]
  public async Task SweepFailing_DoesNotFailTheCycleAndNamesTheConsequenceAsync() {
    var coord = new SweepCoordinator { DiscardThrows = new InvalidOperationException("boom") };
    var (worker, logger) = _build(coord, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.MaintenanceCalls).IsEqualTo(1)
      .Because("the sweep is best-effort; the rest of the cycle still runs");
    var entry = logger.Snapshot().FirstOrDefault(e =>
      e.Level == LogLevel.Warning && e.Message.Contains("sweep failed", StringComparison.Ordinal));
    await Assert.That(entry).IsNotNull();
    await Assert.That(entry!.Exception).IsNotNull();
    await Assert.That(entry.Message).Contains("next cycle")
      .Because("the log states what failed and what happens to the pending rows as a result");
  }

  [Test]
  public async Task SweepCanceled_PropagatesCancellation_AndStopsTheCycleAsync() {
    var coord = new SweepCoordinator { DiscardThrows = new OperationCanceledException() };
    var (worker, logger) = _build(coord, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly });

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("cancellation is shutdown, not a sweep failure; best-effort must not swallow it");
    await Assert.That(coord.MaintenanceCalls).IsEqualTo(0)
      .Because("a canceled cycle stops; nothing after the sweep runs");
    await Assert.That(logger.Snapshot().Any(e => e.Message.Contains("sweep failed", StringComparison.Ordinal))).IsFalse()
      .Because("shutdown is not reported as a failure");
  }

  [Test]
  public async Task ReportOnly_CountsTheDiscardsOnTheMetric_PerTableAsync() {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics());
    var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.RepairTrafficDiscarded)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) => {
      var table = "?";
      foreach (var tag in tags) {
        if (tag.Key == "table") {
          table = tag.Value?.ToString() ?? "?";
        }
      }
      counts.AddOrUpdate(table, measurement, (_, v) => v + measurement);
    });
    listener.Start();
    var coord = new SweepCoordinator { InboxDiscarded = 3, OutboxDiscarded = 2 };
    var options = _everythingOn();
    options.RepairMode = IntegrityRepairMode.ReportOnly;
    var (worker, _) = _build(coord, options, metrics);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(counts.GetValueOrDefault("inbox")).IsEqualTo(3L)
      .Because("the metric is what a dashboard sees; it counts rows per table");
    await Assert.That(counts.GetValueOrDefault("outbox")).IsEqualTo(2L);
  }
}
