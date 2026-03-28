using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for the work coordination pipeline: process_work_batch, flush, publisher worker.
/// Meter name: Whizbang.WorkCoordinator
/// </summary>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/WorkCoordinatorMetricsTests.cs</tests>
public sealed class WorkCoordinatorMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.WorkCoordinator";
#pragma warning restore CA1707


  // Timing

  /// <summary>Time executing process_work_batch SQL.</summary>
  public Histogram<double> ProcessBatchDuration { get; }

  /// <summary>Total FlushAsync time including lifecycle.</summary>
  public Histogram<double> FlushDuration { get; }

  // Batch composition (IN)

  /// <summary>Outbox messages sent to process_work_batch.</summary>
  public Histogram<int> BatchOutboxMessages { get; }

  /// <summary>Inbox messages sent.</summary>
  public Histogram<int> BatchInboxMessages { get; }

  /// <summary>Completions sent.</summary>
  public Histogram<int> BatchCompletions { get; }

  /// <summary>Failures sent.</summary>
  public Histogram<int> BatchFailures { get; }

  // Work returned (OUT)

  /// <summary>Outbox work items returned.</summary>
  public Histogram<int> ReturnedOutboxWork { get; }

  /// <summary>Inbox work items returned.</summary>
  public Histogram<int> ReturnedInboxWork { get; }

  /// <summary>Perspective work items returned.</summary>
  public Histogram<int> ReturnedPerspectiveWork { get; }

  // Counters

  /// <summary>Total process_work_batch calls.</summary>
  public Counter<long> ProcessBatchCalls { get; }

  /// <summary>SQL errors.</summary>
  public Counter<long> ProcessBatchErrors { get; }

  /// <summary>Total FlushAsync calls.</summary>
  public Counter<long> FlushCalls { get; }

  /// <summary>Flushes with no queued work.</summary>
  public Counter<long> EmptyFlushCalls { get; }

  // Publisher worker

  /// <summary>Lease renewals due to transport not ready.</summary>
  public Counter<long> PublisherLeaseRenewals { get; }

  /// <summary>Total messages buffered for publish.</summary>
  public Counter<long> PublisherBufferedMessages { get; }

  // Maintenance

  /// <summary>Duration per maintenance task.</summary>
  public Histogram<double> MaintenanceTaskDuration { get; }

  /// <summary>Rows cleaned per task.</summary>
  public Histogram<long> MaintenanceTaskRowsAffected { get; }

  /// <summary>Initializes a new instance of the <see cref="WorkCoordinatorMetrics"/> class.</summary>
  /// <param name="whizbangMetrics">The shared metrics factory providing the meter.</param>
  public WorkCoordinatorMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    ProcessBatchDuration = meter.CreateHistogram<double>("whizbang.work_coordinator.process_batch.duration", "ms", "Time executing process_work_batch SQL");
    FlushDuration = meter.CreateHistogram<double>("whizbang.work_coordinator.flush.duration", "ms", "Total FlushAsync time incl. lifecycle");

    BatchOutboxMessages = meter.CreateHistogram<int>("whizbang.work_coordinator.batch.outbox_messages", description: "Outbox messages sent to process_work_batch");
    BatchInboxMessages = meter.CreateHistogram<int>("whizbang.work_coordinator.batch.inbox_messages", description: "Inbox messages sent");
    BatchCompletions = meter.CreateHistogram<int>("whizbang.work_coordinator.batch.completions", description: "Completions sent");
    BatchFailures = meter.CreateHistogram<int>("whizbang.work_coordinator.batch.failures", description: "Failures sent");

    ReturnedOutboxWork = meter.CreateHistogram<int>("whizbang.work_coordinator.returned.outbox_work", description: "Outbox work items returned");
    ReturnedInboxWork = meter.CreateHistogram<int>("whizbang.work_coordinator.returned.inbox_work", description: "Inbox work items returned");
    ReturnedPerspectiveWork = meter.CreateHistogram<int>("whizbang.work_coordinator.returned.perspective_work", description: "Perspective work items returned");

    ProcessBatchCalls = meter.CreateCounter<long>("whizbang.work_coordinator.process_batch.calls", description: "Total process_work_batch calls");
    ProcessBatchErrors = meter.CreateCounter<long>("whizbang.work_coordinator.process_batch.errors", description: "SQL errors");
    FlushCalls = meter.CreateCounter<long>("whizbang.work_coordinator.flush.calls", description: "Total FlushAsync calls");
    EmptyFlushCalls = meter.CreateCounter<long>("whizbang.work_coordinator.flush.empty_calls", description: "Flushes with no queued work");

    PublisherLeaseRenewals = meter.CreateCounter<long>("whizbang.publisher.lease_renewals", description: "Lease renewals due to transport not ready");
    PublisherBufferedMessages = meter.CreateCounter<long>("whizbang.publisher.buffered_messages", description: "Total messages buffered for publish");

    MaintenanceTaskDuration = meter.CreateHistogram<double>("whizbang.maintenance.task.duration", "ms", "Duration per maintenance task");
    MaintenanceTaskRowsAffected = meter.CreateHistogram<long>("whizbang.maintenance.task.rows_affected", description: "Rows cleaned per task");
  }
}
