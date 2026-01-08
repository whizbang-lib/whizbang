namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered receptor.
/// This record uses value equality which is critical for incremental generator performance.
/// Supports both IReceptor&lt;TMessage, TResponse&gt; and IReceptor&lt;TMessage&gt; (void) patterns.
/// Enhanced in Phase 2 to include lifecycle stage information from [FireAt] attributes.
/// </summary>
/// <param name="ClassName">Fully qualified class name (e.g., "MyApp.Receptors.OrderReceptor")</param>
/// <param name="MessageType">Fully qualified message type (e.g., "MyApp.Commands.CreateOrder")</param>
/// <param name="ResponseType">Fully qualified response type (e.g., "MyApp.Events.OrderCreated"), or null for void receptors</param>
/// <param name="LifecycleStages">Lifecycle stages at which this receptor should fire (from [FireAt] attributes). Empty if no [FireAt] attributes (defaults to ImmediateAsync).</param>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorInfoTests.cs</tests>
public sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string? ResponseType,
    string[] LifecycleStages
) {
  /// <summary>
  /// True if this is a void receptor (IReceptor&lt;TMessage&gt;), false if it returns a response (IReceptor&lt;TMessage, TResponse&gt;).
  /// </summary>
  public bool IsVoid => ResponseType is null;

  /// <summary>
  /// True if receptor has no [FireAt] attributes (should default to ImmediateAsync).
  /// </summary>
  public bool HasDefaultStage => LifecycleStages.Length == 0;
};
