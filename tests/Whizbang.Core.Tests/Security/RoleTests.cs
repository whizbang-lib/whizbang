using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for the Role record.
/// </summary>
/// <tests>Role</tests>
public class RoleTests {
  // === Property Tests ===

  [Test]
  public async Task Role_Name_ReturnsConfiguredNameAsync() {
    // Arrange
    var permissions = new HashSet<Permission> { Permission.Read("orders") };
    var role = new Role {
      Name = "OrderReader",
      Permissions = permissions
    };

    // Assert
    await Assert.That(role.Name).IsEqualTo("OrderReader");
  }

  [Test]
  public async Task Role_Permissions_ReturnsConfiguredPermissionsAsync() {
    // Arrange
    var permissions = new HashSet<Permission> {
      Permission.Read("orders"),
      Permission.Write("orders")
    };
    var role = new Role {
      Name = "OrderManager",
      Permissions = permissions
    };

    // Assert
    await Assert.That(role.Permissions).Contains(Permission.Read("orders"));
    await Assert.That(role.Permissions).Contains(Permission.Write("orders"));
    await Assert.That(role.Permissions.Count).IsEqualTo(2);
  }

  // === HasPermission Tests ===

  [Test]
  public async Task Role_HasPermission_WithMatchingPermission_ReturnsTrueAsync() {
    // Arrange
    var role = new Role {
      Name = "OrderReader",
      Permissions = new HashSet<Permission> { Permission.Read("orders") }
    };

    // Act
    var result = role.HasPermission(Permission.Read("orders"));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Role_HasPermission_WithNonMatchingPermission_ReturnsFalseAsync() {
    // Arrange
    var role = new Role {
      Name = "OrderReader",
      Permissions = new HashSet<Permission> { Permission.Read("orders") }
    };

    // Act
    var result = role.HasPermission(Permission.Write("orders"));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task Role_HasPermission_WithWildcardPermission_ReturnsTrueAsync() {
    // Arrange - role has orders:* wildcard
    var role = new Role {
      Name = "OrderAdmin",
      Permissions = new HashSet<Permission> { Permission.All("orders") }
    };

    // Act - check if it matches specific permission
    var result = role.HasPermission(Permission.Read("orders"));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Role_HasPermission_WithGlobalWildcard_MatchesAnyPermissionAsync() {
    // Arrange - role has global admin
    var role = new Role {
      Name = "SuperAdmin",
      Permissions = new HashSet<Permission> { new("*:*") }
    };

    // Act & Assert
    await Assert.That(role.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Delete("users"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Admin("system"))).IsTrue();
  }

  [Test]
  public async Task Role_HasPermission_EmptyPermissions_ReturnsFalseAsync() {
    // Arrange
    var role = new Role {
      Name = "NoAccess",
      Permissions = new HashSet<Permission>()
    };

    // Act
    var result = role.HasPermission(Permission.Read("orders"));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task Role_HasPermission_MultiplePermissions_FindsMatchAsync() {
    // Arrange
    var role = new Role {
      Name = "Support",
      Permissions = new HashSet<Permission> {
        Permission.Read("orders"),
        Permission.Read("customers"),
        Permission.Write("tickets")
      }
    };

    // Act & Assert
    await Assert.That(role.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Read("customers"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Write("tickets"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Delete("orders"))).IsFalse();
  }

  // === Equality Tests ===

  [Test]
  public async Task Role_Equals_SameNameAndPermissions_ReturnsTrueAsync() {
    // Arrange
    var permissions = new HashSet<Permission> { Permission.Read("orders") };
    var role1 = new Role { Name = "Reader", Permissions = permissions };
    var role2 = new Role { Name = "Reader", Permissions = permissions };

    // Assert
    await Assert.That(role1).IsEqualTo(role2);
  }

  [Test]
  public async Task Role_Equals_DifferentName_ReturnsFalseAsync() {
    // Arrange
    var permissions = new HashSet<Permission> { Permission.Read("orders") };
    var role1 = new Role { Name = "Reader", Permissions = permissions };
    var role2 = new Role { Name = "Viewer", Permissions = permissions };

    // Assert
    await Assert.That(role1).IsNotEqualTo(role2);
  }
}
