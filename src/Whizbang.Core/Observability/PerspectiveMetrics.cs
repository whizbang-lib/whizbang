// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  /// <summary>Full <c>PerspectiveWorker.ProcessChannelBatchAsync</c> cycle duration.</summary>
  public Histogram<double> BatchDuration { get; }

  /// <summary>
  /// Time of one <c>ClaimWorker</c> claim round trip, in milliseconds. A claim acquires outbox, inbox
  /// and perspective work in one call, so this is the time every worker waits to be handed work.
  /// </summary>
  public Histogram<double> ClaimDuration { get; }

  /// <summary>GetEventsBetweenPolymorphicAsync query duration.</summary>
  public Histogram<double> EventLoadDuration { get; }

  /// <summary>IPerspectiveRunner execution duration per stream.</summary>
  public Histogram<double> RunnerDuration { get; }

  /// <summary>GetPerspectiveCursorAsync duration.</summary>
  public Histogram<double> CheckpointDuration { get; }

  // Throughput

  /// <summary>Events applied to perspectives.</summary>
  public PassiveCounter<long> EventsProcessed { get; }

  /// <summary>Batches completed.</summary>
  public PassiveCounter<long> BatchesProcessed { get; }

  /// <summary>Unique streams updated.</summary>
  public PassiveCounter<long> StreamsUpdated { get; }

  /// <summary>Processing errors.</summary>
  public PassiveCounter<long> Errors { get; }

  /// <summary>Polling cycles with no work.</summary>
  public PassiveCounter<long> EmptyBatches { get; }

  /// <summary>
  /// Rows a perspective could not read. Tags: perspective_name, reason
  /// (<see cref="Perspectives.StoredFormUnreadable.REASON"/> for a stored form no reader takes).
  /// </summary>
  /// <docs>operations/infrastructure/migrations</docs>
  public PassiveCounter<long> ReadFailures { get; }

  /// <summary>
  /// Events skipped because their stream was purged and the Apply did not resurrect it. Tags: perspective_name.
  /// </summary>
  /// <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PurgeStaysPurgedTests.cs:LaterBatch_AfterPurge_IsSkipped_AndNoRowIsRecreatedAsync</tests>
  public PassiveCounter<long> PurgedEventsSkipped { get; }

  // Batch composition

  /// <summary>Work items claimed per batch.</summary>
  public Histogram<int> BatchWorkItems { get; }

  /// <summary>Events loaded per batch.</summary>
  public Histogram<int> BatchEventCount { get; }

  /// <summary>Distinct streams per batch.</summary>
  public Histogram<int> BatchStreamGroups { get; }

  // Queue depth (reported periodically, not every tick)

  /// <summary>Pending perspective events awaiting processing (observable gauge, updated periodically).</summary>
  private long _pendingEventsValue;

  /// <summary>Updates the cached pending events count for the observable gauge.</summary>
  public void SetPendingEvents(long count) => Interlocked.Exchange(ref _pendingEventsValue, count);

  /// <summary>
  /// Refreshes of <c>whizbang.perspective.pending_events</c> whose statistics query failed (#1865).
  /// </summary>
  /// <remarks>
  /// The gauge keeps the last value it read while its query fails, so a frozen number reads as real lag or real
  /// health. This rising while the gauge holds still says the gauge is stale; the worker's warning names the cause.
  /// </remarks>
  /// <docs>operations/observability/metrics#perspectives</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PendingEventsStatisticsRefreshTests.cs</tests>
  public PassiveCounter<long> PendingEventsRefreshFailures { get; }

  // Rewind

  /// <summary>
  /// Rewind operations that COMPLETED. Tags: perspective_name, has_snapshot. Reads with
  /// <see cref="RewindsTriggered"/> and <see cref="RewindsFailed"/> as one identity:
  /// triggered = completed + failed + in flight.
  /// </summary>
  /// <remarks>
  /// The instrument keeps the name <c>whizbang.perspective.rewinds</c> although it counts completions
  /// rather than triggers, because renaming a published meter makes every panel built on it read zero,
  /// which is the same thing that misled an operator in the first place (#1362). The description is what
  /// changed.
  /// </remarks>
  /// <docs>fundamentals/perspectives/rewind#metrics</docs>
  public PassiveCounter<long> Rewinds { get; }

  /// <summary>Rewinds started, counted before the replay runs. Tags: perspective_name.</summary>
  /// <remarks>
  /// Separate from <see cref="Rewinds"/> so a rewind that never finishes is still visible. The two
  /// reading apart is the signal: completions lagging triggers means rewinds are failing or queued,
  /// and until a completed rewind retires its trigger this is inflated by re-detection (#1361).
  /// </remarks>
  /// <docs>fundamentals/perspectives/rewind#metrics</docs>
  public PassiveCounter<long> RewindsTriggered { get; }

  /// <summary>Rewinds that threw. Tags: perspective_name.</summary>
  /// <remarks>
  /// Distinct from the generic <see cref="Errors"/> counter, which mixes every processing failure
  /// together and so cannot answer whether rewinds specifically are failing.
  /// </remarks>
  /// <docs>fundamentals/perspectives/rewind#metrics</docs>
  public PassiveCounter<long> RewindsFailed { get; }

  /// <summary>
  /// Late events an ephemeral perspective dropped instead of rewinding for them, because no snapshot floor preceded them
  /// and the bodies a replay would read were reaped. Tags: perspective_name.
  /// </summary>
  /// <remarks>
  /// Each straggler is counted once: its work is retired when it is dropped, so no later claim finds it again (#1862).
  /// A rising count says late events are arriving outside the perspective's reorder window, which a snapshot cadence
  /// or grace period wide enough to cover them would turn back into rewinds.
  /// </remarks>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  public PassiveCounter<long> StragglersDropped { get; }

  /// <summary>Rewinds running right now (observable gauge).</summary>
  private long _rewindsInFlightValue;

  /// <summary>Records that a rewind has started and is now in flight.</summary>
  public void RewindStarted() => Interlocked.Increment(ref _rewindsInFlightValue);

  /// <summary>Records that a rewind has left flight, whether it completed or threw.</summary>
  public void RewindFinished() => Interlocked.Decrement(ref _rewindsInFlightValue);

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
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);

    BatchDuration = meter.CreateHistogram<double>("whizbang.perspective.batch.duration", "ms", "Full PerspectiveWorker.ProcessChannelBatchAsync cycle");
    ClaimDuration = meter.CreateHistogram<double>("whizbang.perspective.claim.duration", "ms", "Time of one claim round trip, which acquires outbox, inbox and perspective work");
    EventLoadDuration = meter.CreateHistogram<double>("whizbang.perspective.event_load.duration", "ms", "GetEventsBetweenPolymorphicAsync query");
    RunnerDuration = meter.CreateHistogram<double>("whizbang.perspective.runner.duration", "ms", "IPerspectiveRunner execution per stream");
    CheckpointDuration = meter.CreateHistogram<double>("whizbang.perspective.checkpoint.duration", "ms", "GetPerspectiveCursorAsync");

    EventsProcessed = meter.CreatePassiveCounter<long>("whizbang.perspective.events_processed", description: "Events applied to perspectives");
    BatchesProcessed = meter.CreatePassiveCounter<long>("whizbang.perspective.batches_processed", description: "Batches completed");
    StreamsUpdated = meter.CreatePassiveCounter<long>("whizbang.perspective.streams_updated", description: "Unique streams updated");
    Errors = meter.CreatePassiveCounter<long>("whizbang.perspective.errors", description: "Processing errors");
    EmptyBatches = meter.CreatePassiveCounter<long>("whizbang.perspective.empty_batches", description: "Polling cycles with no work");
    ReadFailures = meter.CreatePassiveCounter<long>("whizbang.perspective.read_failures", description: "Rows a perspective could not read, by perspective and reason");
    PurgedEventsSkipped = meter.CreatePassiveCounter<long>("whizbang.perspective.purged_events_skipped", description: "Events skipped because their stream was purged, by perspective");

    BatchWorkItems = meter.CreateHistogram<int>("whizbang.perspective.batch.work_items", description: "Work items claimed per batch");
    BatchEventCount = meter.CreateHistogram<int>("whizbang.perspective.batch.event_count", description: "Events loaded per batch");
    BatchStreamGroups = meter.CreateHistogram<int>("whizbang.perspective.batch.stream_groups", description: "Distinct streams per batch");

    meter.CreateObservableGauge("whizbang.perspective.pending_events",
      observeValue: () => Interlocked.Read(ref _pendingEventsValue),
      description: "Pending perspective events awaiting processing");
    PendingEventsRefreshFailures = meter.CreatePassiveCounter<long>("whizbang.perspective.pending_events_refresh_failures",
      description: "Refreshes of the pending events gauge whose statistics query failed, leaving the gauge at its last value");

    Rewinds = meter.CreatePassiveCounter<long>("whizbang.perspective.rewinds", description: "Rewind operations that completed");
    RewindsTriggered = meter.CreatePassiveCounter<long>("whizbang.perspective.rewinds_triggered", description: "Rewinds started");
    RewindsFailed = meter.CreatePassiveCounter<long>("whizbang.perspective.rewinds_failed", description: "Rewinds that threw");
    StragglersDropped = meter.CreatePassiveCounter<long>("whizbang.perspective.stragglers_dropped",
      description: "Late events an ephemeral perspective dropped instead of rewinding for them");

    // Pinned at the concurrency limit means rewinds are queueing behind it, which no counter shows.
    meter.CreateObservableGauge("whizbang.perspective.rewinds_in_flight",
      observeValue: () => Interlocked.Read(ref _rewindsInFlightValue),
      description: "Rewinds running right now");
    RewindDuration = meter.CreateHistogram<double>("whizbang.perspective.rewind.duration", "ms", "Rewind replay duration");
    RewindEventsReplayed = meter.CreateHistogram<int>("whizbang.perspective.rewind.events_replayed", description: "Events replayed per rewind");
    RewindEventsBehind = meter.CreateHistogram<int>("whizbang.perspective.rewind.events_behind", description: "Events behind cursor when rewind triggered");
  }
}
