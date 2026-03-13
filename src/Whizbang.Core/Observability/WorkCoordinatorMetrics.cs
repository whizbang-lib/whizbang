using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for the work coordination pipeline: process_work_batch, flush, publisher worker.
/// Meter name: Whizbang.WorkCoordinator
/// </summary>
/// <docs>observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/WorkCoordinatorMetricsTests.cs</tests>
public sealed class WorkCoordinatorMetrics {
#pragma warning disable CA1707
  public const string METER_NAME = "Whizbang.WorkCoordinator";
#pragma warning restore CA1707

  private readonly Meter _meter;

  // Timing
  public Histogram<double> ProcessBatchDuration { get; }
  public Histogram<double> FlushDuration { get; }

  // Batch composition (IN)
  public Histogram<int> BatchOutboxMessages { get; }
  public Histogram<int> BatchInboxMessages { get; }
  public Histogram<int> BatchCompletions { get; }
  public Histogram<int> BatchFailures { get; }

  // Work returned (OUT)
  public Histogram<int> ReturnedOutboxWork { get; }
  public Histogram<int> ReturnedInboxWork { get; }
  public Histogram<int> ReturnedPerspectiveWork { get; }

  // Counters
  public Counter<long> ProcessBatchCalls { get; }
  public Counter<long> ProcessBatchErrors { get; }
  public Counter<long> FlushCalls { get; }
  public Counter<long> EmptyFlushCalls { get; }

  // Publisher worker
  public Counter<long> PublisherLeaseRenewals { get; }
  public Counter<long> PublisherBufferedMessages { get; }

  // Maintenance
  public Histogram<double> MaintenanceTaskDuration { get; }
  public Histogram<long> MaintenanceTaskRowsAffected { get; }

  public WorkCoordinatorMetrics(WhizbangMetrics whizbangMetrics) {
    _meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    ProcessBatchDuration = _meter.CreateHistogram<double>("whizbang.work_coordinator.process_batch.duration", "ms", "Time executing process_work_batch SQL");
    FlushDuration = _meter.CreateHistogram<double>("whizbang.work_coordinator.flush.duration", "ms", "Total FlushAsync time incl. lifecycle");

    BatchOutboxMessages = _meter.CreateHistogram<int>("whizbang.work_coordinator.batch.outbox_messages", description: "Outbox messages sent to process_work_batch");
    BatchInboxMessages = _meter.CreateHistogram<int>("whizbang.work_coordinator.batch.inbox_messages", description: "Inbox messages sent");
    BatchCompletions = _meter.CreateHistogram<int>("whizbang.work_coordinator.batch.completions", description: "Completions sent");
    BatchFailures = _meter.CreateHistogram<int>("whizbang.work_coordinator.batch.failures", description: "Failures sent");

    ReturnedOutboxWork = _meter.CreateHistogram<int>("whizbang.work_coordinator.returned.outbox_work", description: "Outbox work items returned");
    ReturnedInboxWork = _meter.CreateHistogram<int>("whizbang.work_coordinator.returned.inbox_work", description: "Inbox work items returned");
    ReturnedPerspectiveWork = _meter.CreateHistogram<int>("whizbang.work_coordinator.returned.perspective_work", description: "Perspective work items returned");

    ProcessBatchCalls = _meter.CreateCounter<long>("whizbang.work_coordinator.process_batch.calls", description: "Total process_work_batch calls");
    ProcessBatchErrors = _meter.CreateCounter<long>("whizbang.work_coordinator.process_batch.errors", description: "SQL errors");
    FlushCalls = _meter.CreateCounter<long>("whizbang.work_coordinator.flush.calls", description: "Total FlushAsync calls");
    EmptyFlushCalls = _meter.CreateCounter<long>("whizbang.work_coordinator.flush.empty_calls", description: "Flushes with no queued work");

    PublisherLeaseRenewals = _meter.CreateCounter<long>("whizbang.publisher.lease_renewals", description: "Lease renewals due to transport not ready");
    PublisherBufferedMessages = _meter.CreateCounter<long>("whizbang.publisher.buffered_messages", description: "Total messages buffered for publish");

    MaintenanceTaskDuration = _meter.CreateHistogram<double>("whizbang.maintenance.task.duration", "ms", "Duration per maintenance task");
    MaintenanceTaskRowsAffected = _meter.CreateHistogram<long>("whizbang.maintenance.task.rows_affected", description: "Rows cleaned per task");
  }
}
