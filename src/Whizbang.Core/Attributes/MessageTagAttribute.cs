namespace Whizbang.Core.Attributes;

/// <summary>
/// Base attribute for tagging messages with cross-cutting concern metadata.
/// Inherit from this to create domain-specific tag types (notifications, telemetry, metrics, audit, etc.).
/// </summary>
/// <remarks>
/// <para>
/// Tags are discovered at compile-time by the MessageTagDiscoveryGenerator for AOT-compatible
/// registration. Hooks registered via <c>options.Tags.UseHook&lt;TAttribute, THook&gt;()</c>
/// are invoked after successful message handling.
/// </para>
/// <para>
/// The payload sent to hooks is built from:
/// <list type="bullet">
/// <item><description>Extracted properties from the <see cref="Properties"/> array as JSON key/value pairs</description></item>
/// <item><description>Full event under "__event" key when <see cref="IncludeEvent"/> is true</description></item>
/// <item><description>Merged content from <see cref="ExtraJson"/> if specified</description></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Create a custom tag type
/// public sealed class AuditTagAttribute : MessageTagAttribute {
///   public string? Reason { get; init; }
/// }
///
/// // Apply to events
/// [AuditTag(Tag = "order-created", Properties = ["OrderId", "CustomerId"], Reason = "Compliance")]
/// public sealed record OrderCreatedEvent(Guid OrderId, Guid CustomerId);
/// </code>
/// </example>
/// <docs>core-concepts/message-tags</docs>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
public abstract class MessageTagAttribute : Attribute {
  /// <summary>
  /// Gets or sets the tag value for this message.
  /// Used to identify the type of cross-cutting concern (e.g., "order-created", "payment-processed").
  /// </summary>
  public required string Tag { get; init; }

  /// <summary>
  /// Gets or sets the property names to extract from the message and include in the tag payload.
  /// Values are stored as JSON key/value pairs: { "PropertyName": value, ... }
  /// </summary>
  /// <remarks>
  /// Property extraction is performed at runtime using source-generated delegates for AOT compatibility.
  /// Only properties that exist on the message type will be extracted; missing properties are silently ignored.
  /// </remarks>
  /// <example>
  /// <code>
  /// [NotificationTag(Tag = "order-shipped", Properties = ["OrderId", "CustomerId", "TrackingNumber"])]
  /// public sealed record OrderShippedEvent(Guid OrderId, Guid CustomerId, string TrackingNumber, DateTime ShippedAt);
  /// // Extracted payload: { "OrderId": "...", "CustomerId": "...", "TrackingNumber": "..." }
  /// </code>
  /// </example>
  public string[]? Properties { get; init; }

  /// <summary>
  /// Gets or sets whether to include the entire event object in the payload under the "__event" key.
  /// This avoids conflicts with other extracted properties.
  /// </summary>
  /// <remarks>
  /// When true, the full serialized event is included, allowing hooks to access any property.
  /// Useful when the set of needed properties varies or is determined at runtime.
  /// </remarks>
  /// <example>
  /// <code>
  /// [TelemetryTag(Tag = "order-completed", IncludeEvent = true)]
  /// public sealed record OrderCompletedEvent(...);
  /// // Payload includes: { "__event": { full event JSON } }
  /// </code>
  /// </example>
  public bool IncludeEvent { get; init; }

  /// <summary>
  /// Gets or sets arbitrary JSON to merge into the payload.
  /// Can reference event properties with {PropertyName} syntax for template expansion.
  /// Merged with Properties and __event (if IncludeEvent is true).
  /// </summary>
  /// <remarks>
  /// The JSON is parsed and merged at runtime. Invalid JSON will cause a runtime exception.
  /// Template placeholders like {PropertyName} are replaced with values from the event.
  /// </remarks>
  /// <example>
  /// <code>
  /// [MetricTag(
  ///     Tag = "order-created",
  ///     MetricName = "orders.created",
  ///     Properties = ["OrderId"],
  ///     ExtraJson = """{"source": "api", "version": 2}""")]
  /// public sealed record OrderCreatedEvent(Guid OrderId);
  /// // Payload: { "OrderId": "...", "source": "api", "version": 2 }
  /// </code>
  /// </example>
  public string? ExtraJson { get; init; }
}
