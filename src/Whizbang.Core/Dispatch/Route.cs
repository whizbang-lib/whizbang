namespace Whizbang.Core.Dispatch;

/// <summary>
/// Static factory class for creating <see cref="Routed{T}"/> instances with explicit dispatch routing.
/// </summary>
/// <remarks>
/// <para>
/// Use Route to wrap receptor return values with routing information:
/// </para>
/// <list type="bullet">
///   <item><b>Local</b>: Dispatch to in-process receptors only</item>
///   <item><b>Outbox</b>: Write to outbox for transport to other services</item>
///   <item><b>Both</b>: Both local dispatch AND outbox write</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Single event routed locally
/// return Route.Local(new CacheInvalidatedEvent { Key = "users" });
///
/// // Single event routed to outbox
/// return Route.Outbox(new UserCreatedEvent { UserId = userId });
///
/// // Single event routed to both local and outbox
/// return Route.Both(new AuditLogEvent { Action = "create" });
///
/// // Array routed to local (all items get same routing)
/// return Route.Local(new IEvent[] { evt1, evt2, evt3 });
///
/// // Tuple with per-item routing
/// return (
///   Route.Local(new CacheInvalidatedEvent { Key = "users" }),
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
  /// Wraps a value for local-only dispatch (in-process receptors).
  /// </summary>
  /// <typeparam name="T">The type of value to wrap.</typeparam>
  /// <param name="value">The value to route locally.</param>
  /// <returns>A routed wrapper with <see cref="DispatchMode.Local"/>.</returns>
  /// <example>
  /// <code>
  /// return Route.Local(new CacheInvalidatedEvent { Key = "users" });
  /// </code>
  /// </example>
  /// <tests>tests/Whizbang.Core.Tests/Dispatch/RouteTests.cs:Local_WithValue_ReturnsRoutedWithLocalModeAsync</tests>
  public static Routed<T> Local<T>(T value) => new(value, DispatchMode.Local);

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
}
