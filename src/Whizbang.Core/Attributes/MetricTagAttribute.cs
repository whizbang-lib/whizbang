using Whizbang.Core.Tags;

namespace Whizbang.Core.Attributes;

/// <summary>
/// Tags a message for OpenTelemetry metrics recording (counters, histograms, gauges).
/// Discovered by MessageTagDiscoveryGenerator for AOT-compatible registration.
/// </summary>
/// <remarks>
/// <para>
/// Metric tags are processed by registered <c>IMessageTagHook&lt;MetricTagAttribute&gt;</c>
/// implementations. The built-in OpenTelemetryMetricHook (in Whizbang.Observability) records
/// metrics based on the tag configuration.
/// </para>
/// <para>
/// The <see cref="MessageTagAttribute.Properties"/> become metric dimensions/labels,
/// allowing metrics to be segmented by tenant, region, or other attributes.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Counter metric - increments by 1 for each event
/// [MetricTag(
///     Tag = "order-created",
///     MetricName = "orders.created",
///     Type = MetricType.Counter,
///     Properties = ["TenantId", "Region"])]
/// public sealed record OrderCreatedEvent(Guid OrderId, string TenantId, string Region);
///
/// // Histogram metric - records the TotalAmount value
/// [MetricTag(
///     Tag = "order-amount",
///     MetricName = "orders.amount",
///     Type = MetricType.Histogram,
///     ValueProperty = nameof(TotalAmount),
///     Unit = "USD",
///     Properties = ["TenantId"])]
/// public sealed record OrderCompletedEvent(Guid OrderId, decimal TotalAmount, string TenantId);
/// </code>
/// </example>
/// <docs>core-concepts/message-tags#metric-tag</docs>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
public sealed class MetricTagAttribute : MessageTagAttribute {
  /// <summary>
  /// Gets or sets the metric name for recording.
  /// Should follow OpenTelemetry naming conventions (e.g., "orders.created", "payments.amount").
  /// </summary>
  /// <remarks>
  /// Good metric names are:
  /// <list type="bullet">
  /// <item><description>Lowercase with dots as separators</description></item>
  /// <item><description>Descriptive but concise</description></item>
  /// <item><description>Include the unit where appropriate (e.g., "request.duration.ms")</description></item>
  /// </list>
  /// </remarks>
  public required string MetricName { get; init; }

  /// <summary>
  /// Gets or sets the metric type.
  /// Defaults to <see cref="MetricType.Counter"/>.
  /// </summary>
  /// <remarks>
  /// Choose the appropriate type based on what you're measuring:
  /// <list type="bullet">
  /// <item><description><see cref="MetricType.Counter"/> - For counting occurrences</description></item>
  /// <item><description><see cref="MetricType.Histogram"/> - For measuring distributions</description></item>
  /// <item><description><see cref="MetricType.Gauge"/> - For point-in-time values</description></item>
  /// </list>
  /// </remarks>
  public MetricType Type { get; init; } = MetricType.Counter;

  /// <summary>
  /// Gets or sets the property name to use as the metric value.
  /// Required for <see cref="MetricType.Histogram"/> and <see cref="MetricType.Gauge"/>.
  /// For <see cref="MetricType.Counter"/>, defaults to 1 if not specified.
  /// </summary>
  /// <remarks>
  /// The property must be a numeric type (int, long, float, double, decimal).
  /// </remarks>
  public string? ValueProperty { get; init; }

  /// <summary>
  /// Gets or sets the unit of measurement for the metric.
  /// Follows OpenTelemetry unit conventions (e.g., "ms", "bytes", "requests", "USD").
  /// </summary>
  /// <remarks>
  /// Common units include:
  /// <list type="bullet">
  /// <item><description>"ms" - milliseconds</description></item>
  /// <item><description>"s" - seconds</description></item>
  /// <item><description>"bytes" - byte count</description></item>
  /// <item><description>"requests" - request count</description></item>
  /// <item><description>Currency codes like "USD", "EUR"</description></item>
  /// </list>
  /// </remarks>
  public string? Unit { get; init; }
}
