using System.Diagnostics.Metrics;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Observability;

/// <summary>
/// Observability for background-activity arbitration and the pod's idle state.
/// </summary>
/// <remarks>
/// <para>
/// The arbitration decides which of the store-contending activities — dead-letter recovery,
/// integrity, the cleanup sweep — holds the slot, and the idle tracker decides whether the pod is
/// active at all. Before this meter, none of that state was observable: an operator asking "is
/// recovery actually running, or deferred, and why?" had only log lines to grep. Now it is three
/// instruments: every verdict, what currently holds the slot, and how long the pod has been idle.
/// </para>
/// <para>
/// Facet questions this answers directly: <c>housekeeping.running</c> grouped by <c>activity</c>
/// shows what is running right now; <c>housekeeping.decisions</c> filtered to
/// <c>verdict=ServiceBusy</c> shows recovery correctly waiting for idle;
/// <c>idle.seconds_since_activity</c> against the backup-tick threshold shows whether the pod is
/// in its ASLEEP or POLLING regime, with the last activity's source as a tag.
/// </para>
/// </remarks>
/// <docs>operations/workers/housekeeping-arbitration</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/HousekeepingMetricsTests.cs</tests>
public sealed class HousekeepingMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Housekeeping";
#pragma warning restore CA1707

  /// <summary>Every arbitration verdict, tagged by activity and verdict.</summary>
  public Counter<long> Decisions { get; }

  /// <summary>Activities currently holding the slot, tagged by activity. 0 or 1 per activity.</summary>
  public UpDownCounter<long> Running { get; }

  /// <summary>
  /// Items each activity processed while holding the slot, tagged by activity — the volume rollup.
  /// </summary>
  /// <remarks>
  /// One chart answering "what did housekeeping actually do": dead letters re-driven, maintenance
  /// rows swept. Integrity keeps its dedicated meter (<c>Whizbang.StreamIntegrity</c>) for the
  /// per-reason detail; this counter is the cross-activity overview.
  /// </remarks>
  public Counter<long> Items { get; }

  /// <summary>Initializes the meter, optionally exposing the idle tracker as a gauge.</summary>
  /// <param name="whizbangMetrics">The shared meter factory holder.</param>
  /// <param name="idleTracker">
  /// When present, publishes <c>whizbang.idle.seconds_since_activity</c> with the last activity's
  /// source as a tag, so active-versus-idle is a dashboard fact rather than a log grep.
  /// </param>
  public HousekeepingMetrics(WhizbangMetrics whizbangMetrics, IIdleActivityTracker? idleTracker = null) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    Decisions = meter.CreateCounter<long>(
      "whizbang.housekeeping.decisions",
      description: "Arbitration verdicts, tagged by activity and verdict.");

    Running = meter.CreateUpDownCounter<long>(
      "whizbang.housekeeping.running",
      description: "Activities currently holding the housekeeping slot, tagged by activity.");

    Items = meter.CreateCounter<long>(
      "whizbang.housekeeping.items",
      description: "Items processed per housekeeping activity — the volume rollup.");

    if (idleTracker is not null) {
      meter.CreateObservableGauge(
        "whizbang.idle.seconds_since_activity",
        () => new Measurement<double>(
          idleTracker.TimeSinceLastActivity.TotalSeconds,
          new KeyValuePair<string, object?>("last_source", idleTracker.LastActivitySource)),
        unit: "s",
        description: "How long this pod has been idle, tagged with the last activity's source.");
    }
  }

  /// <summary>Records one arbitration decision, and slot occupancy when granted.</summary>
  /// <param name="activity">The activity that asked.</param>
  /// <param name="verdict">The arbiter's answer.</param>
  /// <param name="granted">Whether the slot was granted.</param>
  public void RecordDecision(HousekeepingCoordinator.Activity activity, HousekeepingCoordinator.Verdict verdict, bool granted) {
    var tags = new System.Diagnostics.TagList {
      { "activity", activity.ToString() },
      { "verdict", verdict.ToString() },
    };
    Decisions.Add(1, tags);
    if (granted) {
      Running.Add(1, new KeyValuePair<string, object?>("activity", activity.ToString()));
    }
  }

  /// <summary>Records the volume an activity processed while holding the slot.</summary>
  /// <param name="activity">The activity.</param>
  /// <param name="count">Items processed this cycle; zero-count cycles are not recorded.</param>
  public void RecordItems(HousekeepingCoordinator.Activity activity, long count) {
    if (count > 0) {
      Items.Add(count, new KeyValuePair<string, object?>("activity", activity.ToString()));
    }
  }

  /// <summary>Records an activity releasing the slot.</summary>
  /// <param name="activity">The activity that finished.</param>
  public void RecordEnd(HousekeepingCoordinator.Activity activity) {
    Running.Add(-1, new KeyValuePair<string, object?>("activity", activity.ToString()));
  }
}
