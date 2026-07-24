using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for ImmutableScopeContext - the immutable wrapper for security context.
/// </summary>
/// <docs>core-concepts/message-security#immutable-context</docs>
public class ImmutableScopeContextTests {
  // ========================================
  // Constructor Tests
  // ========================================

  [Test]
  public async Task Constructor_WithValidExtraction_CreatesContextAsync() {
    // Arrange
    var extraction = _createExtraction();

    // Act
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Assert
    await Assert.That(context).IsNotNull();
    await Assert.That(context.Source).IsEqualTo("TestSource");
    await Assert.That(context.ShouldPropagate).IsTrue();
    await Assert.That(context.EstablishedAt).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
  }

  [Test]
  public async Task Constructor_WithNullExtraction_ThrowsAsync() {
    // Act & Assert
    await Assert.That(() => new ImmutableScopeContext(null!, shouldPropagate: true))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_ShouldPropagateFalse_SetsPropertyAsync() {
    // Arrange
    var extraction = _createExtraction();

    // Act
    var context = new ImmutableScopeContext(extraction, shouldPropagate: false);

    // Assert
    await Assert.That(context.ShouldPropagate).IsFalse();
  }

  // ========================================
  // Property Delegation Tests
  // ========================================

  [Test]
  public async Task Scope_DelegatesToExtractionAsync() {
    // Arrange
    var extraction = _createExtraction(tenantId: "tenant-123", userId: "user-456");
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act & Assert
    await Assert.That(context.Scope.TenantId).IsEqualTo("tenant-123");
    await Assert.That(context.Scope.UserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task Roles_DelegatesToExtractionAsync() {
    // Arrange
    var extraction = _createExtraction(roles: ["admin", "user"]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act & Assert
    await Assert.That(context.Roles.Count).IsEqualTo(2);
    await Assert.That(context.Roles.Contains("admin")).IsTrue();
    await Assert.That(context.Roles.Contains("user")).IsTrue();
  }

  [Test]
  public async Task Permissions_DelegatesToExtractionAsync() {
    // Arrange
    var readPermission = Permission.Read("orders");
    var writePermission = Permission.Write("orders");
    var extraction = _createExtraction(permissions: [readPermission, writePermission]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act & Assert
    await Assert.That(context.Permissions.Count).IsEqualTo(2);
    await Assert.That(context.Permissions.Contains(readPermission)).IsTrue();
    await Assert.That(context.Permissions.Contains(writePermission)).IsTrue();
  }

  [Test]
  public async Task SecurityPrincipals_DelegatesToExtractionAsync() {
    // Arrange
    var principal1 = SecurityPrincipalId.Group("group-1");
    var principal2 = SecurityPrincipalId.Service("service-1");
    var extraction = _createExtraction(principals: [principal1, principal2]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act & Assert
    await Assert.That(context.SecurityPrincipals.Count).IsEqualTo(2);
    await Assert.That(context.SecurityPrincipals.Contains(principal1)).IsTrue();
    await Assert.That(context.SecurityPrincipals.Contains(principal2)).IsTrue();
  }

  [Test]
  public async Task Claims_DelegatesToExtractionAsync() {
    // Arrange
    var claims = new Dictionary<string, string> {
      ["email"] = "test@example.com",
      ["name"] = "Test User"
    };
    var extraction = _createExtraction(claims: claims);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act & Assert
    await Assert.That(context.Claims.Count).IsEqualTo(2);
    await Assert.That(context.Claims["email"]).IsEqualTo("test@example.com");
    await Assert.That(context.Claims["name"]).IsEqualTo("Test User");
  }

  // ========================================
  // HasPermission Tests
  // ========================================

  [Test]
  public async Task HasPermission_WithMatchingPermission_ReturnsTrueAsync() {
    // Arrange
    var readPermission = Permission.Read("orders");
    var extraction = _createExtraction(permissions: [readPermission]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasPermission(Permission.Read("orders"));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task HasPermission_WithNoMatchingPermission_ReturnsFalseAsync() {
    // Arrange
    var readPermission = Permission.Read("orders");
    var extraction = _createExtraction(permissions: [readPermission]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasPermission(Permission.Write("orders"));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task HasPermission_WithWildcardPermission_MatchesAsync() {
    // Arrange
    var wildcardPermission = Permission.All("orders");
    var extraction = _createExtraction(permissions: [wildcardPermission]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasPermission(Permission.Read("orders"));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task HasPermission_WithEmptyPermissions_ReturnsFalseAsync() {
    // Arrange
    var extraction = _createExtraction(permissions: []);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasPermission(Permission.Read("orders"));

    // Assert
    await Assert.That(result).IsFalse();
  }

  // ========================================
  // HasAnyPermission Tests
  // ========================================

  [Test]
  public async Task HasAnyPermission_WithOneMatching_ReturnsTrueAsync() {
    // Arrange
    var readPermission = Permission.Read("orders");
    var extraction = _createExtraction(permissions: [readPermission]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasAnyPermission(
      Permission.Write("orders"),
      Permission.Read("orders"),
      Permission.Delete("orders")
    );

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task HasAnyPermission_WithNoneMatching_ReturnsFalseAsync() {
    // Arrange
    var readPermission = Permission.Read("orders");
    var extraction = _createExtraction(permissions: [readPermission]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasAnyPermission(
      Permission.Write("orders"),
      Permission.Delete("orders")
    );

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task HasAnyPermission_WithEmptyArray_ReturnsFalseAsync() {
    // Arrange
    var readPermission = Permission.Read("orders");
    var extraction = _createExtraction(permissions: [readPermission]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasAnyPermission();

    // Assert
    await Assert.That(result).IsFalse();
  }

  // ========================================
  // HasAllPermissions Tests
  // ========================================

  [Test]
  public async Task HasAllPermissions_WithAllMatching_ReturnsTrueAsync() {
    // Arrange
    var readPermission = Permission.Read("orders");
    var writePermission = Permission.Write("orders");
    var extraction = _createExtraction(permissions: [readPermission, writePermission]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasAllPermissions(
      Permission.Read("orders"),
      Permission.Write("orders")
    );

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task HasAllPermissions_WithOneMissing_ReturnsFalseAsync() {
    // Arrange
    var readPermission = Permission.Read("orders");
    var extraction = _createExtraction(permissions: [readPermission]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasAllPermissions(
      Permission.Read("orders"),
      Permission.Write("orders")
    );

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task HasAllPermissions_WithEmptyArray_ReturnsTrueAsync() {
    // Arrange
    var extraction = _createExtraction(permissions: []);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasAllPermissions();

    // Assert
    await Assert.That(result).IsTrue();
  }

  // ========================================
  // HasRole Tests
  // ========================================

  [Test]
  public async Task HasRole_WithMatchingRole_ReturnsTrueAsync() {
    // Arrange
    var extraction = _createExtraction(roles: ["admin", "user"]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasRole("admin");

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task HasRole_WithNoMatchingRole_ReturnsFalseAsync() {
    // Arrange
    var extraction = _createExtraction(roles: ["user"]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasRole("admin");

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task HasRole_WithEmptyRoles_ReturnsFalseAsync() {
    // Arrange
    var extraction = _createExtraction(roles: []);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasRole("admin");

    // Assert
    await Assert.That(result).IsFalse();
  }

  // ========================================
  // HasAnyRole Tests
  // ========================================

  [Test]
  public async Task HasAnyRole_WithOneMatching_ReturnsTrueAsync() {
    // Arrange
    var extraction = _createExtraction(roles: ["user"]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasAnyRole("admin", "user", "guest");

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task HasAnyRole_WithNoneMatching_ReturnsFalseAsync() {
    // Arrange
    var extraction = _createExtraction(roles: ["user"]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasAnyRole("admin", "superadmin");

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task HasAnyRole_WithEmptyArray_ReturnsFalseAsync() {
    // Arrange
    var extraction = _createExtraction(roles: ["user"]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.HasAnyRole();

    // Assert
    await Assert.That(result).IsFalse();
  }

  // ========================================
  // IsMemberOfAny Tests
  // ========================================

  [Test]
  public async Task IsMemberOfAny_WithOneMatching_ReturnsTrueAsync() {
    // Arrange
    var principal1 = SecurityPrincipalId.Group("group-1");
    var principal2 = SecurityPrincipalId.Group("group-2");
    var extraction = _createExtraction(principals: [principal1]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.IsMemberOfAny(principal1, principal2);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsMemberOfAny_WithNoneMatching_ReturnsFalseAsync() {
    // Arrange
    var principal1 = SecurityPrincipalId.Group("group-1");
    var principal2 = SecurityPrincipalId.Group("group-2");
    var principal3 = SecurityPrincipalId.Group("group-3");
    var extraction = _createExtraction(principals: [principal3]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.IsMemberOfAny(principal1, principal2);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsMemberOfAny_WithEmptyArray_ReturnsFalseAsync() {
    // Arrange
    var principal1 = SecurityPrincipalId.Group("group-1");
    var extraction = _createExtraction(principals: [principal1]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.IsMemberOfAny();

    // Assert
    await Assert.That(result).IsFalse();
  }

  // ========================================
  // IsMemberOfAll Tests
  // ========================================

  [Test]
  public async Task IsMemberOfAll_WithAllMatching_ReturnsTrueAsync() {
    // Arrange
    var principal1 = SecurityPrincipalId.Group("group-1");
    var principal2 = SecurityPrincipalId.Group("group-2");
    var extraction = _createExtraction(principals: [principal1, principal2]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.IsMemberOfAll(principal1, principal2);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsMemberOfAll_WithOneMissing_ReturnsFalseAsync() {
    // Arrange
    var principal1 = SecurityPrincipalId.Group("group-1");
    var principal2 = SecurityPrincipalId.Group("group-2");
    var extraction = _createExtraction(principals: [principal1]);
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.IsMemberOfAll(principal1, principal2);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsMemberOfAll_WithEmptyArray_ReturnsTrueAsync() {
    // Arrange
    var extraction = _createExtraction();
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var result = context.IsMemberOfAll();

    // Assert
    await Assert.That(result).IsTrue();
  }

  // ========================================
  // EstablishedAt Tests
  // ========================================

  [Test]
  public async Task EstablishedAt_IsSetToCurrentTimeAsync() {
    // Arrange
    var before = DateTimeOffset.UtcNow;
    var extraction = _createExtraction();

    // Act
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);
    var after = DateTimeOffset.UtcNow;

    // Assert
    await Assert.That(context.EstablishedAt).IsGreaterThanOrEqualTo(before);
    await Assert.That(context.EstablishedAt).IsLessThanOrEqualTo(after);
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static SecurityExtraction _createExtraction(
    string? tenantId = null,
    string? userId = null,
    IEnumerable<string>? roles = null,
    IEnumerable<Permission>? permissions = null,
    IEnumerable<SecurityPrincipalId>? principals = null,
    Dictionary<string, string>? claims = null) {
    return new SecurityExtraction {
      Scope = new PerspectiveScope {
        TenantId = tenantId ?? "test-tenant",
        UserId = userId ?? "test-user"
      },
      Roles = roles?.ToHashSet() ?? [],
      Permissions = permissions?.ToHashSet() ?? [],
      SecurityPrincipals = principals?.ToHashSet() ?? [],
      Claims = claims ?? [],
      Source = "TestSource"
    };
  }
}
