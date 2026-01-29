namespace Whizbang.Core.Tags;

/// <summary>
/// Defines the type of OpenTelemetry metric to record.
/// </summary>
/// <remarks>
/// Each metric type has different semantics:
/// <list type="bullet">
/// <item><description><see cref="Counter"/> - Monotonically increasing values (e.g., request count)</description></item>
/// <item><description><see cref="Histogram"/> - Distribution of values (e.g., request duration)</description></item>
/// <item><description><see cref="Gauge"/> - Point-in-time values (e.g., current memory usage)</description></item>
/// </list>
/// </remarks>
/// <docs>core-concepts/message-tags#metric-type</docs>
public enum MetricType {
  /// <summary>
  /// Counter metric for monotonically increasing values.
  /// Used for counting occurrences (e.g., total requests, orders created).
  /// Default value is 1 unless <see cref="MetricTagAttribute.ValueProperty"/> is specified.
  /// </summary>
  Counter = 0,

  /// <summary>
  /// Histogram metric for recording distributions of values.
  /// Used for measuring durations, sizes, or amounts (e.g., request latency, order amount).
  /// Requires <see cref="MetricTagAttribute.ValueProperty"/> to specify the value source.
  /// </summary>
  Histogram = 1,

  /// <summary>
  /// Gauge metric for recording point-in-time values.
  /// Used for current state measurements (e.g., active connections, queue depth).
  /// Requires <see cref="MetricTagAttribute.ValueProperty"/> to specify the value source.
  /// </summary>
  Gauge = 2
}
