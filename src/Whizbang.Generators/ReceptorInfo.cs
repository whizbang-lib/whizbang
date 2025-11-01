namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered receptor.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="ClassName">Fully qualified class name (e.g., "MyApp.Receptors.OrderReceptor")</param>
/// <param name="MessageType">Fully qualified message type (e.g., "MyApp.Commands.CreateOrder")</param>
/// <param name="ResponseType">Fully qualified response type (e.g., "MyApp.Events.OrderCreated")</param>
internal sealed record ReceptorInfo(
    string ClassName,
    string MessageType,
    string ResponseType
);
