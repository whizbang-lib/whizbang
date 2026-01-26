using TUnit.Core;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.SystemEvents.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for AccessDeniedException.
/// AccessDeniedException is thrown when access is denied due to insufficient permissions.
/// </summary>
/// <tests>AccessDeniedException</tests>
[Category("Security")]
public class AccessDeniedExceptionTests {
  #region Default Constructor Tests

  [Test]
  public async Task DefaultConstructor_SetsDefaultMessageAsync() {
    // Arrange & Act
    var exception = new AccessDeniedException();

    // Assert
    await Assert.That(exception.Message).IsEqualTo("Access denied");
    await Assert.That(exception.RequiredPermission).IsEqualTo(default(Permission));
    await Assert.That(exception.ResourceType).IsEqualTo(string.Empty);
    await Assert.That(exception.Reason).IsEqualTo(AccessDenialReason.InsufficientPermission);
  }

  #endregion

  #region Message Constructor Tests

  [Test]
  public async Task MessageConstructor_SetsCustomMessageAsync() {
    // Arrange
    var message = "Custom access denied message";

    // Act
    var exception = new AccessDeniedException(message);

    // Assert
    await Assert.That(exception.Message).IsEqualTo(message);
    await Assert.That(exception.RequiredPermission).IsEqualTo(default(Permission));
    await Assert.That(exception.ResourceType).IsEqualTo(string.Empty);
    await Assert.That(exception.Reason).IsEqualTo(AccessDenialReason.InsufficientPermission);
  }

  #endregion

  #region Message and InnerException Constructor Tests

  [Test]
  public async Task MessageAndInnerExceptionConstructor_SetsMessageAndInnerAsync() {
    // Arrange
    var message = "Access denied with inner exception";
    var innerException = new InvalidOperationException("Inner error");

    // Act
    var exception = new AccessDeniedException(message, innerException);

    // Assert
    await Assert.That(exception.Message).IsEqualTo(message);
    await Assert.That(exception.InnerException).IsEqualTo(innerException);
    await Assert.That(exception.RequiredPermission).IsEqualTo(default(Permission));
    await Assert.That(exception.ResourceType).IsEqualTo(string.Empty);
    await Assert.That(exception.Reason).IsEqualTo(AccessDenialReason.InsufficientPermission);
  }

  #endregion

  #region Full Details Constructor Tests

  [Test]
  public async Task AccessDeniedException_Constructor_SetsAllPropertiesAsync() {
    // Arrange
    var permission = Permission.Read("orders");

    // Act
    var exception = new AccessDeniedException(
      permission,
      "Order",
      "order-123",
      AccessDenialReason.InsufficientPermission);

    // Assert
    await Assert.That(exception.RequiredPermission).IsEqualTo(permission);
    await Assert.That(exception.ResourceType).IsEqualTo("Order");
    await Assert.That(exception.ResourceId).IsEqualTo("order-123");
    await Assert.That(exception.Reason).IsEqualTo(AccessDenialReason.InsufficientPermission);
  }

  [Test]
  public async Task AccessDeniedException_Message_ContainsDetailsAsync() {
    // Arrange
    var permission = Permission.Write("orders");

    // Act
    var exception = new AccessDeniedException(
      permission,
      "Order",
      "order-456",
      AccessDenialReason.InsufficientPermission);

    // Assert
    await Assert.That(exception.Message).Contains("Order");
    await Assert.That(exception.Message).Contains("order-456");
    await Assert.That(exception.Message).Contains("orders:write");
  }

  [Test]
  public async Task AccessDeniedException_WithoutResourceId_MessageDoesNotContainParensAsync() {
    // Arrange
    var permission = Permission.Delete("users");

    // Act
    var exception = new AccessDeniedException(
      permission,
      "User",
      resourceId: null,
      AccessDenialReason.ScopeViolation);

    // Assert
    await Assert.That(exception.Message).Contains("User");
    await Assert.That(exception.Message).Contains("users:delete");
    await Assert.That(exception.ResourceId).IsNull();
  }

  [Test]
  public async Task AccessDeniedException_IsException_ReturnsTrueAsync() {
    // Arrange
    var exception = new AccessDeniedException(
      Permission.Read("orders"),
      "Order");

    // Assert
    await Assert.That(exception is Exception).IsTrue();
  }

  [Test]
  public async Task AccessDeniedException_DefaultReason_IsInsufficientPermissionAsync() {
    // Arrange & Act
    var exception = new AccessDeniedException(
      Permission.Read("orders"),
      "Order");

    // Assert
    await Assert.That(exception.Reason).IsEqualTo(AccessDenialReason.InsufficientPermission);
  }

  [Test]
  public async Task AccessDeniedException_CanBeCaughtAsync() {
    // Arrange & Act & Assert
    try {
      throw new AccessDeniedException(
        Permission.Admin("system"),
        "System",
        reason: AccessDenialReason.PolicyRejected);
    } catch (AccessDeniedException ex) {
      await Assert.That(ex.Reason).IsEqualTo(AccessDenialReason.PolicyRejected);
    }
  }

  #endregion
}
