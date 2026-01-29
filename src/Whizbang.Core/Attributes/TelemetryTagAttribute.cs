using Whizbang.Core.Tags;

namespace Whizbang.Core.Attributes;

/// <summary>
/// Tags a message for OpenTelemetry distributed tracing.
/// Discovered by MessageTagDiscoveryGenerator for AOT-compatible registration.
/// </summary>
/// <remarks>
/// <para>
/// Telemetry tags are processed by registered <c>IMessageTagHook&lt;TelemetryTagAttribute&gt;</c>
/// implementations. The built-in OpenTelemetrySpanHook (in Whizbang.Observability) creates
/// or enriches spans based on the tag configuration.
/// </para>
/// <para>
/// When <see cref="RecordAsEvent"/> is true (default), the message is recorded as an event
/// on the current span with properties from <see cref="MessageTagAttribute.Properties"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [TelemetryTag(
///     Tag = "payment-processed",
///     Properties = ["PaymentId", "Amount", "Currency"],
///     SpanName = "ProcessPayment",
///     Kind = SpanKind.Internal)]
/// public sealed record PaymentProcessedEvent(Guid PaymentId, decimal Amount, string Currency);
/// </code>
/// </example>
/// <docs>core-concepts/message-tags#telemetry-tag</docs>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
public sealed class TelemetryTagAttribute : MessageTagAttribute {
  /// <summary>
  /// Gets or sets the span name for distributed tracing.
  /// If not specified, defaults to the <see cref="MessageTagAttribute.Tag"/> value.
  /// </summary>
  /// <remarks>
  /// The span name appears in tracing visualizations and should be descriptive
  /// but not contain high-cardinality values (use span attributes for those).
  /// </remarks>
  public string? SpanName { get; init; }

  /// <summary>
  /// Gets or sets the span kind for distributed tracing.
  /// Defaults to <see cref="SpanKind.Internal"/>.
  /// </summary>
  /// <remarks>
  /// SpanKind affects how the span is rendered in trace visualizations:
  /// <list type="bullet">
  /// <item><description><see cref="SpanKind.Internal"/> - Default for local operations</description></item>
  /// <item><description><see cref="SpanKind.Server"/> - Processing incoming requests</description></item>
  /// <item><description><see cref="SpanKind.Client"/> - Making outgoing requests</description></item>
  /// <item><description><see cref="SpanKind.Producer"/> - Publishing messages</description></item>
  /// <item><description><see cref="SpanKind.Consumer"/> - Consuming messages</description></item>
  /// </list>
  /// </remarks>
  public SpanKind Kind { get; init; } = SpanKind.Internal;

  /// <summary>
  /// Gets or sets whether to record this message as an event on the current span.
  /// Defaults to true.
  /// </summary>
  /// <remarks>
  /// When true, the message is recorded as a span event with extracted properties.
  /// When false, only span attributes are added without creating an event.
  /// </remarks>
  public bool RecordAsEvent { get; init; } = true;
}
