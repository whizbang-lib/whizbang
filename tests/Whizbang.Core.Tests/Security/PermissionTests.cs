using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for the Permission value object.
/// </summary>
/// <tests>Permission</tests>
public class PermissionTests {
  // === Constructor Tests ===

  [Test]
  public async Task Permission_Constructor_ValidValue_CreatesInstanceAsync() {
    // Arrange & Act
    var permission = new Permission("orders:read");

    // Assert
    await Assert.That(permission.Value).IsEqualTo("orders:read");
  }

  [Test]
  public async Task Permission_Constructor_NullValue_ThrowsArgumentExceptionAsync() {
    // Arrange & Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(() => {
      _ = new Permission(null!);
      return Task.CompletedTask;
    });
  }

  [Test]
  public async Task Permission_Constructor_EmptyValue_ThrowsArgumentExceptionAsync() {
    // Arrange & Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(() => {
      _ = new Permission("");
      return Task.CompletedTask;
    });
  }

  [Test]
  public async Task Permission_Constructor_WhitespaceValue_ThrowsArgumentExceptionAsync() {
    // Arrange & Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(() => {
      _ = new Permission("   ");
      return Task.CompletedTask;
    });
  }

  // === Implicit Conversion Tests ===

  [Test]
  public async Task Permission_ImplicitToString_ReturnsValueAsync() {
    // Arrange
    var permission = new Permission("orders:read");

    // Act
    string value = permission;

    // Assert
    await Assert.That(value).IsEqualTo("orders:read");
  }

  [Test]
  public async Task Permission_ImplicitFromString_CreatesPermissionAsync() {
    // Arrange & Act
    Permission permission = "orders:write";

    // Assert
    await Assert.That(permission.Value).IsEqualTo("orders:write");
  }

  // === Factory Method Tests ===

  [Test]
  public async Task Permission_Read_CreatesCorrectPermissionAsync() {
    // Arrange & Act
    var permission = Permission.Read("orders");

    // Assert
    await Assert.That(permission.Value).IsEqualTo("orders:read");
  }

  [Test]
  public async Task Permission_Write_CreatesCorrectPermissionAsync() {
    // Arrange & Act
    var permission = Permission.Write("orders");

    // Assert
    await Assert.That(permission.Value).IsEqualTo("orders:write");
  }

  [Test]
  public async Task Permission_Delete_CreatesCorrectPermissionAsync() {
    // Arrange & Act
    var permission = Permission.Delete("orders");

    // Assert
    await Assert.That(permission.Value).IsEqualTo("orders:delete");
  }

  [Test]
  public async Task Permission_Admin_CreatesCorrectPermissionAsync() {
    // Arrange & Act
    var permission = Permission.Admin("orders");

    // Assert
    await Assert.That(permission.Value).IsEqualTo("orders:admin");
  }

  [Test]
  public async Task Permission_All_CreatesWildcardPermissionAsync() {
    // Arrange & Act
    var permission = Permission.All("orders");

    // Assert
    await Assert.That(permission.Value).IsEqualTo("orders:*");
  }

  // === Wildcard Matching Tests ===

  [Test]
  public async Task Permission_Matches_ExactMatch_ReturnsTrueAsync() {
    // Arrange
    var held = new Permission("orders:read");
    var required = new Permission("orders:read");

    // Act
    var result = held.Matches(required);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Permission_Matches_DifferentPermission_ReturnsFalseAsync() {
    // Arrange
    var held = new Permission("orders:read");
    var required = new Permission("orders:write");

    // Act
    var result = held.Matches(required);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task Permission_Matches_GlobalWildcard_ReturnsTrueAsync() {
    // Arrange
    var held = new Permission("*:*");
    var required = new Permission("orders:read");

    // Act
    var result = held.Matches(required);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Permission_Matches_ResourceWildcard_ReturnsTrueAsync() {
    // Arrange
    var held = new Permission("orders:*");
    var required = new Permission("orders:read");

    // Act
    var result = held.Matches(required);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Permission_Matches_ActionWildcard_ReturnsTrueAsync() {
    // Arrange
    var held = new Permission("*:read");
    var required = new Permission("orders:read");

    // Act
    var result = held.Matches(required);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task Permission_Matches_WildcardDoesNotMatchDifferentResource_ReturnsFalseAsync() {
    // Arrange
    var held = new Permission("orders:*");
    var required = new Permission("customers:read");

    // Act
    var result = held.Matches(required);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task Permission_Matches_WildcardDoesNotMatchDifferentAction_ReturnsFalseAsync() {
    // Arrange
    var held = new Permission("*:read");
    var required = new Permission("orders:write");

    // Act
    var result = held.Matches(required);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task Permission_Matches_MalformedPermission_ReturnsFalseAsync() {
    // Arrange
    var held = new Permission("orders");
    var required = new Permission("orders:read");

    // Act
    var result = held.Matches(required);

    // Assert
    await Assert.That(result).IsFalse();
  }

  // === ToString Tests ===

  [Test]
  public async Task Permission_ToString_ReturnsValueAsync() {
    // Arrange
    var permission = new Permission("orders:read");

    // Act
    var result = permission.ToString();

    // Assert
    await Assert.That(result).IsEqualTo("orders:read");
  }

  // === Equality Tests ===

  [Test]
  public async Task Permission_Equals_SameValue_ReturnsTrueAsync() {
    // Arrange
    var permission1 = new Permission("orders:read");
    var permission2 = new Permission("orders:read");

    // Act & Assert
    await Assert.That(permission1).IsEqualTo(permission2);
  }

  [Test]
  public async Task Permission_Equals_DifferentValue_ReturnsFalseAsync() {
    // Arrange
    var permission1 = new Permission("orders:read");
    var permission2 = new Permission("orders:write");

    // Act & Assert
    await Assert.That(permission1).IsNotEqualTo(permission2);
  }

  [Test]
  public async Task Permission_GetHashCode_SameValue_ReturnsSameHashAsync() {
    // Arrange
    var permission1 = new Permission("orders:read");
    var permission2 = new Permission("orders:read");

    // Act & Assert
    await Assert.That(permission1.GetHashCode()).IsEqualTo(permission2.GetHashCode());
  }
}
