using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for the ScopeContext implementation.
/// </summary>
/// <tests>ScopeContext</tests>
public class ScopeContextTests {
  // === HasPermission Tests ===

  [Test]
  public async Task ScopeContext_HasPermission_WithExactMatch_ReturnsTrueAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.HasPermission(Permission.Read("orders"));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ScopeContext_HasPermission_WithWildcard_ReturnsTrueAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> { Permission.All("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.HasPermission(Permission.Read("orders"));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ScopeContext_HasPermission_WithoutMatch_ReturnsFalseAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.HasPermission(Permission.Write("orders"));

    // Assert
    await Assert.That(result).IsFalse();
  }

  // === HasAnyPermission Tests ===

  [Test]
  public async Task ScopeContext_HasAnyPermission_WithOneMatch_ReturnsTrueAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

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
  public async Task ScopeContext_HasAnyPermission_WithNoMatch_ReturnsFalseAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.HasAnyPermission(
      Permission.Write("orders"),
      Permission.Delete("orders")
    );

    // Assert
    await Assert.That(result).IsFalse();
  }

  // === HasAllPermissions Tests ===

  [Test]
  public async Task ScopeContext_HasAllPermissions_WithAllMatching_ReturnsTrueAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> {
        Permission.Read("orders"),
        Permission.Write("orders")
      },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.HasAllPermissions(
      Permission.Read("orders"),
      Permission.Write("orders")
    );

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ScopeContext_HasAllPermissions_WithMissing_ReturnsFalseAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.HasAllPermissions(
      Permission.Read("orders"),
      Permission.Write("orders")
    );

    // Assert
    await Assert.That(result).IsFalse();
  }

  // === HasRole Tests ===

  [Test]
  public async Task ScopeContext_HasRole_WithMatch_ReturnsTrueAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string> { "Admin", "User" },
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.HasRole("Admin");

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ScopeContext_HasRole_WithoutMatch_ReturnsFalseAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string> { "User" },
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.HasRole("Admin");

    // Assert
    await Assert.That(result).IsFalse();
  }

  // === HasAnyRole Tests ===

  [Test]
  public async Task ScopeContext_HasAnyRole_WithOneMatch_ReturnsTrueAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string> { "User" },
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.HasAnyRole("Admin", "User", "Guest");

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ScopeContext_HasAnyRole_WithNoMatch_ReturnsFalseAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string> { "Guest" },
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.HasAnyRole("Admin", "User");

    // Assert
    await Assert.That(result).IsFalse();
  }

  // === IsMemberOfAny Tests ===

  [Test]
  public async Task ScopeContext_IsMemberOfAny_WithMatchingPrincipal_ReturnsTrueAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("alice"),
        SecurityPrincipalId.Group("sales-team")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.IsMemberOfAny(
      SecurityPrincipalId.Group("admin-team"),
      SecurityPrincipalId.Group("sales-team")
    );

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ScopeContext_IsMemberOfAny_WithNoMatch_ReturnsFalseAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("alice")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.IsMemberOfAny(
      SecurityPrincipalId.Group("admin-team"),
      SecurityPrincipalId.Group("sales-team")
    );

    // Assert
    await Assert.That(result).IsFalse();
  }

  // === IsMemberOfAll Tests ===

  [Test]
  public async Task ScopeContext_IsMemberOfAll_WithAllMatching_ReturnsTrueAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("alice"),
        SecurityPrincipalId.Group("sales-team"),
        SecurityPrincipalId.Group("all-employees")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.IsMemberOfAll(
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("sales-team")
    );

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ScopeContext_IsMemberOfAll_WithMissingPrincipal_ReturnsFalseAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("alice")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var result = context.IsMemberOfAll(
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("sales-team")
    );

    // Assert
    await Assert.That(result).IsFalse();
  }

  // === Empty Context Tests ===

  [Test]
  public async Task ScopeContext_Empty_HasNoPermissionsAsync() {
    // Act
    var empty = ScopeContext.Empty;

    // Assert
    await Assert.That(empty.HasPermission(Permission.Read("orders"))).IsFalse();
    await Assert.That(empty.Permissions.Count).IsEqualTo(0);
    await Assert.That(empty.Roles.Count).IsEqualTo(0);
    await Assert.That(empty.SecurityPrincipals.Count).IsEqualTo(0);
    await Assert.That(empty.Claims.Count).IsEqualTo(0);
  }
}
