using Whizbang.Core.Security.Exceptions;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for SecurityContextRequiredException.
/// </summary>
/// <docs>core-concepts/message-security#exceptions</docs>
public class SecurityContextRequiredExceptionTests {
  // ========================================
  // Constructor Tests
  // ========================================

  [Test]
  public async Task DefaultConstructor_SetsDefaultMessageAsync() {
    // Act
    var exception = new SecurityContextRequiredException();

    // Assert
    await Assert.That(exception.Message).IsEqualTo(
      "Security context is required but could not be established from the message.");
    await Assert.That(exception.MessageType).IsNull();
  }

  [Test]
  public async Task StringConstructor_SetsCustomMessageAsync() {
    // Arrange
    const string customMessage = "Custom security error message";

    // Act
    var exception = new SecurityContextRequiredException(customMessage);

    // Assert
    await Assert.That(exception.Message).IsEqualTo(customMessage);
    await Assert.That(exception.MessageType).IsNull();
  }

  [Test]
  public async Task TypeConstructor_SetsMessageWithTypeNameAsync() {
    // Arrange
    var messageType = typeof(TestMessage);

    // Act
    var exception = new SecurityContextRequiredException(messageType);

    // Assert
    await Assert.That(exception.Message).IsEqualTo(
      "Security context is required for message type 'Whizbang.Core.Tests.Security.SecurityContextRequiredExceptionTests+TestMessage' but could not be established.");
    await Assert.That(exception.MessageType).IsEqualTo(messageType);
  }

  [Test]
  public async Task MessageAndInnerExceptionConstructor_SetsBothAsync() {
    // Arrange
    const string customMessage = "Custom security error message";
    var innerException = new InvalidOperationException("Inner error");

    // Act
    var exception = new SecurityContextRequiredException(customMessage, innerException);

    // Assert
    await Assert.That(exception.Message).IsEqualTo(customMessage);
    await Assert.That(exception.InnerException).IsEqualTo(innerException);
    await Assert.That(exception.MessageType).IsNull();
  }

  // ========================================
  // Inheritance Tests
  // ========================================

  [Test]
  public async Task Exception_InheritsFromExceptionAsync() {
    // Act
    var exception = new SecurityContextRequiredException();

    // Assert
    await Assert.That(exception).IsAssignableTo<Exception>();
  }

  [Test]
  public async Task Exception_CanBeCaughtAsExceptionAsync() {
    // Act & Assert
    await Assert.That(() => {
      throw new SecurityContextRequiredException();
    }).ThrowsExactly<SecurityContextRequiredException>();
  }

  // ========================================
  // Test Doubles
  // ========================================

  private sealed record TestMessage(string Value);
}
