namespace Whizbang.Core.Dispatch;

/// <summary>
/// Represents an explicitly empty value in a discriminated union tuple.
/// Used with <see cref="Route.None"/> to indicate "no value" for a specific path.
/// </summary>
/// <remarks>
/// <para>
/// RoutedNone is used in discriminated union patterns where a receptor returns
/// multiple possible outcomes but only one is populated:
/// </para>
/// <code>
/// // Success path - failure is Route.None()
/// return (success: orderCreated, failure: Route.None());
///
/// // Failure path - success is Route.None()
/// return (success: Route.None(), failure: orderFailed);
/// </code>
/// <para>
/// RoutedNone values are automatically skipped during message extraction and cascade.
/// They cannot be extracted as RPC responses.
/// </para>
/// </remarks>
/// <docs>core-concepts/rpc-extraction#discriminated-unions</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatch/RouteTests.cs:None_*</tests>
public readonly struct RoutedNone : IRouted {
  /// <summary>
  /// Gets the wrapped value, which is always null for RoutedNone.
  /// </summary>
  public object? Value => null;

  /// <summary>
  /// Gets the dispatch mode, which is always None for RoutedNone.
  /// </summary>
  public DispatchMode Mode => DispatchMode.None;
}

/// <summary>
/// Non-generic interface for pattern matching on routed values.
/// Enables MessageExtractor to detect and unwrap Routed&lt;T&gt; without knowing T.
/// </summary>
/// <remarks>
/// <para>
/// IRouted provides a non-generic view of routing information, allowing code to:
/// </para>
/// <list type="bullet">
///   <item>Pattern match with <c>obj is IRouted</c></item>
///   <item>Access the wrapped value as <see cref="object"/></item>
///   <item>Access the <see cref="DispatchMode"/> without knowing the generic type</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Pattern matching in MessageExtractor
/// if (result is IRouted routed) {
///   var innerValue = routed.Value;
///   var mode = routed.Mode;
///   // Process with routing info...
/// }
/// </code>
/// </example>
/// <docs>core-concepts/dispatcher#routed-message-cascading</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatch/RoutedTests.cs</tests>
public interface IRouted {
  /// <summary>
  /// Gets the wrapped value as an object.
  /// </summary>
  object? Value { get; }

  /// <summary>
  /// Gets the dispatch mode for routing the wrapped value.
  /// </summary>
  DispatchMode Mode { get; }
}

/// <summary>
/// Wraps a value with explicit dispatch routing information.
/// </summary>
/// <typeparam name="T">The type of the wrapped value.</typeparam>
/// <remarks>
/// <para>
/// Routed&lt;T&gt; is a readonly struct that associates a value with a <see cref="DispatchMode"/>.
/// It's used to explicitly control where cascaded messages are dispatched:
/// </para>
/// <list type="bullet">
///   <item><b>Local</b>: In-process receptors only</item>
///   <item><b>Outbox</b>: Transport to other services</item>
///   <item><b>Both</b>: Both local and outbox</item>
/// </list>
/// <para>
/// Use the <see cref="Route"/> static class for convenient factory methods.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Using factory methods (preferred)
/// return Route.Local(new CacheInvalidatedEvent { Key = "users" });
/// return Route.Outbox(new UserCreatedEvent { UserId = userId });
/// return Route.Both(new AuditLogEvent { Action = "create" });
///
/// // Using constructor directly
/// return new Routed&lt;MyEvent&gt;(myEvent, DispatchMode.Local);
/// </code>
/// </example>
/// <docs>core-concepts/dispatcher#routed-message-cascading</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatch/RoutedTests.cs</tests>
public readonly struct Routed<T> : IRouted {
  /// <summary>
  /// Gets the wrapped value.
  /// </summary>
  public T Value { get; }

  /// <summary>
  /// Gets the dispatch mode for routing this value.
  /// </summary>
  public DispatchMode Mode { get; }

  /// <summary>
  /// Creates a new routed wrapper with the specified value and dispatch mode.
  /// </summary>
  /// <param name="value">The value to wrap.</param>
  /// <param name="mode">The dispatch mode for routing.</param>
  public Routed(T value, DispatchMode mode) {
    Value = value;
    Mode = mode;
  }

  /// <summary>
  /// Gets the wrapped value as an object (explicit interface implementation).
  /// </summary>
  object? IRouted.Value => Value;
}
