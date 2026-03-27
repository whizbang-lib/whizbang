namespace Whizbang.Core.Dispatch;

/// <summary>
/// Specifies the routing destination(s) for cascaded messages returned from receptors.
/// </summary>
/// <remarks>
/// <para>
/// When a receptor returns messages (events or commands), the dispatcher needs to know
/// where to send them. DispatchModes is a flags enum that allows specifying routing behavior:
/// </para>
/// <list type="bullet">
///   <item><b>Local</b>: Dispatch to in-process receptors AND persist to event store</item>
///   <item><b>LocalNoPersist</b>: Dispatch to in-process receptors only (no persistence)</item>
///   <item><b>Outbox</b>: Write to outbox for transport to other services</item>
///   <item><b>Both</b>: Local dispatch AND outbox write</item>
///   <item><b>EventStoreOnly</b>: Persist to event store only (no local dispatch)</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Return event to local receptors with persistence to event store
/// return Route.Local(new OrderCreatedEvent { OrderId = orderId });
///
/// // Return event to local receptors only (no persistence, ephemeral)
/// return Route.LocalNoPersist(new CacheInvalidatedEvent { Key = "users" });
///
/// // Return event to outbox for transport
/// return Route.Outbox(new UserCreatedEvent { UserId = userId });
///
/// // Return event to both local AND outbox
/// return Route.Both(new OrderCompletedEvent { OrderId = orderId });
///
/// // Return event directly to event store (no local receptors)
/// return Route.EventStoreOnly(new AuditEvent { Action = "login" });
/// </code>
/// </example>
/// <docs>fundamentals/dispatcher/dispatcher#routed-message-cascading</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatch/DispatchModeTests.cs</tests>
[Flags]
public enum DispatchModes {
  /// <summary>
  /// No routing - message is not dispatched.
  /// </summary>
  None = 0,

  /// <summary>
  /// Invoke in-process receptors (base flag).
  /// </summary>
  LocalDispatch = 1,

  /// <summary>
  /// Write to outbox for transport delivery to other services.
  /// Message will be sent via the configured transport (Kafka, RabbitMQ, etc).
  /// Events going through the outbox are automatically stored in the event store.
  /// </summary>
  Outbox = 2,

  /// <summary>
  /// Direct event storage (without going through outbox transport).
  /// Events are stored to wh_event_store and perspective events are created,
  /// but no cross-service transport occurs.
  /// </summary>
  EventStore = 4,

  /// <summary>
  /// Dispatch to in-process receptors AND persist to event store.
  /// This is the recommended mode for local event handling with durability.
  /// Events are stored via the outbox (with null destination to skip transport).
  /// </summary>
  Local = LocalDispatch | EventStore,

  /// <summary>
  /// Dispatch to in-process receptors only (no persistence).
  /// Use for ephemeral events like cache invalidation that don't need durability.
  /// This was the behavior of Route.Local() before persistence was added.
  /// </summary>
  LocalNoPersist = LocalDispatch,

  /// <summary>
  /// Both local dispatch AND outbox write (cross-service delivery).
  /// Message is handled by local receptors AND sent to other services.
  /// Events are stored via the normal outbox flow.
  /// </summary>
  Both = LocalDispatch | Outbox,

  /// <summary>
  /// Persist to event store only (no local dispatch, no cross-service transport).
  /// Use for audit events or when you need persistence without immediate processing.
  /// </summary>
  EventStoreOnly = EventStore
}
