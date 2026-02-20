namespace Whizbang.Core.Dispatch;

/// <summary>
/// Static factory class for creating <see cref="Routed{T}"/> instances with explicit dispatch routing.
/// </summary>
/// <remarks>
/// <para>
/// Use Route to wrap receptor return values with routing information:
/// </para>
/// <list type="bullet">
///   <item><b>Local</b>: Dispatch to in-process receptors AND persist to event store</item>
///   <item><b>LocalNoPersist</b>: Dispatch to in-process receptors only (no persistence)</item>
///   <item><b>Outbox</b>: Write to outbox for transport to other services</item>
///   <item><b>Both</b>: Both local dispatch AND outbox write</item>
///   <item><b>EventStoreOnly</b>: Persist to event store only (no local dispatch)</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Single event routed locally with persistence
/// return Route.Local(new OrderCreatedEvent { OrderId = orderId });
///
/// // Single event routed locally without persistence (ephemeral)
/// return Route.LocalNoPersist(new CacheInvalidatedEvent { Key = "users" });
///
/// // Single event routed to outbox
/// return Route.Outbox(new UserCreatedEvent { UserId = userId });
///
/// // Single event routed to both local and outbox
/// return Route.Both(new AuditLogEvent { Action = "create" });
///
/// // Single event routed directly to event store (no local receptors)
/// return Route.EventStoreOnly(new AuditEvent { Action = "login" });
///
/// // Array routed to local (all items get same routing)
/// return Route.Local(new IEvent[] { evt1, evt2, evt3 });
///
/// // Tuple with per-item routing
/// return (
///   Route.Local(new OrderCreatedEvent { OrderId = orderId }),
///   Route.Outbox(new UserCreatedEvent { UserId = userId })
/// );
/// </code>
/// </example>
/// <docs>core-concepts/dispatcher#routed-message-cascading</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatch/RouteTests.cs</tests>
public static class Route {
  /// <summary>
  /// Returns a value indicating "no value" for use in discriminated union tuples.
  /// </summary>
  /// <returns>A <see cref="RoutedNone"/> value that is skipped during extraction and cascade.</returns>
  /// <remarks>
  /// <para>
  /// Use Route.None() in discriminated union patterns where a receptor returns
  /// multiple possible outcomes but only one is populated:
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Success path
  /// return (success: orderCreated, failure: Route.None());
  ///
  /// // Failure path
  /// return (success: Route.None(), failure: orderFailed);
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/RouteTests.cs:None_*</tests>
  public static RoutedNone None() => default;

  /// <summary>
  /// Wraps a value for local dispatch with persistence to event store.
  /// </summary>
  /// <typeparam name="T">The type of value to wrap.</typeparam>
  /// <param name="value">The value to route locally with persistence.</param>
  /// <returns>A routed wrapper with <see cref="DispatchMode.Local"/>.</returns>
  /// <remarks>
  /// Events are dispatched to in-process receptors AND persisted to the event store.
  /// This is the recommended mode for local event handling with durability.
  /// Use <see cref="LocalNoPersist{T}(T)"/> for ephemeral events that don't need persistence.
  /// </remarks>
  /// <example>
  /// <code>
  /// return Route.Local(new OrderCreatedEvent { OrderId = orderId });
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/RouteTests.cs:Local_WithValue_ReturnsRoutedWithLocalModeAsync</tests>
  public static Routed<T> Local<T>(T value) => new(value, DispatchMode.Local);

  /// <summary>
  /// Wraps a value for local-only dispatch without persistence (ephemeral).
  /// </summary>
  /// <typeparam name="T">The type of value to wrap.</typeparam>
  /// <param name="value">The value to route locally without persistence.</param>
  /// <returns>A routed wrapper with <see cref="DispatchMode.LocalNoPersist"/>.</returns>
  /// <remarks>
  /// Events are dispatched to in-process receptors only. No persistence to event store.
  /// Use for ephemeral events like cache invalidation that don't need durability.
  /// This was the behavior of <see cref="Local{T}(T)"/> before persistence was added.
  /// </remarks>
  /// <example>
  /// <code>
  /// return Route.LocalNoPersist(new CacheInvalidatedEvent { Key = "users" });
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/RouteTests.cs:LocalNoPersist_WithValue_ReturnsRoutedWithLocalNoPersistModeAsync</tests>
  public static Routed<T> LocalNoPersist<T>(T value) => new(value, DispatchMode.LocalNoPersist);

