namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered receptor.
/// This record uses value equality which is critical for incremental generator performance.
/// Supports both IReceptor&lt;TMessage, TResponse&gt; and IReceptor&lt;TMessage&gt; (void) patterns.
/// Also supports ISyncReceptor&lt;TMessage, TResponse&gt; and ISyncReceptor&lt;TMessage&gt; (sync) patterns.
/// Enhanced in Phase 2 to include lifecycle stage information from [FireAt] attributes.
/// </summary>
/// <param name="ClassName">Fully qualified class name (e.g., "MyApp.Receptors.OrderReceptor")</param>
/// <param name="MessageType">Fully qualified message type (e.g., "MyApp.Commands.CreateOrder")</param>
/// <param name="ResponseType">Fully qualified response type (e.g., "MyApp.Events.OrderCreated"), or null for void receptors</param>
/// <param name="LifecycleStages">Lifecycle stages at which this receptor should fire (from [FireAt] attributes). Empty if no [FireAt] attributes (defaults to ImmediateAsync).</param>
/// <param name="IsSync">True if this is a sync receptor (ISyncReceptor), false for async receptor (IReceptor).</param>
/// <param name="DefaultRouting">Default dispatch routing from [DefaultRouting] attribute on the receptor class. Null if no attribute.</param>
/// <param name="SyncAttributes">Perspective sync attributes from [AwaitPerspectiveSync] attributes. Empty if no attributes.</param>
/// <param name="HasTraceAttribute">True if receptor class has [WhizbangTrace] attribute for explicit tracing.</param>
/// <param name="IsMessageAnEvent">True if the message type implements IEvent. Used to determine if perspective sync should be generated.</param>
/// <param name="IsPolymorphicMessageType">True if the message type is an interface or non-sealed class, meaning concrete subtypes should be expanded at compile time.</param>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorInfoTests.cs</tests>
public sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string? ResponseType,
    string[] LifecycleStages,
    bool IsSync = false,
    string? DefaultRouting = null,
    SyncAttributeInfo[]? SyncAttributes = null,
    bool HasTraceAttribute = false,
    bool IsMessageAnEvent = false,
    bool IsPolymorphicMessageType = false
) {
  /// <summary>
  /// True if this is a void receptor (IReceptor&lt;TMessage&gt; or ISyncReceptor&lt;TMessage&gt;), false if it returns a response.
  /// </summary>
  public bool IsVoid => ResponseType is null;

  /// <summary>
  /// True if receptor has no [FireAt] attributes (should default to ImmediateAsync).
  /// </summary>
  public bool HasDefaultStage => LifecycleStages.Length == 0;

  /// <summary>
  /// True if receptor has a [DefaultRouting] attribute.
  /// </summary>
  public bool HasDefaultRouting => DefaultRouting is not null;

  /// <summary>
  /// True if receptor has any [AwaitPerspectiveSync] attributes.
  /// </summary>
  public bool HasSyncAttributes => SyncAttributes is { Length: > 0 };
};

/// <summary>
/// Value type containing information about a perspective sync attribute.
/// Uses string representations of types for value equality and serialization.
/// </summary>
/// <param name="PerspectiveType">Fully qualified perspective type name.</param>
/// <param name="EventTypes">Fully qualified event type names, or null for all events.</param>
/// <param name="TimeoutMs">The timeout in milliseconds.</param>
/// <param name="FireBehavior">The fire behavior value (0=FireOnSuccess, 1=FireAlways, 2=FireOnEachEvent).</param>
public sealed record SyncAttributeInfo(
    string PerspectiveType,
    string[]? EventTypes,
    int TimeoutMs,
    int FireBehavior
);
