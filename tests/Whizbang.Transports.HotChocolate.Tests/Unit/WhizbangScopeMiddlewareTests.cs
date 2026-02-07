using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Transports.HotChocolate.Middleware;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="WhizbangScopeMiddleware"/> and <see cref="RequestScopeContext"/>.
/// Verifies scope extraction from HTTP claims and headers.
/// </summary>
/// <tests>src/Whizbang.Transports.HotChocolate/Middleware/WhizbangScopeMiddleware.cs</tests>
public class WhizbangScopeMiddlewareTests {
  #region InvokeAsync - Basic Behavior

  [Test]
  public async Task InvokeAsync_ShouldSetScopeContextOnAccessorAsync() {
    // Arrange
    var accessor = new TestScopeContextAccessor();
    var middleware = new WhizbangScopeMiddleware(_ => Task.CompletedTask);
    var context = new DefaultHttpContext();

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current).IsNotNull();
  }

  [Test]
  public async Task InvokeAsync_ShouldCallNextMiddlewareAsync() {
    // Arrange
    var nextCalled = false;
    var middleware = new WhizbangScopeMiddleware(_ => {
      nextCalled = true;
      return Task.CompletedTask;
    });
    var context = new DefaultHttpContext();
    var accessor = new TestScopeContextAccessor();

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(nextCalled).IsTrue();
  }

  [Test]
  public async Task InvokeAsync_WithNullOptions_ShouldUseDefaultsAsync() {
    // Arrange
    var accessor = new TestScopeContextAccessor();
    var middleware = new WhizbangScopeMiddleware(_ => Task.CompletedTask, null);
    var context = new DefaultHttpContext();
    context.Request.Headers["X-Tenant-Id"] = "default-tenant";

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.TenantId).IsEqualTo("default-tenant");
  }

  #endregion

  #region Scope Extraction - Claims

  [Test]
  public async Task InvokeAsync_WithTenantIdClaim_ShouldExtractTenantIdAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(("tenant_id", "tenant-123"));

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.TenantId).IsEqualTo("tenant-123");
  }

  [Test]
  public async Task InvokeAsync_WithUserIdClaim_ShouldExtractUserIdAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims((ClaimTypes.NameIdentifier, "user-456"));

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.UserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task InvokeAsync_WithOrganizationIdClaim_ShouldExtractOrgIdAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(("org_id", "org-789"));

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.OrganizationId).IsEqualTo("org-789");
  }

  [Test]
  public async Task InvokeAsync_WithCustomerIdClaim_ShouldExtractCustomerIdAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(("customer_id", "cust-321"));

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.CustomerId).IsEqualTo("cust-321");
  }

  [Test]
  public async Task InvokeAsync_WithAllScopeClaims_ShouldExtractAllAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(
      ("tenant_id", "t1"),
      (ClaimTypes.NameIdentifier, "u1"),
      ("org_id", "o1"),
      ("customer_id", "c1")
    );

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    var scope = accessor.Current!.Scope;
    await Assert.That(scope.TenantId).IsEqualTo("t1");
    await Assert.That(scope.UserId).IsEqualTo("u1");
    await Assert.That(scope.OrganizationId).IsEqualTo("o1");
    await Assert.That(scope.CustomerId).IsEqualTo("c1");
  }

  #endregion

  #region Scope Extraction - Headers

  [Test]
  public async Task InvokeAsync_WithTenantIdHeader_ShouldExtractTenantIdAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = new DefaultHttpContext();
    context.Request.Headers["X-Tenant-Id"] = "header-tenant";

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.TenantId).IsEqualTo("header-tenant");
  }

  [Test]
  public async Task InvokeAsync_WithUserIdHeader_ShouldExtractUserIdAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = new DefaultHttpContext();
    context.Request.Headers["X-User-Id"] = "header-user";

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.UserId).IsEqualTo("header-user");
  }

  [Test]
  public async Task InvokeAsync_WithOrganizationIdHeader_ShouldExtractOrgIdAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = new DefaultHttpContext();
    context.Request.Headers["X-Organization-Id"] = "header-org";

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.OrganizationId).IsEqualTo("header-org");
  }

  [Test]
  public async Task InvokeAsync_WithCustomerIdHeader_ShouldExtractCustomerIdAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = new DefaultHttpContext();
    context.Request.Headers["X-Customer-Id"] = "header-customer";

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.CustomerId).IsEqualTo("header-customer");
  }

  [Test]
  public async Task InvokeAsync_ClaimsHavePriorityOverHeaders_ForSameFieldAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(("tenant_id", "claim-tenant"));
    context.Request.Headers["X-Tenant-Id"] = "header-tenant";

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert - claim should take priority
    await Assert.That(accessor.Current!.Scope.TenantId).IsEqualTo("claim-tenant");
  }

  #endregion

  #region Scope Extraction - No Values

  [Test]
  public async Task InvokeAsync_WithNoClaims_ScopeFieldsShouldBeNullAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = new DefaultHttpContext();

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    var scope = accessor.Current!.Scope;
    await Assert.That(scope.TenantId).IsNull();
    await Assert.That(scope.UserId).IsNull();
    await Assert.That(scope.OrganizationId).IsNull();
    await Assert.That(scope.CustomerId).IsNull();
    await Assert.That(scope.Extensions).IsEmpty();
  }

  [Test]
  public async Task InvokeAsync_WithNullUser_ScopeFieldsShouldBeNullAsync() {
    // Arrange - explicitly set User to null to exercise the ?. branches
    var (middleware, accessor) = _createMiddleware();
    var context = new DefaultHttpContext();
    context.User = null!;

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    var scope = accessor.Current!.Scope;
    await Assert.That(scope.TenantId).IsNull();
    await Assert.That(scope.UserId).IsNull();
    await Assert.That(scope.OrganizationId).IsNull();
    await Assert.That(scope.CustomerId).IsNull();
    await Assert.That(scope.Extensions).IsEmpty();
    await Assert.That(accessor.Current!.Roles.Count).IsEqualTo(0);
    await Assert.That(accessor.Current!.Permissions.Count).IsEqualTo(0);
    await Assert.That(accessor.Current!.SecurityPrincipals.Count).IsEqualTo(0);
    await Assert.That(accessor.Current!.Claims.Count).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeAsync_WithNullUser_AndExtensionMappings_ShouldHandleGracefullyAsync() {
    // Arrange - null User with extension mappings configured
    var options = new WhizbangScopeOptions();
    options.ExtensionClaimMappings["region_claim"] = "Region";
    var (middleware, accessor) = _createMiddleware(options);
    var context = new DefaultHttpContext();
    context.User = null!;

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.Extensions).IsEmpty();
  }

  #endregion

  #region Extension Mappings

  [Test]
  public async Task InvokeAsync_WithExtensionClaimMappings_ShouldExtractExtensionsAsync() {
    // Arrange
    var options = new WhizbangScopeOptions();
    options.ExtensionClaimMappings["region_claim"] = "Region";
    var (middleware, accessor) = _createMiddleware(options);
    var context = _createContextWithClaims(("region_claim", "us-east"));

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.Extensions).IsNotEmpty();
    await Assert.That(accessor.Current!.Scope.Extensions.First(e => e.Key == "Region").Value).IsEqualTo("us-east");
  }

  [Test]
  public async Task InvokeAsync_WithExtensionHeaderMappings_ShouldExtractExtensionsAsync() {
    // Arrange
    var options = new WhizbangScopeOptions();
    options.ExtensionHeaderMappings["X-Region"] = "Region";
    var (middleware, accessor) = _createMiddleware(options);
    var context = new DefaultHttpContext();
    context.Request.Headers["X-Region"] = "eu-west";

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.Extensions).IsNotEmpty();
    await Assert.That(accessor.Current!.Scope.Extensions.First(e => e.Key == "Region").Value).IsEqualTo("eu-west");
  }

  [Test]
  public async Task InvokeAsync_WithEmptyExtensionClaimValue_ShouldSkipAsync() {
    // Arrange
    var options = new WhizbangScopeOptions();
    options.ExtensionClaimMappings["empty_claim"] = "Empty";
    var (middleware, accessor) = _createMiddleware(options);
    var context = _createContextWithClaims(("empty_claim", ""));

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.Extensions).IsEmpty();
  }

  [Test]
  public async Task InvokeAsync_WithExtensionClaimMapping_WhenClaimNotPresent_ShouldSkipAsync() {
    // Arrange - mapping configured but claim doesn't exist in user's claims
    // Exercises the FindFirst returning null branch of context.User?.FindFirst(claimType)?.Value
    var options = new WhizbangScopeOptions();
    options.ExtensionClaimMappings["nonexistent_claim"] = "Region";
    var (middleware, accessor) = _createMiddleware(options);
    var context = _createContextWithClaims(("other_claim", "some_value"));

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.Extensions).IsEmpty();
  }

  [Test]
  public async Task InvokeAsync_WithEmptyExtensionHeaderValue_ShouldSkipAsync() {
    // Arrange
    var options = new WhizbangScopeOptions();
    options.ExtensionHeaderMappings["X-Empty"] = "Empty";
    var (middleware, accessor) = _createMiddleware(options);
    var context = new DefaultHttpContext();
    context.Request.Headers["X-Empty"] = "";

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.Extensions).IsEmpty();
  }

  #endregion

  #region Roles Extraction

  [Test]
  public async Task InvokeAsync_WithRoleClaims_ShouldExtractRolesAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(
      (ClaimTypes.Role, "Admin"),
      (ClaimTypes.Role, "Manager")
    );

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Roles).Contains("Admin");
    await Assert.That(accessor.Current!.Roles).Contains("Manager");
  }

  [Test]
  public async Task InvokeAsync_WithEmptyRoleClaim_ShouldSkipEmptyAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(
      (ClaimTypes.Role, "Admin"),
      (ClaimTypes.Role, "")
    );

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Roles.Count).IsEqualTo(1);
    await Assert.That(accessor.Current!.Roles).Contains("Admin");
  }

  [Test]
  public async Task InvokeAsync_WithNoRoles_ShouldReturnEmptySetAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = new DefaultHttpContext();

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Roles.Count).IsEqualTo(0);
  }

  #endregion

  #region Permissions Extraction

  [Test]
  public async Task InvokeAsync_WithPermissionClaims_ShouldExtractPermissionsAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(
      ("permissions", "orders:read"),
      ("permissions", "orders:write")
    );

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Permissions.Count).IsEqualTo(2);
  }

  [Test]
  public async Task InvokeAsync_WithEmptyPermissionClaim_ShouldSkipAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(
      ("permissions", "orders:read"),
      ("permissions", "")
    );

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Permissions.Count).IsEqualTo(1);
  }

  #endregion

  #region Principals Extraction

  [Test]
  public async Task InvokeAsync_WithUserIdClaim_ShouldAddUserPrincipalAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims((ClaimTypes.NameIdentifier, "user-abc"));

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    var expected = SecurityPrincipalId.User("user-abc");
    await Assert.That(accessor.Current!.SecurityPrincipals).Contains(expected);
  }

  [Test]
  public async Task InvokeAsync_WithGroupClaims_ShouldAddGroupPrincipalsAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(
      ("groups", "sales-team"),
      ("groups", "engineering")
    );

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.SecurityPrincipals).Contains(SecurityPrincipalId.Group("sales-team"));
    await Assert.That(accessor.Current!.SecurityPrincipals).Contains(SecurityPrincipalId.Group("engineering"));
  }

  [Test]
  public async Task InvokeAsync_WithEmptyGroupClaim_ShouldSkipAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(
      ("groups", "team"),
      ("groups", "")
    );

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.SecurityPrincipals.Count).IsEqualTo(1);
  }

  [Test]
  public async Task InvokeAsync_WithNoUserOrGroups_ShouldReturnEmptyPrincipalsAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = new DefaultHttpContext();

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.SecurityPrincipals.Count).IsEqualTo(0);
  }

  #endregion

  #region Claims Extraction

  [Test]
  public async Task InvokeAsync_WithClaims_ShouldExtractAllClaimsAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = _createContextWithClaims(
      ("tenant_id", "t1"),
      ("custom_claim", "custom_value")
    );

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Claims).ContainsKey("tenant_id");
    await Assert.That(accessor.Current!.Claims["tenant_id"]).IsEqualTo("t1");
    await Assert.That(accessor.Current!.Claims).ContainsKey("custom_claim");
    await Assert.That(accessor.Current!.Claims["custom_claim"]).IsEqualTo("custom_value");
  }

  [Test]
  public async Task InvokeAsync_WithDuplicateClaimTypes_ShouldKeepFirstAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var claims = new ClaimsIdentity(
      [new Claim("key", "first"), new Claim("key", "second")],
      "TestAuth"
    );
    var context = new DefaultHttpContext { User = new ClaimsPrincipal(claims) };

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Claims["key"]).IsEqualTo("first");
  }

  [Test]
  public async Task InvokeAsync_WithNoClaims_ShouldReturnEmptyDictionaryAsync() {
    // Arrange
    var (middleware, accessor) = _createMiddleware();
    var context = new DefaultHttpContext();

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Claims.Count).IsEqualTo(0);
  }

  #endregion

  #region Custom Options

  [Test]
  public async Task InvokeAsync_WithCustomTenantIdClaimType_ShouldUseCustomClaimAsync() {
    // Arrange
    var options = new WhizbangScopeOptions { TenantIdClaimType = "custom_tenant" };
    var (middleware, accessor) = _createMiddleware(options);
    var context = _createContextWithClaims(("custom_tenant", "custom-t1"));

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.TenantId).IsEqualTo("custom-t1");
  }

  [Test]
  public async Task InvokeAsync_WithCustomHeaderName_ShouldUseCustomHeaderAsync() {
    // Arrange
    var options = new WhizbangScopeOptions { TenantIdHeaderName = "X-Custom-Tenant" };
    var (middleware, accessor) = _createMiddleware(options);
    var context = new DefaultHttpContext();
    context.Request.Headers["X-Custom-Tenant"] = "custom-header-t1";

    // Act
    await middleware.InvokeAsync(context, accessor);

    // Assert
    await Assert.That(accessor.Current!.Scope.TenantId).IsEqualTo("custom-header-t1");
  }

  #endregion

  #region RequestScopeContext - Permission Methods

  [Test]
  public async Task HasPermission_WithMatchingPermission_ShouldReturnTrueAsync() {
    // Arrange
    var scopeContext = _createScopeContext(permissions: ["orders:read", "orders:write"]);

    // Act & Assert
    await Assert.That(scopeContext.HasPermission(new Permission("orders:read"))).IsTrue();
  }

  [Test]
  public async Task HasPermission_WithoutMatchingPermission_ShouldReturnFalseAsync() {
    // Arrange
    var scopeContext = _createScopeContext(permissions: ["orders:read"]);

    // Act & Assert
    await Assert.That(scopeContext.HasPermission(new Permission("orders:delete"))).IsFalse();
  }

  [Test]
  public async Task HasAnyPermission_WithOneMatching_ShouldReturnTrueAsync() {
    // Arrange
    var scopeContext = _createScopeContext(permissions: ["orders:read"]);

    // Act & Assert
    await Assert.That(scopeContext.HasAnyPermission(
      new Permission("orders:read"),
      new Permission("orders:write")
    )).IsTrue();
  }

  [Test]
  public async Task HasAnyPermission_WithNoneMatching_ShouldReturnFalseAsync() {
    // Arrange
    var scopeContext = _createScopeContext(permissions: ["orders:read"]);

    // Act & Assert
    await Assert.That(scopeContext.HasAnyPermission(
      new Permission("products:read"),
      new Permission("products:write")
    )).IsFalse();
  }

  [Test]
  public async Task HasAllPermissions_WithAllMatching_ShouldReturnTrueAsync() {
    // Arrange
    var scopeContext = _createScopeContext(permissions: ["orders:read", "orders:write"]);

    // Act & Assert
    await Assert.That(scopeContext.HasAllPermissions(
      new Permission("orders:read"),
      new Permission("orders:write")
    )).IsTrue();
  }

  [Test]
  public async Task HasAllPermissions_WithSomeMissing_ShouldReturnFalseAsync() {
    // Arrange
    var scopeContext = _createScopeContext(permissions: ["orders:read"]);

    // Act & Assert
    await Assert.That(scopeContext.HasAllPermissions(
      new Permission("orders:read"),
      new Permission("orders:write")
    )).IsFalse();
  }

  #endregion

  #region RequestScopeContext - Role Methods

  [Test]
  public async Task HasRole_WithMatchingRole_ShouldReturnTrueAsync() {
    // Arrange
    var scopeContext = _createScopeContext(roles: ["Admin", "Manager"]);

    // Act & Assert
    await Assert.That(scopeContext.HasRole("Admin")).IsTrue();
  }

  [Test]
  public async Task HasRole_WithoutMatchingRole_ShouldReturnFalseAsync() {
    // Arrange
    var scopeContext = _createScopeContext(roles: ["Admin"]);

    // Act & Assert
    await Assert.That(scopeContext.HasRole("SuperAdmin")).IsFalse();
  }

  [Test]
  public async Task HasAnyRole_WithOneMatching_ShouldReturnTrueAsync() {
    // Arrange
    var scopeContext = _createScopeContext(roles: ["Manager"]);

    // Act & Assert
    await Assert.That(scopeContext.HasAnyRole("Admin", "Manager")).IsTrue();
  }

  [Test]
  public async Task HasAnyRole_WithNoneMatching_ShouldReturnFalseAsync() {
    // Arrange
    var scopeContext = _createScopeContext(roles: ["Viewer"]);

    // Act & Assert
    await Assert.That(scopeContext.HasAnyRole("Admin", "Manager")).IsFalse();
  }

  #endregion

  #region RequestScopeContext - Principal Methods

  [Test]
  public async Task IsMemberOfAny_WithMatchingPrincipal_ShouldReturnTrueAsync() {
    // Arrange
    var scopeContext = _createScopeContext(principals: [SecurityPrincipalId.User("alice")]);

    // Act & Assert
    await Assert.That(scopeContext.IsMemberOfAny(
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.User("bob")
    )).IsTrue();
  }

  [Test]
  public async Task IsMemberOfAny_WithNoneMatching_ShouldReturnFalseAsync() {
    // Arrange
    var scopeContext = _createScopeContext(principals: [SecurityPrincipalId.User("alice")]);

    // Act & Assert
    await Assert.That(scopeContext.IsMemberOfAny(
      SecurityPrincipalId.User("bob"),
      SecurityPrincipalId.Group("team")
    )).IsFalse();
  }

  [Test]
  public async Task IsMemberOfAll_WithAllMatching_ShouldReturnTrueAsync() {
    // Arrange
    var scopeContext = _createScopeContext(principals: [
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("team")
    ]);

    // Act & Assert
    await Assert.That(scopeContext.IsMemberOfAll(
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("team")
    )).IsTrue();
  }

  [Test]
  public async Task IsMemberOfAll_WithSomeMissing_ShouldReturnFalseAsync() {
    // Arrange
    var scopeContext = _createScopeContext(principals: [SecurityPrincipalId.User("alice")]);

    // Act & Assert
    await Assert.That(scopeContext.IsMemberOfAll(
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("team")
    )).IsFalse();
  }

  #endregion

  #region WhizbangScopeOptions - Defaults

  [Test]
  public async Task Options_DefaultTenantIdClaimType_ShouldBeTenantIdAsync() {
    var options = new WhizbangScopeOptions();
    await Assert.That(options.TenantIdClaimType).IsEqualTo("tenant_id");
  }

  [Test]
  public async Task Options_DefaultTenantIdHeaderName_ShouldBeXTenantIdAsync() {
    var options = new WhizbangScopeOptions();
    await Assert.That(options.TenantIdHeaderName).IsEqualTo("X-Tenant-Id");
  }

  [Test]
  public async Task Options_DefaultUserIdClaimType_ShouldBeNameIdentifierAsync() {
    var options = new WhizbangScopeOptions();
    await Assert.That(options.UserIdClaimType).IsEqualTo(ClaimTypes.NameIdentifier);
  }

  [Test]
  public async Task Options_DefaultPermissionsClaimType_ShouldBePermissionsAsync() {
    var options = new WhizbangScopeOptions();
    await Assert.That(options.PermissionsClaimType).IsEqualTo("permissions");
  }

  [Test]
  public async Task Options_DefaultGroupsClaimType_ShouldBeGroupsAsync() {
    var options = new WhizbangScopeOptions();
    await Assert.That(options.GroupsClaimType).IsEqualTo("groups");
  }

  [Test]
  public async Task Options_ExtensionMappings_ShouldBeEmptyByDefaultAsync() {
    var options = new WhizbangScopeOptions();
    await Assert.That(options.ExtensionClaimMappings.Count).IsEqualTo(0);
    await Assert.That(options.ExtensionHeaderMappings.Count).IsEqualTo(0);
  }

  #endregion

  #region Helpers

  private static (WhizbangScopeMiddleware middleware, TestScopeContextAccessor accessor) _createMiddleware(
      WhizbangScopeOptions? options = null) {
    var accessor = new TestScopeContextAccessor();
    var middleware = new WhizbangScopeMiddleware(_ => Task.CompletedTask, options);
    return (middleware, accessor);
  }

  private static DefaultHttpContext _createContextWithClaims(params (string type, string value)[] claims) {
    var claimsList = claims.Select(c => new Claim(c.type, c.value)).ToList();
    var identity = new ClaimsIdentity(claimsList, "TestAuth");
    var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    return context;
  }

  private static RequestScopeContext _createScopeContext(
      string[]? roles = null,
      string[]? permissions = null,
      SecurityPrincipalId[]? principals = null) {
    return new RequestScopeContext {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(roles ?? []),
      Permissions = new HashSet<Permission>((permissions ?? []).Select(p => new Permission(p))),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(principals ?? []),
      Claims = new Dictionary<string, string>()
    };
  }

  #endregion
}

/// <summary>
/// Simple test accessor for scope context.
/// </summary>
internal sealed class TestScopeContextAccessor : IScopeContextAccessor {
  public IScopeContext? Current { get; set; }
}
