using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Scoping;

/// <summary>
/// Tests for the PerspectiveScope record.
/// </summary>
/// <tests>PerspectiveScope</tests>
public class PerspectiveScopeTests {
  // === Standard Property Tests ===

  [Test]
  public async Task PerspectiveScope_TenantId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "tenant-123" };

    // Assert
    await Assert.That(scope.TenantId).IsEqualTo("tenant-123");
  }

  [Test]
  public async Task PerspectiveScope_CustomerId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { CustomerId = "customer-456" };

    // Assert
    await Assert.That(scope.CustomerId).IsEqualTo("customer-456");
  }

  [Test]
  public async Task PerspectiveScope_UserId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { UserId = "user-789" };

    // Assert
    await Assert.That(scope.UserId).IsEqualTo("user-789");
  }

  [Test]
  public async Task PerspectiveScope_OrganizationId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { OrganizationId = "org-abc" };

    // Assert
    await Assert.That(scope.OrganizationId).IsEqualTo("org-abc");
  }

  // === Indexer Tests ===

  [Test]
  public async Task PerspectiveScope_Indexer_StandardProperty_TenantId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "tenant-123" };

    // Act
    var value = scope["TenantId"];

    // Assert
    await Assert.That(value).IsEqualTo("tenant-123");
  }

  [Test]
  public async Task PerspectiveScope_Indexer_StandardProperty_CustomerId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { CustomerId = "customer-456" };

    // Act
    var value = scope["CustomerId"];

    // Assert
    await Assert.That(value).IsEqualTo("customer-456");
  }

  [Test]
  public async Task PerspectiveScope_Indexer_StandardProperty_UserId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { UserId = "user-789" };

    // Act
    var value = scope["UserId"];

    // Assert
    await Assert.That(value).IsEqualTo("user-789");
  }

  [Test]
  public async Task PerspectiveScope_Indexer_StandardProperty_OrganizationId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { OrganizationId = "org-abc" };

    // Act
    var value = scope["OrganizationId"];

    // Assert
    await Assert.That(value).IsEqualTo("org-abc");
  }

  [Test]
  public async Task PerspectiveScope_Indexer_Extension_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope {
      Extensions = new Dictionary<string, string?> {
        ["CustomField"] = "custom-value",
        ["AnotherField"] = "another-value"
      }
    };

    // Act
    var value = scope["CustomField"];

    // Assert
    await Assert.That(value).IsEqualTo("custom-value");
  }

  [Test]
  public async Task PerspectiveScope_Indexer_Extension_WithNullValue_ReturnsNullAsync() {
    // Arrange
    var scope = new PerspectiveScope {
      Extensions = new Dictionary<string, string?> {
        ["NullableField"] = null
      }
    };

    // Act
    var value = scope["NullableField"];

    // Assert
    await Assert.That(value).IsNull();
  }

  [Test]
  public async Task PerspectiveScope_Indexer_Unknown_ReturnsNullAsync() {
    // Arrange
    var scope = new PerspectiveScope {
      TenantId = "tenant-123",
      Extensions = new Dictionary<string, string?> {
        ["CustomField"] = "custom-value"
      }
    };

    // Act
    var value = scope["UnknownField"];

    // Assert
    await Assert.That(value).IsNull();
  }

  [Test]
  public async Task PerspectiveScope_Indexer_NoExtensions_ReturnsNullForCustomFieldAsync() {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "tenant-123" };

    // Act
    var value = scope["CustomField"];

    // Assert
    await Assert.That(value).IsNull();
  }

  // === AllowedPrincipals Tests ===

  [Test]
  public async Task PerspectiveScope_AllowedPrincipals_StoresPrincipalsAsync() {
    // Arrange
    var principals = new List<SecurityPrincipalId> {
      SecurityPrincipalId.Group("sales-team"),
      SecurityPrincipalId.User("manager-456")
    };

    var scope = new PerspectiveScope {
      TenantId = "tenant-123",
      AllowedPrincipals = principals
    };

    // Assert
    await Assert.That(scope.AllowedPrincipals).IsNotNull();
    await Assert.That(scope.AllowedPrincipals!.Count).IsEqualTo(2);
    await Assert.That(scope.AllowedPrincipals).Contains(SecurityPrincipalId.Group("sales-team"));
    await Assert.That(scope.AllowedPrincipals).Contains(SecurityPrincipalId.User("manager-456"));
  }

  [Test]
  public async Task PerspectiveScope_AllowedPrincipals_WhenNull_ReturnsNullAsync() {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "tenant-123" };

    // Assert
    await Assert.That(scope.AllowedPrincipals).IsNull();
  }

  [Test]
  public async Task PerspectiveScope_AllowedPrincipals_EmptyList_ReturnsEmptyListAsync() {
    // Arrange
    var scope = new PerspectiveScope {
      TenantId = "tenant-123",
      AllowedPrincipals = new List<SecurityPrincipalId>()
    };

    // Assert
    await Assert.That(scope.AllowedPrincipals).IsNotNull();
    await Assert.That(scope.AllowedPrincipals!.Count).IsEqualTo(0);
  }

  // === Record Equality Tests ===

  [Test]
  public async Task PerspectiveScope_Equals_SameProperties_ReturnsTrueAsync() {
    // Arrange
    var scope1 = new PerspectiveScope {
      TenantId = "tenant-123",
      UserId = "user-456"
    };
    var scope2 = new PerspectiveScope {
      TenantId = "tenant-123",
      UserId = "user-456"
    };

    // Assert
    await Assert.That(scope1).IsEqualTo(scope2);
  }

  [Test]
  public async Task PerspectiveScope_Equals_DifferentProperties_ReturnsFalseAsync() {
    // Arrange
    var scope1 = new PerspectiveScope { TenantId = "tenant-123" };
    var scope2 = new PerspectiveScope { TenantId = "tenant-456" };

    // Assert
    await Assert.That(scope1).IsNotEqualTo(scope2);
  }

  // === With Expression Tests ===

  [Test]
  public async Task PerspectiveScope_With_CreatesNewInstanceWithModifiedPropertyAsync() {
    // Arrange
    var original = new PerspectiveScope {
      TenantId = "tenant-123",
      UserId = "user-456"
    };

    // Act
    var modified = original with { UserId = "user-789" };

    // Assert
    await Assert.That(modified.TenantId).IsEqualTo("tenant-123");
    await Assert.That(modified.UserId).IsEqualTo("user-789");
    await Assert.That(original.UserId).IsEqualTo("user-456"); // Original unchanged
  }
}
