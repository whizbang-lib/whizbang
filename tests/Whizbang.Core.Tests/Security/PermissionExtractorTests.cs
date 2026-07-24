using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for ClaimPermissionExtractor, ClaimRoleExtractor, and ClaimSecurityPrincipalExtractor.
/// These internal implementations of IPermissionExtractor extract security info from claims.
/// </summary>
[Category("Security")]
public class PermissionExtractorTests {
  // ========================================
  // ClaimPermissionExtractor Tests
  // ========================================

  [Test]
  public async Task ClaimPermissionExtractor_WithMatchingClaim_ExtractsPermissionsAsync() {
    // Arrange
    var extractor = new ClaimPermissionExtractor("permissions");
    var claims = new Dictionary<string, string> {
      ["permissions"] = "orders:read,orders:write,customers:read"
    };

    // Act
    var permissions = extractor.ExtractPermissions(claims).ToList();

    // Assert
    await Assert.That(permissions.Count).IsEqualTo(3);
    await Assert.That(permissions).Contains(new Permission("orders:read"));
    await Assert.That(permissions).Contains(new Permission("orders:write"));
    await Assert.That(permissions).Contains(new Permission("customers:read"));
  }

  [Test]
  public async Task ClaimPermissionExtractor_WithMissingClaim_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimPermissionExtractor("permissions");
    var claims = new Dictionary<string, string> {
      ["other"] = "value"
    };

    // Act
    var permissions = extractor.ExtractPermissions(claims).ToList();

