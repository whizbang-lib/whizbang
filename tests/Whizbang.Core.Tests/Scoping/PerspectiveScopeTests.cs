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

  // === GetValue Tests (replaced indexer for EF Core ComplexProperty compatibility) ===

  [Test]
  public async Task PerspectiveScope_GetValue_StandardProperty_TenantId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "tenant-123" };

    // Act
    var value = scope.GetValue("TenantId");

    // Assert
    await Assert.That(value).IsEqualTo("tenant-123");
  }

  [Test]
  public async Task PerspectiveScope_GetValue_StandardProperty_CustomerId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { CustomerId = "customer-456" };

    // Act
    var value = scope.GetValue("CustomerId");

    // Assert
    await Assert.That(value).IsEqualTo("customer-456");
  }

  [Test]
  public async Task PerspectiveScope_GetValue_StandardProperty_UserId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { UserId = "user-789" };

    // Act
    var value = scope.GetValue("UserId");

    // Assert
    await Assert.That(value).IsEqualTo("user-789");
  }

  [Test]
  public async Task PerspectiveScope_GetValue_StandardProperty_OrganizationId_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope { OrganizationId = "org-abc" };

    // Act
    var value = scope.GetValue("OrganizationId");

    // Assert
    await Assert.That(value).IsEqualTo("org-abc");
  }

  [Test]
  public async Task PerspectiveScope_GetValue_Extension_ReturnsValueAsync() {
    // Arrange
    var scope = new PerspectiveScope {
      Extensions = [
        new ScopeExtension("CustomField", "custom-value"),
        new ScopeExtension("AnotherField", "another-value")
      ]
    };

    // Act
    var value = scope.GetValue("CustomField");

    // Assert
    await Assert.That(value).IsEqualTo("custom-value");
  }

  [Test]
  public async Task PerspectiveScope_GetValue_Extension_WithNullValue_ReturnsNullAsync() {
    // Arrange
    var scope = new PerspectiveScope {
      Extensions = [new ScopeExtension("NullableField", null)]
    };

    // Act
    var value = scope.GetValue("NullableField");

    // Assert
    await Assert.That(value).IsNull();
  }

  [Test]
  public async Task PerspectiveScope_GetValue_Unknown_ReturnsNullAsync() {
    // Arrange
    var scope = new PerspectiveScope {
      TenantId = "tenant-123",
      Extensions = [new ScopeExtension("CustomField", "custom-value")]
    };

    // Act
    var value = scope.GetValue("UnknownField");

    // Assert
    await Assert.That(value).IsNull();
  }

  [Test]
  public async Task PerspectiveScope_GetValue_NoExtensions_ReturnsNullForCustomFieldAsync() {
    // Arrange
    var scope = new PerspectiveScope { TenantId = "tenant-123" };

    // Act
    var value = scope.GetValue("CustomField");

    // Assert
    await Assert.That(value).IsNull();
  }

  // === AllowedPrincipals Tests ===

  [Test]
  public async Task PerspectiveScope_AllowedPrincipals_StoresPrincipalsAsync() {
    // Arrange - AllowedPrincipals now stores string values directly
    var principals = new List<string> {
      "group:sales-team",
      "user:manager-456"
    };

    var scope = new PerspectiveScope {
      TenantId = "tenant-123",
      AllowedPrincipals = principals
    };

    // Assert
    await Assert.That(scope.AllowedPrincipals).IsNotNull();
    await Assert.That(scope.AllowedPrincipals!.Count).IsEqualTo(2);
    await Assert.That(scope.AllowedPrincipals).Contains("group:sales-team");
    await Assert.That(scope.AllowedPrincipals).Contains("user:manager-456");
  }

  [Test]
  public async Task PerspectiveScope_AllowedPrincipals_DefaultsToEmptyListAsync() {
    // Arrange - AllowedPrincipals now defaults to empty list (not null) for JSON serialization
    var scope = new PerspectiveScope { TenantId = "tenant-123" };

    // Assert
    await Assert.That(scope.AllowedPrincipals).IsNotNull();
    await Assert.That(scope.AllowedPrincipals.Count).IsEqualTo(0);
  }

  [Test]
  public async Task PerspectiveScope_AllowedPrincipals_EmptyList_ReturnsEmptyListAsync() {
    // Arrange
    var scope = new PerspectiveScope {
      TenantId = "tenant-123",
      AllowedPrincipals = []
    };

    // Assert
    await Assert.That(scope.AllowedPrincipals).IsNotNull();
    await Assert.That(scope.AllowedPrincipals!.Count).IsEqualTo(0);
  }

  // === Class Property Equality Tests ===
  // Note: PerspectiveScope is a class (not record) for EF Core compatibility.
  // Classes use reference equality by default, so we test property values directly.

  [Test]
  public async Task PerspectiveScope_SameProperties_HaveMatchingValuesAsync() {
    // Arrange
    var scope1 = new PerspectiveScope {
      TenantId = "tenant-123",
      UserId = "user-456"
    };
    var scope2 = new PerspectiveScope {
      TenantId = "tenant-123",
      UserId = "user-456"
    };

    // Assert - compare property values since class uses reference equality
    await Assert.That(scope1.TenantId).IsEqualTo(scope2.TenantId);
    await Assert.That(scope1.UserId).IsEqualTo(scope2.UserId);
  }

  [Test]
  public async Task PerspectiveScope_DifferentProperties_HaveDifferentValuesAsync() {
    // Arrange
    var scope1 = new PerspectiveScope { TenantId = "tenant-123" };
    var scope2 = new PerspectiveScope { TenantId = "tenant-456" };

    // Assert - compare property values
    await Assert.That(scope1.TenantId).IsNotEqualTo(scope2.TenantId);
  }

  // Note: `with` expression tests removed - PerspectiveScope is a class (not record)
  // for EF Core 10 ComplexProperty().ToJson() compatibility. Classes don't support `with`.
  // Properties remain init-only for construction-time assignment.
}
