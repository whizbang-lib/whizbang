namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered receptor.
/// This record uses value equality which is critical for incremental generator performance.
/// Supports both IReceptor&lt;TMessage, TResponse&gt; and IReceptor&lt;TMessage&gt; (void) patterns.
/// </summary>
/// <param name="ClassName">Fully qualified class name (e.g., "MyApp.Receptors.OrderReceptor")</param>
/// <param name="MessageType">Fully qualified message type (e.g., "MyApp.Commands.CreateOrder")</param>
/// <param name="ResponseType">Fully qualified response type (e.g., "MyApp.Events.OrderCreated"), or null for void receptors</param>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorInfoTests.cs</tests>
internal sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string? ResponseType
) {
  /// <summary>
  /// True if this is a void receptor (IReceptor&lt;TMessage&gt;), false if it returns a response (IReceptor&lt;TMessage, TResponse&gt;).
  /// </summary>
  public bool IsVoid => ResponseType is null;
};
