using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for ScopeFilterBuilder expression building.
/// </summary>
/// <tests>ScopeFilterBuilder</tests>
public class ScopeFilterBuilderTests {
  // === Tenant Only Tests ===

  [Test]
  public async Task ScopeFilterBuilder_TenantOnly_BuildsTenantFilterAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilter.Tenant, context);

    // Assert
    await Assert.That(filterInfo.TenantId).IsEqualTo("tenant-123");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilter.Tenant)).IsTrue();
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilter.User)).IsFalse();
  }

  [Test]
  public async Task ScopeFilterBuilder_None_BuildsEmptyFilterAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilter.None, context);

    // Assert
    await Assert.That(filterInfo.Filters).IsEqualTo(ScopeFilter.None);
    await Assert.That(filterInfo.IsEmpty).IsTrue();
  }

  // === Tenant And User Tests ===

  [Test]
  public async Task ScopeFilterBuilder_TenantAndUser_BuildsAndFilterAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123", UserId = "user-456" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilter.Tenant | ScopeFilter.User, context);

    // Assert
    await Assert.That(filterInfo.TenantId).IsEqualTo("tenant-123");
    await Assert.That(filterInfo.UserId).IsEqualTo("user-456");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilter.Tenant)).IsTrue();
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilter.User)).IsTrue();
    await Assert.That(filterInfo.UseOrLogicForUserAndPrincipal).IsFalse();
  }

  // === User And Principal Tests (OR Logic) ===

  [Test]
  public async Task ScopeFilterBuilder_UserAndPrincipal_BuildsOrFilterAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123", UserId = "user-456" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("user-456"),
        SecurityPrincipalId.Group("sales-team")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilter.User | ScopeFilter.Principal, context);

    // Assert - User + Principal should use OR logic
    await Assert.That(filterInfo.UseOrLogicForUserAndPrincipal).IsTrue();
    await Assert.That(filterInfo.UserId).IsEqualTo("user-456");
    await Assert.That(filterInfo.SecurityPrincipals).Contains(SecurityPrincipalId.Group("sales-team"));
  }

  // === Tenant + User + Principal Tests (AND for Tenant, OR for User/Principal) ===

  [Test]
  public async Task ScopeFilterBuilder_TenantUserPrincipal_BuildsTenantAndUserOrPrincipalAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123", UserId = "user-456" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("user-456"),
        SecurityPrincipalId.Group("sales-team")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var filterInfo = ScopeFilterBuilder.Build(
      ScopeFilter.Tenant | ScopeFilter.User | ScopeFilter.Principal,
      context);

    // Assert
    // Tenant is always AND'd
    await Assert.That(filterInfo.TenantId).IsEqualTo("tenant-123");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilter.Tenant)).IsTrue();
    // User + Principal are OR'd together
    await Assert.That(filterInfo.UseOrLogicForUserAndPrincipal).IsTrue();
    await Assert.That(filterInfo.UserId).IsEqualTo("user-456");
    await Assert.That(filterInfo.SecurityPrincipals.Count).IsEqualTo(2);
  }

  // === Principal Only Tests ===

  [Test]
  public async Task ScopeFilterBuilder_PrincipalOnly_BuildsOverlapFilterAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("user-456"),
        SecurityPrincipalId.Group("sales-team"),
        SecurityPrincipalId.Group("all-employees")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilter.Principal, context);

    // Assert
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilter.Principal)).IsTrue();
    await Assert.That(filterInfo.SecurityPrincipals.Count).IsEqualTo(3);
    await Assert.That(filterInfo.SecurityPrincipals).Contains(SecurityPrincipalId.Group("sales-team"));
  }

  // === Organization Tests ===

  [Test]
  public async Task ScopeFilterBuilder_TenantAndOrganization_BuildsAndFilterAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123", OrganizationId = "org-789" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilter.Tenant | ScopeFilter.Organization, context);

    // Assert
    await Assert.That(filterInfo.TenantId).IsEqualTo("tenant-123");
    await Assert.That(filterInfo.OrganizationId).IsEqualTo("org-789");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilter.Organization)).IsTrue();
  }

  // === Customer Tests ===

  [Test]
  public async Task ScopeFilterBuilder_TenantAndCustomer_BuildsAndFilterAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123", CustomerId = "cust-999" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilter.Tenant | ScopeFilter.Customer, context);

    // Assert
    await Assert.That(filterInfo.TenantId).IsEqualTo("tenant-123");
    await Assert.That(filterInfo.CustomerId).IsEqualTo("cust-999");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilter.Customer)).IsTrue();
  }

  // === Missing Scope Values ===

  [Test]
  public async Task ScopeFilterBuilder_MissingTenantId_ThrowsAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = null },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act & Assert
    await Assert.That(() => ScopeFilterBuilder.Build(ScopeFilter.Tenant, context))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task ScopeFilterBuilder_MissingUserId_ThrowsAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123", UserId = null },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act & Assert
    await Assert.That(() => ScopeFilterBuilder.Build(ScopeFilter.User, context))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task ScopeFilterBuilder_EmptyPrincipals_ThrowsAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act & Assert
    await Assert.That(() => ScopeFilterBuilder.Build(ScopeFilter.Principal, context))
      .Throws<InvalidOperationException>();
  }
}
