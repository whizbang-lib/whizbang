#pragma warning disable S3604 // Primary constructor field/property initializers are intentional

namespace Whizbang.Core;

/// <summary>
/// Thrown when no receptor is found for a given message type.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ReceptorNotFoundException"/> class.
/// </remarks>
/// <param name="messageType">The message type that has no receptor</param>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Send_WithUnknownMessageType_ShouldThrowReceptorNotFoundExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvoke_WithUnknownMessageType_ShouldThrowReceptorNotFoundExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_VoidReceptor_NoReceptor_ShouldThrowReceptorNotFoundExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Integration.Tests/DispatcherReceptorIntegrationTests.cs:Integration_UnregisteredMessage_ShouldThrowReceptorNotFoundAsync</tests>
[Serializable]
#pragma warning disable S3925 // ISerializable not needed — binary serialization is deprecated in modern .NET
public class ReceptorNotFoundException(Type messageType) : Exception(_formatMessage(messageType)) {
#pragma warning restore S3925
  public ReceptorNotFoundException() : this(typeof(object)) {
  }

  public ReceptorNotFoundException(string? message) : this(typeof(object)) {
  }

  public ReceptorNotFoundException(string? message, Exception? innerException) : this(typeof(object)) {
  }

  /// <summary>
  /// The type of message that has no receptor.
  /// </summary>
  public Type MessageType => messageType;

  private static string _formatMessage(Type messageType) {
    return $@"No receptor found for message type '{messageType.FullName}'.

To fix this:
1. Create a receptor that implements IReceptor<{messageType.Name}, TResponse>
2. Ensure the receptor is in a project that references Whizbang.Generators
3. The receptor will be auto-discovered at compile time (no attribute needed)

Example:
public class {messageType.Name}Receptor : IReceptor<{messageType.Name}, {messageType.Name}Result> {{
    public async Task<{messageType.Name}Result> HandleAsync({messageType.Name} message) {{
        // Handle message
        return new {messageType.Name}Result();
    }}
}}";
  }
}