    // Assert
    await Assert.That(permissions.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimPermissionExtractor_WithEmptyClaim_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimPermissionExtractor("permissions");
    var claims = new Dictionary<string, string> {
      ["permissions"] = ""
    };

    // Act
    var permissions = extractor.ExtractPermissions(claims).ToList();

    // Assert
    await Assert.That(permissions.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimPermissionExtractor_WithWhitespaceClaim_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimPermissionExtractor("permissions");
    var claims = new Dictionary<string, string> {
      ["permissions"] = "   "
    };

    // Act
    var permissions = extractor.ExtractPermissions(claims).ToList();

    // Assert
    await Assert.That(permissions.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimPermissionExtractor_WithSpacesAroundValues_TrimsCorrectlyAsync() {
    // Arrange
    var extractor = new ClaimPermissionExtractor("permissions");
    var claims = new Dictionary<string, string> {
      ["permissions"] = "  orders:read , orders:write  "
    };

    // Act
    var permissions = extractor.ExtractPermissions(claims).ToList();

    // Assert
    await Assert.That(permissions.Count).IsEqualTo(2);
    await Assert.That(permissions).Contains(new Permission("orders:read"));
    await Assert.That(permissions).Contains(new Permission("orders:write"));
  }

  [Test]
  public async Task ClaimPermissionExtractor_ExtractRoles_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimPermissionExtractor("permissions");
    var claims = new Dictionary<string, string> {
      ["permissions"] = "orders:read"
    };

    // Act
    var roles = extractor.ExtractRoles(claims).ToList();

    // Assert
    await Assert.That(roles.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimPermissionExtractor_ExtractSecurityPrincipals_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimPermissionExtractor("permissions");
    var claims = new Dictionary<string, string> {
      ["permissions"] = "orders:read"
    };

    // Act
    var principals = extractor.ExtractSecurityPrincipals(claims).ToList();

    // Assert
    await Assert.That(principals.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimPermissionExtractor_WithSinglePermission_ExtractsOneAsync() {
    // Arrange
    var extractor = new ClaimPermissionExtractor("perms");
    var claims = new Dictionary<string, string> {
      ["perms"] = "admin:all"
    };

    // Act
    var permissions = extractor.ExtractPermissions(claims).ToList();

    // Assert
    await Assert.That(permissions.Count).IsEqualTo(1);
    await Assert.That(permissions[0]).IsEqualTo(new Permission("admin:all"));
  }

  // ========================================
  // ClaimRoleExtractor Tests
  // ========================================

  [Test]
  public async Task ClaimRoleExtractor_WithMatchingClaim_ExtractsRolesAsync() {
    // Arrange
    var extractor = new ClaimRoleExtractor("roles");
    var claims = new Dictionary<string, string> {
      ["roles"] = "Admin,User,Manager"
    };

    // Act
    var roles = extractor.ExtractRoles(claims).ToList();

    // Assert
    await Assert.That(roles.Count).IsEqualTo(3);
    await Assert.That(roles).Contains("Admin");
    await Assert.That(roles).Contains("User");
    await Assert.That(roles).Contains("Manager");
  }

  [Test]
  public async Task ClaimRoleExtractor_WithMissingClaim_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimRoleExtractor("roles");
    var claims = new Dictionary<string, string> {
      ["other"] = "value"
    };

    // Act
    var roles = extractor.ExtractRoles(claims).ToList();

    // Assert
    await Assert.That(roles.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimRoleExtractor_WithEmptyClaim_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimRoleExtractor("roles");
    var claims = new Dictionary<string, string> {
      ["roles"] = ""
    };

    // Act
    var roles = extractor.ExtractRoles(claims).ToList();

    // Assert
    await Assert.That(roles.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimRoleExtractor_WithWhitespaceClaim_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimRoleExtractor("roles");
    var claims = new Dictionary<string, string> {
      ["roles"] = "   "
    };

    // Act
    var roles = extractor.ExtractRoles(claims).ToList();

    // Assert
    await Assert.That(roles.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimRoleExtractor_WithSpacesAroundValues_TrimsCorrectlyAsync() {
    // Arrange
    var extractor = new ClaimRoleExtractor("roles");
    var claims = new Dictionary<string, string> {
      ["roles"] = "  Admin , User  "
    };

    // Act
    var roles = extractor.ExtractRoles(claims).ToList();

    // Assert
    await Assert.That(roles.Count).IsEqualTo(2);
    await Assert.That(roles).Contains("Admin");
    await Assert.That(roles).Contains("User");
  }

  [Test]
  public async Task ClaimRoleExtractor_ExtractPermissions_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimRoleExtractor("roles");
    var claims = new Dictionary<string, string> {
      ["roles"] = "Admin"
    };

    // Act
    var permissions = extractor.ExtractPermissions(claims).ToList();

    // Assert
    await Assert.That(permissions.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimRoleExtractor_ExtractSecurityPrincipals_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimRoleExtractor("roles");
    var claims = new Dictionary<string, string> {
      ["roles"] = "Admin"
    };

    // Act
    var principals = extractor.ExtractSecurityPrincipals(claims).ToList();

    // Assert
    await Assert.That(principals.Count).IsEqualTo(0);
  }

  // ========================================
  // ClaimSecurityPrincipalExtractor Tests
  // ========================================

  [Test]
  public async Task ClaimSecurityPrincipalExtractor_WithMatchingClaim_ExtractsPrincipalsAsync() {
    // Arrange
    var extractor = new ClaimSecurityPrincipalExtractor("groups");
    var claims = new Dictionary<string, string> {
      ["groups"] = "user:alice,group:sales-team,svc:api"
    };

    // Act
    var principals = extractor.ExtractSecurityPrincipals(claims).ToList();

    // Assert
    await Assert.That(principals.Count).IsEqualTo(3);
    await Assert.That(principals).Contains(new SecurityPrincipalId("user:alice"));
    await Assert.That(principals).Contains(new SecurityPrincipalId("group:sales-team"));
    await Assert.That(principals).Contains(new SecurityPrincipalId("svc:api"));
  }

  [Test]
  public async Task ClaimSecurityPrincipalExtractor_WithMissingClaim_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimSecurityPrincipalExtractor("groups");
    var claims = new Dictionary<string, string> {
      ["other"] = "value"
    };

    // Act
    var principals = extractor.ExtractSecurityPrincipals(claims).ToList();

    // Assert
    await Assert.That(principals.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimSecurityPrincipalExtractor_WithEmptyClaim_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimSecurityPrincipalExtractor("groups");
    var claims = new Dictionary<string, string> {
      ["groups"] = ""
    };

    // Act
    var principals = extractor.ExtractSecurityPrincipals(claims).ToList();

    // Assert
    await Assert.That(principals.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimSecurityPrincipalExtractor_WithWhitespaceClaim_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimSecurityPrincipalExtractor("groups");
    var claims = new Dictionary<string, string> {
      ["groups"] = "   "
    };

    // Act
    var principals = extractor.ExtractSecurityPrincipals(claims).ToList();

    // Assert
    await Assert.That(principals.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimSecurityPrincipalExtractor_WithSpacesAroundValues_TrimsCorrectlyAsync() {
    // Arrange
    var extractor = new ClaimSecurityPrincipalExtractor("groups");
    var claims = new Dictionary<string, string> {
      ["groups"] = "  user:alice , group:sales  "
    };

    // Act
    var principals = extractor.ExtractSecurityPrincipals(claims).ToList();

    // Assert
    await Assert.That(principals.Count).IsEqualTo(2);
    await Assert.That(principals).Contains(new SecurityPrincipalId("user:alice"));
    await Assert.That(principals).Contains(new SecurityPrincipalId("group:sales"));
  }

  [Test]
  public async Task ClaimSecurityPrincipalExtractor_ExtractPermissions_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimSecurityPrincipalExtractor("groups");
    var claims = new Dictionary<string, string> {
      ["groups"] = "user:alice"
    };

    // Act
    var permissions = extractor.ExtractPermissions(claims).ToList();

    // Assert
    await Assert.That(permissions.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimSecurityPrincipalExtractor_ExtractRoles_ReturnsEmptyAsync() {
    // Arrange
    var extractor = new ClaimSecurityPrincipalExtractor("groups");
    var claims = new Dictionary<string, string> {
      ["groups"] = "user:alice"
    };

    // Act
    var roles = extractor.ExtractRoles(claims).ToList();

    // Assert
    await Assert.That(roles.Count).IsEqualTo(0);
  }

  // ========================================
  // SecurityOptions Integration Tests
  // ========================================

  [Test]
  public async Task SecurityOptions_ExtractPermissionsFrom_AddsCustomExtractorAsync() {
    // Arrange
    var options = new SecurityOptions();
    var extractor = new ClaimPermissionExtractor("custom-perms");

    // Act
    var result = options.ExtractPermissionsFrom(extractor);

    // Assert
    await Assert.That(options.Extractors.Count).IsEqualTo(1);
    await Assert.That(ReferenceEquals(result, options)).IsTrue();
  }

  [Test]
  public async Task SecurityOptions_ExtractSecurityPrincipalsFromClaim_AddsExtractorAsync() {
    // Arrange
    var options = new SecurityOptions();

    // Act
    options.ExtractSecurityPrincipalsFromClaim("groups");

    // Assert
    await Assert.That(options.Extractors.Count).IsEqualTo(1);
  }

  [Test]
  public async Task SecurityOptions_ExtractPermissionsFromClaim_Fluent_ReturnsSameInstanceAsync() {
    // Arrange
    var options = new SecurityOptions();

    // Act
    var result = options.ExtractPermissionsFromClaim("permissions");

    // Assert
    await Assert.That(ReferenceEquals(result, options)).IsTrue();
  }

  [Test]
  public async Task SecurityOptions_ExtractRolesFromClaim_Fluent_ReturnsSameInstanceAsync() {
    // Arrange
    var options = new SecurityOptions();

    // Act
    var result = options.ExtractRolesFromClaim("roles");

    // Assert
    await Assert.That(ReferenceEquals(result, options)).IsTrue();
  }

  [Test]
  public async Task SecurityOptions_ExtractSecurityPrincipalsFromClaim_Fluent_ReturnsSameInstanceAsync() {
    // Arrange
    var options = new SecurityOptions();

    // Act
    var result = options.ExtractSecurityPrincipalsFromClaim("groups");

    // Assert
    await Assert.That(ReferenceEquals(result, options)).IsTrue();
  }

  [Test]
  public async Task SecurityOptions_ExtractorsFromClaims_ProduceCorrectResultsAsync() {
    // Arrange
    var options = new SecurityOptions()
      .ExtractPermissionsFromClaim("permissions")
      .ExtractRolesFromClaim("roles")
      .ExtractSecurityPrincipalsFromClaim("groups");

    var claims = new Dictionary<string, string> {
      ["permissions"] = "orders:read,orders:write",
      ["roles"] = "Admin,User",
      ["groups"] = "user:alice,group:sales"
    };

    // Act - Extract from all extractors
    var allPermissions = options.Extractors.SelectMany(e => e.ExtractPermissions(claims)).ToList();
    var allRoles = options.Extractors.SelectMany(e => e.ExtractRoles(claims)).ToList();
    var allPrincipals = options.Extractors.SelectMany(e => e.ExtractSecurityPrincipals(claims)).ToList();

    // Assert
    await Assert.That(allPermissions.Count).IsEqualTo(2);
    await Assert.That(allRoles.Count).IsEqualTo(2);
    await Assert.That(allPrincipals.Count).IsEqualTo(2);
  }
}
