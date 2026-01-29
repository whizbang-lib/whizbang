using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for SecurityOptions configuration.
/// </summary>
/// <tests>SecurityOptions</tests>
public class SecurityOptionsTests {
  // === DefineRole Tests ===

  [Test]
  public async Task SecurityOptions_DefineRole_AddsRoleAsync() {
    // Arrange
    var options = new SecurityOptions();

    // Act
    options.DefineRole("Admin", builder => builder
      .HasReadPermission("orders")
      .HasWritePermission("orders"));

    // Assert
    await Assert.That(options.Roles.ContainsKey("Admin")).IsTrue();
    await Assert.That(options.Roles["Admin"].HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(options.Roles["Admin"].HasPermission(Permission.Write("orders"))).IsTrue();
  }

  [Test]
  public async Task SecurityOptions_DefineRole_Fluent_ReturnsSameInstanceAsync() {
    // Arrange
    var options = new SecurityOptions();

    // Act
    var result = options.DefineRole("Admin", _ => { });

    // Assert
    await Assert.That(ReferenceEquals(result, options)).IsTrue();
  }

  [Test]
  public async Task SecurityOptions_DefineMultipleRoles_AllAddedAsync() {
    // Arrange
    var options = new SecurityOptions();

    // Act
    options
      .DefineRole("Admin", b => b.HasAllPermissions("*"))
      .DefineRole("Reader", b => b.HasReadPermission("orders"))
      .DefineRole("Writer", b => b.HasWritePermission("orders"));

    // Assert
    await Assert.That(options.Roles.Count).IsEqualTo(3);
    await Assert.That(options.Roles.ContainsKey("Admin")).IsTrue();
    await Assert.That(options.Roles.ContainsKey("Reader")).IsTrue();
    await Assert.That(options.Roles.ContainsKey("Writer")).IsTrue();
  }

  // === Permission Extractor Tests ===

  [Test]
  public async Task SecurityOptions_ExtractPermissionsFromClaim_AddsExtractorAsync() {
    // Arrange
    var options = new SecurityOptions();

    // Act
    options.ExtractPermissionsFromClaim("permissions");

    // Assert
    await Assert.That(options.Extractors.Count).IsEqualTo(1);
  }

  [Test]
  public async Task SecurityOptions_ExtractRolesFromClaim_AddsExtractorAsync() {
    // Arrange
    var options = new SecurityOptions();

    // Act
    options.ExtractRolesFromClaim("roles");

    // Assert
    await Assert.That(options.Extractors.Count).IsEqualTo(1);
  }

  [Test]
  public async Task SecurityOptions_ExtractMultipleClaims_AllExtractorsAddedAsync() {
    // Arrange
    var options = new SecurityOptions();

    // Act
    options
      .ExtractPermissionsFromClaim("permissions")
      .ExtractRolesFromClaim("roles")
      .ExtractSecurityPrincipalsFromClaim("groups");

    // Assert
    await Assert.That(options.Extractors.Count).IsEqualTo(3);
  }

  // === Combined Configuration Tests ===

  [Test]
  public async Task SecurityOptions_FullConfiguration_WorksCorrectlyAsync() {
    // Arrange & Act
    var options = new SecurityOptions()
      .DefineRole("Admin", b => b
        .HasAllPermissions("orders")
        .HasAllPermissions("customers"))
      .DefineRole("Support", b => b
        .HasReadPermission("orders")
        .HasReadPermission("customers")
        .HasWritePermission("tickets"))
      .ExtractPermissionsFromClaim("permissions")
      .ExtractRolesFromClaim("roles");

    // Assert
    await Assert.That(options.Roles.Count).IsEqualTo(2);
    await Assert.That(options.Extractors.Count).IsEqualTo(2);

    var adminRole = options.Roles["Admin"];
    await Assert.That(adminRole.HasPermission(Permission.Delete("orders"))).IsTrue();
    await Assert.That(adminRole.HasPermission(Permission.Admin("customers"))).IsTrue();

    var supportRole = options.Roles["Support"];
    await Assert.That(supportRole.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(supportRole.HasPermission(Permission.Delete("orders"))).IsFalse();
  }
}
