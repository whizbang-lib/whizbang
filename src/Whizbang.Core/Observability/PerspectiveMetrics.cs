using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for perspective processing: worker, checkpoint, event loading.
/// Meter name: Whizbang.Perspectives
/// </summary>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/PerspectiveMetricsTests.cs</tests>
public sealed class PerspectiveMetrics {
#pragma warning disable CA1707
  public const string METER_NAME = "Whizbang.Perspectives";
#pragma warning restore CA1707

  private readonly Meter _meter;

  // Timing
  public Histogram<double> BatchDuration { get; }
  public Histogram<double> ClaimDuration { get; }
  public Histogram<double> EventLoadDuration { get; }
  public Histogram<double> RunnerDuration { get; }
  public Histogram<double> CheckpointDuration { get; }

  // Throughput
  public Counter<long> EventsProcessed { get; }
  public Counter<long> BatchesProcessed { get; }
  public Counter<long> StreamsUpdated { get; }
  public Counter<long> Errors { get; }
  public Counter<long> EmptyBatches { get; }

  // Batch composition
  public Histogram<int> BatchWorkItems { get; }
  public Histogram<int> BatchEventCount { get; }
  public Histogram<int> BatchStreamGroups { get; }

  public PerspectiveMetrics(WhizbangMetrics whizbangMetrics) {
    _meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    BatchDuration = _meter.CreateHistogram<double>("whizbang.perspective.batch.duration", "ms", "Full _processWorkBatchAsync cycle");
    ClaimDuration = _meter.CreateHistogram<double>("whizbang.perspective.claim.duration", "ms", "ProcessWorkBatchAsync to claim perspective work");
    EventLoadDuration = _meter.CreateHistogram<double>("whizbang.perspective.event_load.duration", "ms", "GetEventsBetweenPolymorphicAsync query");
    RunnerDuration = _meter.CreateHistogram<double>("whizbang.perspective.runner.duration", "ms", "IPerspectiveRunner execution per stream");
    CheckpointDuration = _meter.CreateHistogram<double>("whizbang.perspective.checkpoint.duration", "ms", "GetPerspectiveCursorAsync");

    EventsProcessed = _meter.CreateCounter<long>("whizbang.perspective.events_processed", description: "Events applied to perspectives");
    BatchesProcessed = _meter.CreateCounter<long>("whizbang.perspective.batches_processed", description: "Batches completed");
    StreamsUpdated = _meter.CreateCounter<long>("whizbang.perspective.streams_updated", description: "Unique streams updated");
    Errors = _meter.CreateCounter<long>("whizbang.perspective.errors", description: "Processing errors");
    EmptyBatches = _meter.CreateCounter<long>("whizbang.perspective.empty_batches", description: "Polling cycles with no work");

    BatchWorkItems = _meter.CreateHistogram<int>("whizbang.perspective.batch.work_items", description: "Work items claimed per batch");
    BatchEventCount = _meter.CreateHistogram<int>("whizbang.perspective.batch.event_count", description: "Events loaded per batch");
    BatchStreamGroups = _meter.CreateHistogram<int>("whizbang.perspective.batch.stream_groups", description: "Distinct streams per batch");
  }
}
