namespace Whizbang.Core.Dispatch;

/// <summary>
/// Specifies the routing destination(s) for cascaded messages returned from receptors.
/// </summary>
/// <remarks>
/// <para>
/// When a receptor returns messages (events or commands), the dispatcher needs to know
/// where to send them. DispatchMode is a flags enum that allows specifying one or both:
/// </para>
/// <list type="bullet">
///   <item><b>Local</b>: Dispatch to in-process receptors only</item>
///   <item><b>Outbox</b>: Write to outbox for transport to other services</item>
///   <item><b>Both</b>: Do both local dispatch AND outbox write</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Return event to local receptors only
/// return Route.Local(new CacheInvalidatedEvent { Key = "users" });
///
/// // Return event to outbox for transport
/// return Route.Outbox(new UserCreatedEvent { UserId = userId });
///
/// // Return event to both local AND outbox
/// return Route.Both(new OrderCompletedEvent { OrderId = orderId });
/// </code>
/// </example>
/// <docs>core-concepts/dispatcher#routed-message-cascading</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchModeTests.cs</tests>
[Flags]
public enum DispatchMode {
  /// <summary>
  /// No routing - message is not dispatched.
  /// </summary>
  None = 0,

  /// <summary>
  /// Dispatch to in-process receptors only.
  /// Message stays within the current service boundary.
  /// </summary>
  Local = 1,

  /// <summary>
  /// Write to outbox for transport delivery to other services.
  /// Message will be sent via the configured transport (Kafka, RabbitMQ, etc).
  /// </summary>
  Outbox = 2,

  /// <summary>
  /// Both local dispatch AND outbox write.
  /// Message is handled by local receptors AND sent to other services.
  /// </summary>
  Both = Local | Outbox
}
