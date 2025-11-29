using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Core;

/// <summary>
/// Thrown when no handler is found for a given message type.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="HandlerNotFoundException"/> class.
/// </remarks>
/// <param name="messageType">The message type that has no handler</param>
[Serializable]
public class HandlerNotFoundException(Type messageType) : Exception(_formatMessage(messageType)) {
  /// <summary>
  /// The type of message that has no handler.
  /// </summary>
  public Type MessageType { get; } = messageType;

  private static string _formatMessage(Type messageType) {
    return $@"No handler found for message type '{messageType.Name}'.

To fix this:
1. Create a receptor that implements IReceptor<{messageType.Name}, TResponse>
2. Add the [WhizbangHandler] attribute to the receptor
3. Ensure the receptor is in a scanned assembly

Example:
[WhizbangHandler]
public class {messageType.Name}Receptor : IReceptor<{messageType.Name}, {messageType.Name}Result> {{
    public async Task<{messageType.Name}Result> Receive({messageType.Name} message) {{
        // Handle message
        return new {messageType.Name}Result();
    }}
}}";
  }
}
