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
  [Arguments("user-123", "tenant-abc", "Both values set")]
  [Arguments(null, "tenant-abc", "Null UserId")]
  [Arguments("user-123", null, "Null TenantId")]
  [Arguments(null, null, "Both null")]
  public async Task Constructor_VariousNullCombinations_HandlesCorrectlyAsync(string? userId, string? tenantId, string description) {
    // Arrange & Act
    var context = new SecurityContext {
      UserId = userId,
      TenantId = tenantId
    };

    // Assert
    await Assert.That(context.UserId).IsEqualTo(userId);
    await Assert.That(context.TenantId).IsEqualTo(tenantId);
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
  [Arguments("user-456", "tenant-abc", "Different UserId")]
  [Arguments("user-123", "tenant-xyz", "Different TenantId")]
  [Arguments("user-456", "tenant-xyz", "Both different")]
  public async Task RecordEquality_DifferentValues_AreNotEqualAsync(string userId2, string tenantId2, string description) {
    // Arrange
    var context1 = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    var context2 = new SecurityContext {
      UserId = userId2,
      TenantId = tenantId2
    };

    // Assert
    await Assert.That(context1).IsNotEqualTo(context2);
  }

  /// <summary>
  /// Data source for 'with' expression tests.
  /// Returns a function that applies the 'with' expression and the expected values.
  /// </summary>
  public static IEnumerable<Func<(Func<SecurityContext, SecurityContext> withExpression, string expectedUserId, string expectedTenantId, string originalProperty, string description)>> GetWithExpressions() {
    yield return () => (
      ctx => ctx with { UserId = "user-456" },
      "user-456",
      "tenant-abc",
      "UserId",
      "Update UserId"
    );
    yield return () => (
      ctx => ctx with { TenantId = "tenant-xyz" },
      "user-123",
      "tenant-xyz",
      "TenantId",
      "Update TenantId"
    );
    yield return () => (
      ctx => ctx with { UserId = "user-789", TenantId = "tenant-def" },
      "user-789",
      "tenant-def",
      "Both",
      "Update both"
    );
  }

  [Test]
  [MethodDataSource(nameof(GetWithExpressions))]
  public async Task WithExpression_CreatesNewInstance_WithUpdatedValuesAsync(
      Func<SecurityContext, SecurityContext> withExpression,
      string expectedUserId,
      string expectedTenantId,
      string originalProperty,
      string description) {
    // Arrange
    var original = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    // Act
    var updated = withExpression(original);

    // Assert - Updated instance has new values
    await Assert.That(updated.UserId).IsEqualTo(expectedUserId);
    await Assert.That(updated.TenantId).IsEqualTo(expectedTenantId);

    // Assert - Original instance is unchanged
    await Assert.That(original.UserId).IsEqualTo("user-123");
    await Assert.That(original.TenantId).IsEqualTo("tenant-abc");
  }
}
