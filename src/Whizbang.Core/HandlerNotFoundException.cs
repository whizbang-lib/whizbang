using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Core;

/// <summary>
/// Thrown when no handler is found for a given message type.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="HandlerNotFoundException"/> class.
/// </remarks>
/// <param name="messageType">The message type that has no handler</param>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Send_WithUnknownMessageType_ShouldThrowHandlerNotFoundExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvoke_WithUnknownMessageType_ShouldThrowHandlerNotFoundExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_VoidReceptor_NoHandler_ShouldThrowHandlerNotFoundExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Integration/DispatcherReceptorIntegrationTests.cs:Integration_UnregisteredMessage_ShouldThrowHandlerNotFoundAsync</tests>
[Serializable]
public class HandlerNotFoundException(Type messageType) : Exception(_formatMessage(messageType)) {
  public HandlerNotFoundException() : this(typeof(object)) {
  }

  public HandlerNotFoundException(string? message) : this(typeof(object)) {
  }

  public HandlerNotFoundException(string? message, Exception? innerException) : this(typeof(object)) {
  }

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
