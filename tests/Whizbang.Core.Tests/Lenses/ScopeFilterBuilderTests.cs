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
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilters.Tenant, context);

    // Assert
    await Assert.That(filterInfo.TenantId).IsEqualTo("tenant-123");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.Tenant)).IsTrue();
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.User)).IsFalse();
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
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilters.None, context);

    // Assert
    await Assert.That(filterInfo.Filters).IsEqualTo(ScopeFilters.None);
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
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilters.Tenant | ScopeFilters.User, context);

    // Assert
    await Assert.That(filterInfo.TenantId).IsEqualTo("tenant-123");
    await Assert.That(filterInfo.UserId).IsEqualTo("user-456");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.Tenant)).IsTrue();
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.User)).IsTrue();
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
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilters.User | ScopeFilters.Principal, context);

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
      ScopeFilters.Tenant | ScopeFilters.User | ScopeFilters.Principal,
      context);

    // Assert
    // Tenant is always AND'd
    await Assert.That(filterInfo.TenantId).IsEqualTo("tenant-123");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.Tenant)).IsTrue();
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
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilters.Principal, context);

    // Assert
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.Principal)).IsTrue();
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
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilters.Tenant | ScopeFilters.Organization, context);

    // Assert
    await Assert.That(filterInfo.TenantId).IsEqualTo("tenant-123");
    await Assert.That(filterInfo.OrganizationId).IsEqualTo("org-789");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.Organization)).IsTrue();
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
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilters.Tenant | ScopeFilters.Customer, context);

    // Assert
    await Assert.That(filterInfo.TenantId).IsEqualTo("tenant-123");
    await Assert.That(filterInfo.CustomerId).IsEqualTo("cust-999");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.Customer)).IsTrue();
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
    await Assert.That(() => ScopeFilterBuilder.Build(ScopeFilters.Tenant, context))
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
    await Assert.That(() => ScopeFilterBuilder.Build(ScopeFilters.User, context))
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
    await Assert.That(() => ScopeFilterBuilder.Build(ScopeFilters.Principal, context))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task ScopeFilterBuilder_MissingOrganizationId_ThrowsAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123", OrganizationId = null },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act & Assert
    await Assert.That(() => ScopeFilterBuilder.Build(ScopeFilters.Organization, context))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task ScopeFilterBuilder_MissingCustomerId_ThrowsAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-123", CustomerId = null },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act & Assert
    await Assert.That(() => ScopeFilterBuilder.Build(ScopeFilters.Customer, context))
      .Throws<InvalidOperationException>();
  }

  // === Organization Only Tests ===

  [Test]
  public async Task ScopeFilterBuilder_OrganizationOnly_BuildsOrganizationFilterAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { OrganizationId = "org-789" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilters.Organization, context);

    // Assert
    await Assert.That(filterInfo.OrganizationId).IsEqualTo("org-789");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.Organization)).IsTrue();
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.Tenant)).IsFalse();
  }

  // === Customer Only Tests ===

  [Test]
  public async Task ScopeFilterBuilder_CustomerOnly_BuildsCustomerFilterAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { CustomerId = "cust-999" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilters.Customer, context);

    // Assert
    await Assert.That(filterInfo.CustomerId).IsEqualTo("cust-999");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.Customer)).IsTrue();
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.Tenant)).IsFalse();
  }

  // === User Only Tests ===

  [Test]
  public async Task ScopeFilterBuilder_UserOnly_BuildsUserFilterAsync() {
    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { UserId = "user-456" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    var filterInfo = ScopeFilterBuilder.Build(ScopeFilters.User, context);

    // Assert
    await Assert.That(filterInfo.UserId).IsEqualTo("user-456");
    await Assert.That(filterInfo.Filters.HasFlag(ScopeFilters.User)).IsTrue();
    await Assert.That(filterInfo.UseOrLogicForUserAndPrincipal).IsFalse();
  }

  // === ScopeFilterInfo Tests ===

  [Test]
  public async Task ScopeFilterInfo_IsEmpty_TrueWhenNoFiltersAsync() {
    // Arrange
    var filterInfo = new ScopeFilterInfo { Filters = ScopeFilters.None };

    // Act & Assert
    await Assert.That(filterInfo.IsEmpty).IsTrue();
  }

  [Test]
  public async Task ScopeFilterInfo_IsEmpty_FalseWhenFiltersSetAsync() {
    // Arrange
    var filterInfo = new ScopeFilterInfo { Filters = ScopeFilters.Tenant };

    // Act & Assert
    await Assert.That(filterInfo.IsEmpty).IsFalse();
  }
}