  /// <summary>
  /// Wraps a collection of values for local-only dispatch without persistence.
  /// </summary>
  /// <typeparam name="T">The type of values to wrap.</typeparam>
  /// <param name="values">The values to route locally without persistence.</param>
  /// <returns>An enumerable of routed wrappers with <see cref="DispatchMode.LocalNoPersist"/>.</returns>
  /// <example>
  /// <code>
  /// return Route.LocalNoPersist(new[] { evt1, evt2, evt3 });
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/RouteTests.cs:LocalNoPersist_WithCollection_ReturnsEnumerableOfRoutedAsync</tests>
  public static IEnumerable<Routed<T>> LocalNoPersist<T>(IEnumerable<T> values)
    => values.Select(v => new Routed<T>(v, DispatchMode.LocalNoPersist));

  /// <summary>
  /// Wraps a value for outbox-only dispatch (transport to other services).
  /// </summary>
  /// <typeparam name="T">The type of value to wrap.</typeparam>
  /// <param name="value">The value to route to outbox.</param>
  /// <returns>A routed wrapper with <see cref="DispatchMode.Outbox"/>.</returns>
  /// <example>
  /// <code>
  /// return Route.Outbox(new UserCreatedEvent { UserId = userId });
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/RouteTests.cs:Outbox_WithValue_ReturnsRoutedWithOutboxModeAsync</tests>
  public static Routed<T> Outbox<T>(T value) => new(value, DispatchMode.Outbox);

  /// <summary>
  /// Wraps a value for both local dispatch AND outbox write.
  /// </summary>
  /// <typeparam name="T">The type of value to wrap.</typeparam>
  /// <param name="value">The value to route to both destinations.</param>
  /// <returns>A routed wrapper with <see cref="DispatchMode.Both"/>.</returns>
  /// <example>
  /// <code>
  /// return Route.Both(new AuditLogEvent { Action = "create" });
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/RouteTests.cs:Both_WithValue_ReturnsRoutedWithBothModeAsync</tests>
  public static Routed<T> Both<T>(T value) => new(value, DispatchMode.Both);

  /// <summary>
  /// Wraps a value for direct event store persistence only (no local dispatch).
  /// </summary>
  /// <typeparam name="T">The type of value to wrap.</typeparam>
  /// <param name="value">The value to persist to event store.</param>
  /// <returns>A routed wrapper with <see cref="DispatchMode.EventStoreOnly"/>.</returns>
  /// <remarks>
  /// Events are persisted to the event store but NOT dispatched to local receptors.
  /// Use for audit events or when you need persistence without immediate processing.
  /// </remarks>
  /// <example>
  /// <code>
  /// return Route.EventStoreOnly(new AuditEvent { Action = "login" });
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/RouteTests.cs:EventStoreOnly_WithValue_ReturnsRoutedWithEventStoreOnlyModeAsync</tests>
  public static Routed<T> EventStoreOnly<T>(T value) => new(value, DispatchMode.EventStoreOnly);

  /// <summary>
  /// Wraps a collection of values for direct event store persistence only.
  /// </summary>
  /// <typeparam name="T">The type of values to wrap.</typeparam>
  /// <param name="values">The values to persist to event store.</param>
  /// <returns>An enumerable of routed wrappers with <see cref="DispatchMode.EventStoreOnly"/>.</returns>
  /// <example>
  /// <code>
  /// return Route.EventStoreOnly(new[] { auditEvt1, auditEvt2 });
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/RouteTests.cs:EventStoreOnly_WithCollection_ReturnsEnumerableOfRoutedAsync</tests>
  public static IEnumerable<Routed<T>> EventStoreOnly<T>(IEnumerable<T> values)
    => values.Select(v => new Routed<T>(v, DispatchMode.EventStoreOnly));
}
