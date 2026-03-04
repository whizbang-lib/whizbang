namespace Whizbang.Core.Security.Exceptions;

/// <summary>
/// Exception thrown when security context is required but could not be established.
/// </summary>
/// <remarks>
/// This exception is thrown when:
/// - MessageSecurityOptions.AllowAnonymous is false
/// - No extractor could establish a security context
///
/// This indicates a security policy violation - the message was expected
/// to carry authentication/authorization information but none was found.
/// </remarks>
/// <docs>core-concepts/message-security#exceptions</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageSecurityContextProviderTests.cs:EstablishContextAsync_NoExtractors_AllowAnonymousFalse_ThrowsSecurityContextRequiredExceptionAsync</tests>
public sealed class SecurityContextRequiredException : Exception {
  /// <summary>
  /// The type of message that required security context.
  /// </summary>
  public Type? MessageType { get; }

  /// <summary>
  /// Creates a new SecurityContextRequiredException.
  /// </summary>
  public SecurityContextRequiredException()
    : base("Security context is required but could not be established from the message.") {
  }

  /// <summary>
  /// Creates a new SecurityContextRequiredException with a custom message.
  /// </summary>
  /// <param name="message">The exception message</param>
  public SecurityContextRequiredException(string message)
    : base(message) {
  }

  /// <summary>
  /// Creates a new SecurityContextRequiredException with message type information.
  /// </summary>
  /// <param name="messageType">The type of message that required security context</param>
  public SecurityContextRequiredException(Type messageType)
    : base($"Security context is required for message type '{messageType.FullName}' but could not be established.") {
    MessageType = messageType;
  }

  /// <summary>
  /// Creates a new SecurityContextRequiredException with a custom message and inner exception.
  /// </summary>
  /// <param name="message">The exception message</param>
  /// <param name="innerException">The inner exception</param>
  public SecurityContextRequiredException(string message, Exception innerException)
    : base(message, innerException) {
  }
}
