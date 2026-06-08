using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for the inbox dispatch path: per-message wall time tagged with
/// message type. production import (Jun 2026) surfaced 2,704
/// <c>OrderCompetencyRowAddedEvent</c> rows draining at ~27 rows/sec;
/// without a per-message-type histogram an operator can't tell whether the
/// bottleneck is one slow event type or the pipeline overall.
/// </summary>
/// <remarks>
/// Meter name reuses <see cref="WorkCoordinatorMetrics.METER_NAME"/> —
/// inbox dispatch belongs to the work-coordinator pipeline observability
/// surface, not a separate one.
/// </remarks>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/InboxMetricsTests.cs</tests>
public sealed class InboxMetrics {
  /// <summary>
  /// Per-message dispatch wall time (milliseconds). Tagged with
  /// <c>message_type</c> (short class name only — see <see cref="RecordDispatch"/>).
  /// </summary>
  public Histogram<double> DispatchDuration { get; }

  /// <summary>Constructor.</summary>
  /// <param name="whizbangMetrics">The shared metrics factory providing the meter.</param>
  public InboxMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory?.Create(WorkCoordinatorMetrics.METER_NAME)
              ?? new Meter(WorkCoordinatorMetrics.METER_NAME);
    DispatchDuration = meter.CreateHistogram<double>(
      "whizbang.inbox.dispatch.duration_ms",
      "ms",
      "Per-message inbox dispatch wall time, tagged with short message type");
  }

  /// <summary>
  /// Records a single inbox-dispatch observation. The message type tag is
  /// shortened to the last segment (after the final <c>'.'</c> or
  /// <c>'+'</c>) and the assembly-qualified name suffix (after the first
  /// comma) is stripped — operators care about the event class, not its
  /// namespace + assembly identity. Empty/null message types are tagged
  /// <c>&lt;unknown&gt;</c> so mis-instrumented call sites are visible.
  /// </summary>
  public void RecordDispatch(string messageType, double durationMs) {
    var shortName = _shortenMessageType(messageType);
    DispatchDuration.Record(durationMs, new KeyValuePair<string, object?>("message_type", shortName));
  }

  internal static string _shortenMessageType(string? messageType) {
    if (string.IsNullOrEmpty(messageType)) {
      return "<unknown>";
    }
    // Strip AQN suffix (everything after the first comma).
    var commaIdx = messageType.IndexOf(',');
    var typeSpan = commaIdx >= 0 ? messageType.AsSpan(0, commaIdx).TrimEnd() : messageType.AsSpan();
    // Take the segment after the final '.' or '+' (whichever appears last).
    var dotIdx = typeSpan.LastIndexOf('.');
    var plusIdx = typeSpan.LastIndexOf('+');
    var separatorIdx = Math.Max(dotIdx, plusIdx);
    var lastSegment = separatorIdx >= 0
      ? typeSpan[(separatorIdx + 1)..]
      : typeSpan;
    return lastSegment.IsEmpty ? "<unknown>" : lastSegment.ToString();
  }
}
