using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.SystemEvents.Security;

namespace Whizbang.Core.Tests.Integration;

/// <summary>
/// Integration tests for the security system.
/// Verifies that security components work together correctly.
/// </summary>
[Category("Integration")]
public class SecurityIntegrationTests {
  // === Permission System Integration ===

  [Test]
  public async Task Permission_WithRole_MatchesWildcardCorrectlyAsync() {
    // Integration test: verify Permission + Role work together

    // Arrange
    var role = new RoleBuilder("Admin")
      .HasAllPermissions("orders")
      .HasAllPermissions("customers")
      .Build();

    // Act & Assert - Role with wildcard should match specific permissions
    await Assert.That(role.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Write("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Delete("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Admin("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Read("customers"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Read("products"))).IsFalse();
  }

  [Test]
  public async Task ScopeContext_WithRolePermissions_ChecksCorrectlyAsync() {
    // Integration test: verify ScopeContext integrates with Permission matching

    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Roles = new HashSet<string> { "Admin", "Support" },
      Permissions = new HashSet<Permission> {
        Permission.All("orders"),
        Permission.Read("customers"),
        Permission.Write("tickets")
      },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("user-1"),
        SecurityPrincipalId.Group("support-team")
      },
      Claims = new Dictionary<string, string> {
        ["sub"] = "user-1",
        ["tenant"] = "tenant-1"
      }
    };

    // Act & Assert - Permission checks
    await Assert.That(context.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(context.HasPermission(Permission.Delete("orders"))).IsTrue();
    await Assert.That(context.HasPermission(Permission.Read("customers"))).IsTrue();
    await Assert.That(context.HasPermission(Permission.Delete("customers"))).IsFalse();

    // Role checks
    await Assert.That(context.HasRole("Admin")).IsTrue();
    await Assert.That(context.HasAnyRole("Admin", "Guest")).IsTrue();
    await Assert.That(context.HasAnyRole("Guest", "Viewer")).IsFalse();

    // Principal checks
    await Assert.That(context.IsMemberOfAny(SecurityPrincipalId.Group("support-team"))).IsTrue();
    await Assert.That(context.IsMemberOfAny(SecurityPrincipalId.Group("sales-team"))).IsFalse();
  }

  // === ScopeFilter Integration ===

  [Test]
  public async Task ScopeFilterBuilder_WithScopeContext_BuildsCorrectFiltersAsync() {
    // Integration test: verify ScopeFilterBuilder + ScopeContext work together

    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope {
        TenantId = "tenant-123",
        UserId = "user-456",
        OrganizationId = "org-789"
      },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("user-456"),
        SecurityPrincipalId.Group("developers")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act - Build various filter combinations
    var tenantFilter = ScopeFilterBuilder.Build(ScopeFilter.Tenant, context);
    var userFilter = ScopeFilterBuilder.Build(ScopeFilter.Tenant | ScopeFilter.User, context);
    var principalFilter = ScopeFilterBuilder.Build(ScopeFilter.Tenant | ScopeFilter.Principal, context);
    var myOrSharedFilter = ScopeFilterBuilder.Build(
      ScopeFilter.Tenant | ScopeFilter.User | ScopeFilter.Principal, context);

    // Assert
    await Assert.That(tenantFilter.TenantId).IsEqualTo("tenant-123");
    await Assert.That(tenantFilter.UserId).IsNull();

    await Assert.That(userFilter.TenantId).IsEqualTo("tenant-123");
    await Assert.That(userFilter.UserId).IsEqualTo("user-456");
    await Assert.That(userFilter.UseOrLogicForUserAndPrincipal).IsFalse();

    await Assert.That(principalFilter.TenantId).IsEqualTo("tenant-123");
    await Assert.That(principalFilter.SecurityPrincipals.Count).IsEqualTo(2);

    await Assert.That(myOrSharedFilter.UseOrLogicForUserAndPrincipal).IsTrue();
  }

  // === Security Options Configuration ===

  [Test]
  public async Task SecurityOptions_FullConfiguration_WorksCorrectlyAsync() {
    // Integration test: verify full security configuration works

    // Arrange & Act
    var options = new SecurityOptions()
      .DefineRole("Admin", b => b
        .HasAllPermissions("*"))
      .DefineRole("Manager", b => b
        .HasAllPermissions("orders")
        .HasReadPermission("reports")
        .HasWritePermission("schedules"))
      .DefineRole("User", b => b
        .HasReadPermission("orders")
        .HasReadPermission("products"))
      .ExtractPermissionsFromClaim("permissions")
      .ExtractRolesFromClaim("roles")
      .ExtractSecurityPrincipalsFromClaim("groups");

    // Assert
    await Assert.That(options.Roles.Count).IsEqualTo(3);
    await Assert.That(options.Extractors.Count).IsEqualTo(3);

    // Verify Admin has super-admin powers
    var adminRole = options.Roles["Admin"];
    await Assert.That(adminRole.HasPermission(Permission.Delete("anything"))).IsTrue();
    await Assert.That(adminRole.HasPermission(Permission.Admin("everything"))).IsTrue();

    // Verify Manager has specific permissions
    var managerRole = options.Roles["Manager"];
    await Assert.That(managerRole.HasPermission(Permission.Delete("orders"))).IsTrue();
    await Assert.That(managerRole.HasPermission(Permission.Read("reports"))).IsTrue();
    await Assert.That(managerRole.HasPermission(Permission.Delete("reports"))).IsFalse();

    // Verify User has limited permissions
    var userRole = options.Roles["User"];
    await Assert.That(userRole.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(userRole.HasPermission(Permission.Write("orders"))).IsFalse();
  }

  // === Permission Extraction ===

  [Test]
  public async Task PermissionExtractors_ExtractFromClaims_CorrectlyAsync() {
    // Integration test: verify extractors work with claims

    // Arrange
    var options = new SecurityOptions()
      .ExtractPermissionsFromClaim("permissions")
      .ExtractRolesFromClaim("roles")
      .ExtractSecurityPrincipalsFromClaim("groups");

    var claims = new Dictionary<string, string> {
      ["permissions"] = "orders:read, orders:write, customers:read",
      ["roles"] = "Admin, Support",
      ["groups"] = "group:developers, group:qa-team"
    };

    // Act
    var permissions = options.Extractors
      .SelectMany(e => e.ExtractPermissions(claims))
      .ToList();
    var roles = options.Extractors
      .SelectMany(e => e.ExtractRoles(claims))
      .ToList();
    var principals = options.Extractors
      .SelectMany(e => e.ExtractSecurityPrincipals(claims))
      .ToList();

    // Assert
    await Assert.That(permissions.Count).IsEqualTo(3);
    await Assert.That(permissions).Contains(new Permission("orders:read"));
    await Assert.That(permissions).Contains(new Permission("orders:write"));
    await Assert.That(permissions).Contains(new Permission("customers:read"));

    await Assert.That(roles.Count).IsEqualTo(2);
    await Assert.That(roles).Contains("Admin");
    await Assert.That(roles).Contains("Support");

    await Assert.That(principals.Count).IsEqualTo(2);
    await Assert.That(principals).Contains(new SecurityPrincipalId("group:developers"));
    await Assert.That(principals).Contains(new SecurityPrincipalId("group:qa-team"));
  }

  // === Security System Events ===

  [Test]
  public async Task AccessDenied_CapturesAllSecurityContext_Async() {
    // Integration test: verify AccessDenied captures complete security context

    // Arrange & Act
    var accessDenied = new AccessDenied {
      ResourceType = "Order",
      ResourceId = "order-123",
      RequiredPermission = Permission.Delete("orders"),
      CallerPermissions = new HashSet<Permission> { Permission.Read("orders") },
      CallerRoles = new HashSet<string> { "User" },
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Reason = AccessDenialReason.InsufficientPermission,
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert - All security context is captured
    await Assert.That(accessDenied.ResourceType).IsEqualTo("Order");
    await Assert.That(accessDenied.ResourceId).IsEqualTo("order-123");
    await Assert.That(accessDenied.RequiredPermission.Value).IsEqualTo("orders:delete");
    await Assert.That(accessDenied.CallerPermissions.Count).IsEqualTo(1);
    await Assert.That(accessDenied.CallerRoles).Contains("User");
    await Assert.That(accessDenied.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(accessDenied.Reason).IsEqualTo(AccessDenialReason.InsufficientPermission);
  }

  [Test]
  public async Task AccessGranted_CapturesAccessDetails_Async() {
    // Integration test: verify AccessGranted captures access details

    // Arrange & Act
    var accessGranted = new AccessGranted {
      ResourceType = "Order",
      ResourceId = "order-123",
      UsedPermission = Permission.Read("orders"),
      AccessFilter = ScopeFilter.Tenant | ScopeFilter.User,
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(accessGranted.ResourceType).IsEqualTo("Order");
    await Assert.That(accessGranted.ResourceId).IsEqualTo("order-123");
    await Assert.That(accessGranted.UsedPermission.Value).IsEqualTo("orders:read");
    await Assert.That(accessGranted.AccessFilter.HasFlag(ScopeFilter.Tenant)).IsTrue();
    await Assert.That(accessGranted.AccessFilter.HasFlag(ScopeFilter.User)).IsTrue();
    await Assert.That(accessGranted.Scope.TenantId).IsEqualTo("tenant-1");
  }

  // === Exception Integration ===

  [Test]
  public async Task AccessDeniedException_ContainsAllDetails_Async() {
    // Integration test: verify exception carries all necessary information

    // Arrange
    var permission = Permission.Admin("system");
    var resourceType = "Configuration";
    var resourceId = "global-settings";
    var reason = AccessDenialReason.PolicyRejected;

    // Act
    var exception = new AccessDeniedException(permission, resourceType, resourceId, reason);

    // Assert
    await Assert.That(exception.RequiredPermission).IsEqualTo(permission);
    await Assert.That(exception.ResourceType).IsEqualTo(resourceType);
    await Assert.That(exception.ResourceId).IsEqualTo(resourceId);
    await Assert.That(exception.Reason).IsEqualTo(reason);
    await Assert.That(exception.Message).Contains("Configuration");
    await Assert.That(exception.Message).Contains("global-settings");
    await Assert.That(exception.Message).Contains("system:admin");
  }

  // === ScopeContext Accessor Integration ===

  [Test]
  public async Task ScopeContextAccessor_PropagatesAcrossAsyncCalls_Async() {
    // Integration test: verify AsyncLocal propagation works

    // Arrange
    var accessor = new ScopeContextAccessor();
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Roles = new HashSet<string> { "Admin" },
      Permissions = new HashSet<Permission> { Permission.All("*") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    IScopeContext? capturedInner = null;
    IScopeContext? capturedParallel = null;

    // Act
    accessor.Current = context;

    // Nested async call
    await Task.Run(async () => {
      await Task.Delay(1);
      capturedInner = accessor.Current;
    });

    // Parallel async calls
    var tasks = Enumerable.Range(0, 5).Select(async _ => {
      await Task.Delay(1);
      return accessor.Current;
    });
    var results = await Task.WhenAll(tasks);
    capturedParallel = results.FirstOrDefault();

    // Assert
    await Assert.That(capturedInner).IsNotNull();
    await Assert.That(capturedInner!.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(capturedParallel).IsNotNull();
    await Assert.That(capturedParallel!.HasRole("Admin")).IsTrue();
  }

  // === PerspectiveScope Integration ===

  [Test]
  public async Task PerspectiveScope_WithAllowedPrincipals_WorksCorrectly_Async() {
    // Integration test: verify PerspectiveScope with principals

    // Arrange
    var scope = new PerspectiveScope {
      TenantId = "tenant-1",
      UserId = "user-1",
      AllowedPrincipals = [
        SecurityPrincipalId.User("user-1"),
        SecurityPrincipalId.Group("managers"),
        SecurityPrincipalId.Group("finance-team")
      ],
      Extensions = new Dictionary<string, string?> {
        ["department"] = "Engineering",
        ["costCenter"] = "CC-123"
      }
    };

    // Act & Assert - Indexer access
    await Assert.That(scope["TenantId"]).IsEqualTo("tenant-1");
    await Assert.That(scope["UserId"]).IsEqualTo("user-1");
    await Assert.That(scope["department"]).IsEqualTo("Engineering");
    await Assert.That(scope["costCenter"]).IsEqualTo("CC-123");
    await Assert.That(scope["unknown"]).IsNull();

    // Principal checks
    await Assert.That(scope.AllowedPrincipals!.Count).IsEqualTo(3);
    await Assert.That(scope.AllowedPrincipals).Contains(SecurityPrincipalId.Group("managers"));
  }

  // === IScopedLensFactory Interface ===

  [Test]
  public async Task IScopedLensFactory_HasAllRequiredMethods_Async() {
    // Integration test: verify interface has all methods for security-aware lens access

    // Assert - String-based method (legacy)
    var stringMethod = typeof(IScopedLensFactory).GetMethod("GetLens", [typeof(string)]);
    await Assert.That(stringMethod).IsNotNull();

    // ScopeFilter-based methods
    var filterMethod = typeof(IScopedLensFactory).GetMethod("GetLens", [typeof(ScopeFilter)]);
    await Assert.That(filterMethod).IsNotNull();

    var permissionMethod = typeof(IScopedLensFactory).GetMethod("GetLens", [typeof(ScopeFilter), typeof(Permission)]);
    await Assert.That(permissionMethod).IsNotNull();

    // Convenience methods
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetGlobalLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetTenantLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetUserLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetOrganizationLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetCustomerLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetPrincipalLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetMyOrSharedLens")).IsNotNull();
  }
}
