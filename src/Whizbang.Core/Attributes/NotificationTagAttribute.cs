using Whizbang.Core.Tags;

namespace Whizbang.Core.Attributes;

/// <summary>
/// Tags a message for real-time notification delivery (SignalR, WebSockets, etc.).
/// Discovered by MessageTagDiscoveryGenerator for AOT-compatible registration.
/// </summary>
/// <remarks>
/// <para>
/// Notifications are delivered through registered <c>IMessageTagHook&lt;NotificationTagAttribute&gt;</c>
/// implementations. The built-in SignalRNotificationHook (in Whizbang.Observability) sends notifications
/// to the specified group with the constructed payload.
/// </para>
/// <para>
/// The <see cref="Group"/> property supports {PropertyName} placeholders that are replaced
/// with values from the event at runtime.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [NotificationTag(
///     Tag = "order-shipped",
///     Properties = ["OrderId", "CustomerId", "TrackingNumber"],
///     Group = "customer-{CustomerId}",
///     Priority = NotificationPriority.High)]
/// public sealed record OrderShippedEvent(Guid OrderId, Guid CustomerId, string TrackingNumber);
/// </code>
/// </example>
/// <docs>core-concepts/message-tags#notification-tag</docs>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
public sealed class NotificationTagAttribute : MessageTagAttribute {
  /// <summary>
  /// Gets or sets the target group/channel for the notification.
  /// Supports {PropertyName} placeholders for dynamic group resolution.
  /// </summary>
  /// <remarks>
  /// Examples:
  /// <list type="bullet">
  /// <item><description>"all" - broadcasts to all connected clients</description></item>
  /// <item><description>"tenant-{TenantId}" - targets a specific tenant's clients</description></item>
  /// <item><description>"customer-{CustomerId}" - targets a specific customer</description></item>
  /// <item><description>"user-{UserId}" - targets a specific user</description></item>
  /// </list>
  /// </remarks>
  public string? Group { get; init; }

  /// <summary>
  /// Gets or sets the notification priority.
  /// Defaults to <see cref="NotificationPriority.Normal"/>.
  /// </summary>
  /// <remarks>
  /// Higher priority notifications may receive different visual treatment,
  /// bypass quiet hours, or trigger additional delivery channels.
  /// </remarks>
  public NotificationPriority Priority { get; init; } = NotificationPriority.Normal;
}
