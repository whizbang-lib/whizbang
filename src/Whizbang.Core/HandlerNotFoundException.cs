using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Core;

/// <summary>
/// Thrown when no handler is found for a given message type.
/// </summary>
[Serializable]
public class HandlerNotFoundException : Exception {
  /// <summary>
  /// The type of message that has no handler.
  /// </summary>
  [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
  public Type MessageType { get; }

  /// <summary>
  /// Initializes a new instance of the <see cref="HandlerNotFoundException"/> class.
  /// </summary>
  /// <param name="messageType">The message type that has no handler</param>
  public HandlerNotFoundException(Type messageType)
      : base(FormatMessage(messageType)) {
    MessageType = messageType;
  }

  private static string FormatMessage(Type messageType) {
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
