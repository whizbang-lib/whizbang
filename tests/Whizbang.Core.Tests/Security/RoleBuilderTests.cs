using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for the RoleBuilder fluent builder.
/// </summary>
/// <tests>RoleBuilder</tests>
public class RoleBuilderTests {
  // === Build Tests ===

  [Test]
  public async Task RoleBuilder_Build_ReturnsRoleWithNameAsync() {
    // Arrange
    var builder = new RoleBuilder("TestRole");

    // Act
    var role = builder.Build();

    // Assert
    await Assert.That(role.Name).IsEqualTo("TestRole");
  }

  [Test]
  public async Task RoleBuilder_Build_ReturnsRoleWithPermissionsAsync() {
    // Arrange
    var builder = new RoleBuilder("TestRole")
      .HasPermission(Permission.Read("orders"))
      .HasPermission(Permission.Write("orders"));

    // Act
    var role = builder.Build();

    // Assert
    await Assert.That(role.Permissions.Count).IsEqualTo(2);
    await Assert.That(role.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Write("orders"))).IsTrue();
  }

  // === HasPermission String Overload Tests ===

  [Test]
  public async Task RoleBuilder_HasPermission_String_AddsPermissionAsync() {
    // Arrange & Act
    var role = new RoleBuilder("TestRole")
      .HasPermission("custom:action")
      .Build();

    // Assert
    await Assert.That(role.HasPermission(new Permission("custom:action"))).IsTrue();
  }

  // === Factory Method Tests ===

  [Test]
  public async Task RoleBuilder_HasReadPermission_AddsReadPermissionAsync() {
    // Arrange & Act
    var role = new RoleBuilder("Reader")
      .HasReadPermission("orders")
      .Build();

    // Assert
    await Assert.That(role.HasPermission(Permission.Read("orders"))).IsTrue();
  }

  [Test]
  public async Task RoleBuilder_HasWritePermission_AddsWritePermissionAsync() {
    // Arrange & Act
    var role = new RoleBuilder("Writer")
      .HasWritePermission("orders")
      .Build();

    // Assert
    await Assert.That(role.HasPermission(Permission.Write("orders"))).IsTrue();
  }

  [Test]
  public async Task RoleBuilder_HasDeletePermission_AddsDeletePermissionAsync() {
    // Arrange & Act
    var role = new RoleBuilder("Deleter")
      .HasDeletePermission("orders")
      .Build();

    // Assert
    await Assert.That(role.HasPermission(Permission.Delete("orders"))).IsTrue();
  }

  [Test]
  public async Task RoleBuilder_HasAdminPermission_AddsAdminPermissionAsync() {
    // Arrange & Act
    var role = new RoleBuilder("Admin")
      .HasAdminPermission("orders")
      .Build();

    // Assert
    await Assert.That(role.HasPermission(Permission.Admin("orders"))).IsTrue();
  }

  [Test]
  public async Task RoleBuilder_HasAllPermissions_AddsWildcardPermissionAsync() {
    // Arrange & Act
    var role = new RoleBuilder("FullAccess")
      .HasAllPermissions("orders")
      .Build();

    // Assert
    await Assert.That(role.HasPermission(Permission.All("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Delete("orders"))).IsTrue();
  }

  // === Chaining Tests ===

  [Test]
  public async Task RoleBuilder_Chaining_BuildsCompleteRoleAsync() {
    // Arrange & Act
    var role = new RoleBuilder("OrderManager")
      .HasReadPermission("orders")
      .HasWritePermission("orders")
      .HasDeletePermission("orders")
      .HasReadPermission("customers")
      .Build();

    // Assert
    await Assert.That(role.Name).IsEqualTo("OrderManager");
    await Assert.That(role.Permissions.Count).IsEqualTo(4);
    await Assert.That(role.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Write("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Delete("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Read("customers"))).IsTrue();
  }

  // === Duplicate Permission Tests ===

  [Test]
  public async Task RoleBuilder_DuplicatePermission_OnlyAddedOnceAsync() {
    // Arrange & Act
    var role = new RoleBuilder("TestRole")
      .HasReadPermission("orders")
      .HasReadPermission("orders")
      .HasReadPermission("orders")
      .Build();

    // Assert
    await Assert.That(role.Permissions.Count).IsEqualTo(1);
  }

  // === Empty Permissions Tests ===

  [Test]
  public async Task RoleBuilder_NoPermissions_BuildsRoleWithEmptySetAsync() {
    // Arrange & Act
    var role = new RoleBuilder("EmptyRole").Build();

    // Assert
    await Assert.That(role.Permissions.Count).IsEqualTo(0);
  }
}
