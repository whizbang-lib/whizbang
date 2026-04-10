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
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Perspectives";
#pragma warning restore CA1707


  // Timing

  /// <summary>Full _processWorkBatchAsync cycle duration.</summary>
  public Histogram<double> BatchDuration { get; }

  /// <summary>ProcessWorkBatchAsync duration to claim perspective work.</summary>
  public Histogram<double> ClaimDuration { get; }

  /// <summary>GetEventsBetweenPolymorphicAsync query duration.</summary>
  public Histogram<double> EventLoadDuration { get; }

  /// <summary>IPerspectiveRunner execution duration per stream.</summary>
  public Histogram<double> RunnerDuration { get; }

  /// <summary>GetPerspectiveCursorAsync duration.</summary>
  public Histogram<double> CheckpointDuration { get; }

  // Throughput

  /// <summary>Events applied to perspectives.</summary>
  public Counter<long> EventsProcessed { get; }

  /// <summary>Batches completed.</summary>
  public Counter<long> BatchesProcessed { get; }

  /// <summary>Unique streams updated.</summary>
  public Counter<long> StreamsUpdated { get; }

  /// <summary>Processing errors.</summary>
  public Counter<long> Errors { get; }

  /// <summary>Polling cycles with no work.</summary>
  public Counter<long> EmptyBatches { get; }

  // Batch composition

  /// <summary>Work items claimed per batch.</summary>
  public Histogram<int> BatchWorkItems { get; }

  /// <summary>Events loaded per batch.</summary>
  public Histogram<int> BatchEventCount { get; }

  /// <summary>Distinct streams per batch.</summary>
  public Histogram<int> BatchStreamGroups { get; }

  // Rewind

  /// <summary>Rewind operations triggered. Tags: perspective_name, has_snapshot.</summary>
  /// <docs>fundamentals/perspectives/rewind#metrics</docs>
  public Counter<long> Rewinds { get; }

  /// <summary>Rewind replay duration in milliseconds. Tags: perspective_name.</summary>
  /// <docs>fundamentals/perspectives/rewind#metrics</docs>
  public Histogram<double> RewindDuration { get; }

  /// <summary>Events replayed per rewind. Tags: perspective_name.</summary>
  /// <docs>fundamentals/perspectives/rewind#metrics</docs>
  public Histogram<int> RewindEventsReplayed { get; }

  /// <summary>Perspective events behind cursor when rewind triggered. Tags: perspective_name.</summary>
  /// <docs>fundamentals/perspectives/rewind#metrics</docs>
  public Histogram<int> RewindEventsBehind { get; }

  /// <summary>Initializes a new instance of the <see cref="PerspectiveMetrics"/> class.</summary>
  /// <param name="whizbangMetrics">The shared metrics factory providing the meter.</param>
  public PerspectiveMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    BatchDuration = meter.CreateHistogram<double>("whizbang.perspective.batch.duration", "ms", "Full _processWorkBatchAsync cycle");
    ClaimDuration = meter.CreateHistogram<double>("whizbang.perspective.claim.duration", "ms", "ProcessWorkBatchAsync to claim perspective work");
    EventLoadDuration = meter.CreateHistogram<double>("whizbang.perspective.event_load.duration", "ms", "GetEventsBetweenPolymorphicAsync query");
    RunnerDuration = meter.CreateHistogram<double>("whizbang.perspective.runner.duration", "ms", "IPerspectiveRunner execution per stream");
    CheckpointDuration = meter.CreateHistogram<double>("whizbang.perspective.checkpoint.duration", "ms", "GetPerspectiveCursorAsync");

    EventsProcessed = meter.CreateCounter<long>("whizbang.perspective.events_processed", description: "Events applied to perspectives");
    BatchesProcessed = meter.CreateCounter<long>("whizbang.perspective.batches_processed", description: "Batches completed");
    StreamsUpdated = meter.CreateCounter<long>("whizbang.perspective.streams_updated", description: "Unique streams updated");
    Errors = meter.CreateCounter<long>("whizbang.perspective.errors", description: "Processing errors");
    EmptyBatches = meter.CreateCounter<long>("whizbang.perspective.empty_batches", description: "Polling cycles with no work");

    BatchWorkItems = meter.CreateHistogram<int>("whizbang.perspective.batch.work_items", description: "Work items claimed per batch");
    BatchEventCount = meter.CreateHistogram<int>("whizbang.perspective.batch.event_count", description: "Events loaded per batch");
    BatchStreamGroups = meter.CreateHistogram<int>("whizbang.perspective.batch.stream_groups", description: "Distinct streams per batch");

    Rewinds = meter.CreateCounter<long>("whizbang.perspective.rewinds", description: "Rewind operations triggered");
    RewindDuration = meter.CreateHistogram<double>("whizbang.perspective.rewind.duration", "ms", "Rewind replay duration");
    RewindEventsReplayed = meter.CreateHistogram<int>("whizbang.perspective.rewind.events_replayed", description: "Events replayed per rewind");
    RewindEventsBehind = meter.CreateHistogram<int>("whizbang.perspective.rewind.events_behind", description: "Events behind cursor when rewind triggered");
  }
}
