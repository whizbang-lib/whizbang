using Whizbang.Core.Observability;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for SecurityContext - security metadata that can change from hop to hop.
/// </summary>
public class SecurityContextTests {
  [Test]
  public async Task Constructor_SetsUserIdAsync() {
    // Arrange & Act
    var context = new SecurityContext {
      UserId = "user-123"
    };

    // Assert
    await Assert.That(context.UserId).IsEqualTo("user-123");
  }

  [Test]
  public async Task Constructor_SetsTenantIdAsync() {
    // Arrange & Act
    var context = new SecurityContext {
      TenantId = "tenant-abc"
    };

    // Assert
    await Assert.That(context.TenantId).IsEqualTo("tenant-abc");
  }

  [Test]
  public async Task Constructor_AllowsNullUserIdAsync() {
    // Arrange & Act
    var context = new SecurityContext {
      UserId = null,
      TenantId = "tenant-abc"
    };

    // Assert
    await Assert.That(context.UserId).IsNull();
  }

  [Test]
  public async Task Constructor_AllowsNullTenantIdAsync() {
    // Arrange & Act
    var context = new SecurityContext {
      UserId = "user-123",
      TenantId = null
    };

    // Assert
    await Assert.That(context.TenantId).IsNull();
  }

  [Test]
  public async Task Constructor_AllowsBothNullAsync() {
    // Arrange & Act
    var context = new SecurityContext {
      UserId = null,
      TenantId = null
    };

    // Assert
    await Assert.That(context.UserId).IsNull();
    await Assert.That(context.TenantId).IsNull();
  }

  [Test]
  public async Task RecordEquality_SameValues_AreEqualAsync() {
    // Arrange
    var context1 = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    var context2 = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    // Assert
    await Assert.That(context1).IsEqualTo(context2);
  }

  [Test]
  public async Task RecordEquality_DifferentUserId_AreNotEqualAsync() {
    // Arrange
    var context1 = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    var context2 = new SecurityContext {
      UserId = "user-456",
      TenantId = "tenant-abc"
    };

    // Assert
    await Assert.That(context1).IsNotEqualTo(context2);
  }

  [Test]
  public async Task RecordEquality_DifferentTenantId_AreNotEqualAsync() {
    // Arrange
    var context1 = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    var context2 = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-xyz"
    };

    // Assert
    await Assert.That(context1).IsNotEqualTo(context2);
  }

  [Test]
  public async Task WithUserId_CreatesNewInstance_WithUpdatedUserIdAsync() {
    // Arrange
    var original = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    // Act
    var updated = original with { UserId = "user-456" };

    // Assert
    await Assert.That(updated.UserId).IsEqualTo("user-456");
    await Assert.That(updated.TenantId).IsEqualTo("tenant-abc");
    await Assert.That(original.UserId).IsEqualTo("user-123"); // Original unchanged
  }

  [Test]
  public async Task WithTenantId_CreatesNewInstance_WithUpdatedTenantIdAsync() {
    // Arrange
    var original = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    // Act
    var updated = original with { TenantId = "tenant-xyz" };

    // Assert
    await Assert.That(updated.UserId).IsEqualTo("user-123");
    await Assert.That(updated.TenantId).IsEqualTo("tenant-xyz");
    await Assert.That(original.TenantId).IsEqualTo("tenant-abc"); // Original unchanged
  }
}
