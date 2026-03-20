#pragma warning disable CS0618

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the fluent scope-before-query API on EFCoreFilterableLensQuery.
/// Verifies Scope(), ScopeOverride(), DefaultScope across all scope types.
/// </summary>
[Category("Integration")]
public class FluentScopeApiTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new();

  // === Helpers ===

  private async Task _seedOrderAsync(
      DbContext context,
      Guid orderId,
      decimal amount,
      string? tenantId = null,
      string? userId = null,
      string? organizationId = null,
      string? customerId = null,
      List<string>? allowedPrincipals = null) {
    var order = new Order {
      OrderId = TestOrderId.From(orderId),
      Amount = amount,
      Status = "Created"
    };

    var row = new PerspectiveRow<Order> {
      Id = orderId,
      Data = order,
      Metadata = new PerspectiveMetadata {
        EventType = "OrderCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope {
        TenantId = tenantId,
        UserId = userId,
        OrganizationId = organizationId,
        CustomerId = customerId,
        AllowedPrincipals = allowedPrincipals ?? []
      },
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };

    context.Set<PerspectiveRow<Order>>().Add(row);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();
  }

  private static TestScopeContextAccessor _createScopeContext(
      string? tenantId = null,
      string? userId = null,
      string? organizationId = null,
      string? customerId = null,
      HashSet<SecurityPrincipalId>? principals = null) {
    var accessor = new TestScopeContextAccessor();
    accessor.Current = new TestScopeContext {
      Scope = new PerspectiveScope {
        TenantId = tenantId,
        UserId = userId,
        OrganizationId = organizationId,
        CustomerId = customerId
      },
      SecurityPrincipals = principals ?? []
    };
    return accessor;
  }

  private static IOptions<WhizbangCoreOptions> _options(QueryScope defaultScope = QueryScope.Tenant) {
    return Options.Create(new WhizbangCoreOptions { DefaultQueryScope = defaultScope });
  }

  // === Scope(QueryScope.Global) ===

  [Test]
  public async Task Scope_Global_ReturnsAllRowsAsync() {
    await using var context = CreateDbContext();
    var accessor = _createScopeContext(tenantId: "tenant-1");
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options());

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-2");

    var result = await lensQuery.Scope(QueryScope.Global).Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(2);
  }

  // === Scope(QueryScope.Tenant) ===

  [Test]
  public async Task Scope_Tenant_ReturnsOnlyCurrentTenantRowsAsync() {
    await using var context = CreateDbContext();
    var accessor = _createScopeContext(tenantId: "tenant-1");
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options());

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    var order3Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-1");
    await _seedOrderAsync(context, order3Id, 300m, tenantId: "tenant-2");

    var result = await lensQuery.Scope(QueryScope.Tenant).Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result.All(r => r.Scope.TenantId == "tenant-1")).IsTrue();
  }

  // === Scope(QueryScope.User) ===

  [Test]
  public async Task Scope_User_ReturnsTenantAndUserFilteredRowsAsync() {
    await using var context = CreateDbContext();
    var accessor = _createScopeContext(tenantId: "tenant-1", userId: "user-alice");
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options());

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    var order3Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1", userId: "user-alice");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-1", userId: "user-bob");
    await _seedOrderAsync(context, order3Id, 300m, tenantId: "tenant-2", userId: "user-alice");

    var result = await lensQuery.Scope(QueryScope.User).Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(order1Id);
  }

  // === Scope(QueryScope.Organization) ===

  [Test]
  public async Task Scope_Organization_ReturnsTenantAndOrgFilteredRowsAsync() {
    await using var context = CreateDbContext();
    var accessor = _createScopeContext(tenantId: "tenant-1", organizationId: "org-sales");
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options());

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1", organizationId: "org-sales");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-1", organizationId: "org-engineering");

    var result = await lensQuery.Scope(QueryScope.Organization).Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(order1Id);
  }

  // === Scope(QueryScope.Customer) ===

  [Test]
  public async Task Scope_Customer_ReturnsTenantAndCustomerFilteredRowsAsync() {
    await using var context = CreateDbContext();
    var accessor = _createScopeContext(tenantId: "tenant-1", customerId: "customer-acme");
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options());

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1", customerId: "customer-acme");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-1", customerId: "customer-globex");

    var result = await lensQuery.Scope(QueryScope.Customer).Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(order1Id);
  }

  // === Scope(QueryScope.Principal) ===

  [Test]
  public async Task Scope_Principal_ReturnsTenantAndPrincipalFilteredRowsAsync() {
    await using var context = CreateDbContext();
    var principals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.Group("sales-team")
    };
    var accessor = _createScopeContext(tenantId: "tenant-1", principals: principals);
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options());

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1", allowedPrincipals: ["group:sales-team"]);
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-1", allowedPrincipals: ["group:engineering"]);

    var result = await lensQuery.Scope(QueryScope.Principal).Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(order1Id);
  }

  // === Scope(QueryScope.UserOrPrincipal) ===

  [Test]
  public async Task Scope_UserOrPrincipal_ReturnsOwnedAndSharedRowsAsync() {
    await using var context = CreateDbContext();
    var principals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("sales-team")
    };
    var accessor = _createScopeContext(tenantId: "tenant-1", userId: "user-alice", principals: principals);
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options());

    var order1Id = _idProvider.NewGuid(); // Owned by alice
    var order2Id = _idProvider.NewGuid(); // Shared with sales-team
    var order3Id = _idProvider.NewGuid(); // Not visible to alice
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1", userId: "user-alice");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-1", userId: "user-bob", allowedPrincipals: ["group:sales-team"]);
    await _seedOrderAsync(context, order3Id, 300m, tenantId: "tenant-1", userId: "user-charlie", allowedPrincipals: ["group:engineering"]);

    var result = await lensQuery.Scope(QueryScope.UserOrPrincipal).Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(2);
    var ids = result.Select(r => r.Id).ToHashSet();
    await Assert.That(ids.Contains(order1Id)).IsTrue();
    await Assert.That(ids.Contains(order2Id)).IsTrue();
    await Assert.That(ids.Contains(order3Id)).IsFalse();
  }

  // === DefaultScope ===

  [Test]
  public async Task DefaultScope_UsesConfiguredDefaultFromOptionsAsync() {
    await using var context = CreateDbContext();
    var accessor = _createScopeContext(tenantId: "tenant-1");
    // Default is Tenant
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options(QueryScope.Tenant));

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-2");

    var result = await lensQuery.DefaultScope.Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Scope.TenantId).IsEqualTo("tenant-1");
  }

  [Test]
  public async Task DefaultScope_WhenGlobal_ReturnsAllRowsAsync() {
    await using var context = CreateDbContext();
    var accessor = _createScopeContext(tenantId: "tenant-1");
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options(QueryScope.Global));

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-2");

    var result = await lensQuery.DefaultScope.Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(2);
  }

  // === GetByIdAsync through scope ===

  [Test]
  public async Task Scope_Tenant_GetByIdAsync_ReturnsMatchingRowAsync() {
    await using var context = CreateDbContext();
    var accessor = _createScopeContext(tenantId: "tenant-1");
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options());

    var orderId = _idProvider.NewGuid();
    await _seedOrderAsync(context, orderId, 100m, tenantId: "tenant-1");

    var result = await lensQuery.Scope(QueryScope.Tenant).GetByIdAsync(orderId);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Amount).IsEqualTo(100m);
  }

  [Test]
  public async Task Scope_Tenant_GetByIdAsync_WrongTenant_ReturnsNullAsync() {
    await using var context = CreateDbContext();
    var accessor = _createScopeContext(tenantId: "tenant-2");
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options());

    var orderId = _idProvider.NewGuid();
    await _seedOrderAsync(context, orderId, 100m, tenantId: "tenant-1");

    var result = await lensQuery.Scope(QueryScope.Tenant).GetByIdAsync(orderId);

    await Assert.That(result).IsNull();
  }

  // === ScopeOverride ===

  [Test]
  public async Task ScopeOverride_UsesProvidedTenantIdAsync() {
    await using var context = CreateDbContext();
    // Ambient context has tenant-1
    var accessor = _createScopeContext(tenantId: "tenant-1");
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options());

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-2");

    // Override to tenant-2
    var result = await lensQuery.ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride {
      TenantId = "tenant-2"
    }).Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Scope.TenantId).IsEqualTo("tenant-2");
  }

  // === Legacy API delegates to DefaultScope ===

  [Test]
  public async Task LegacyQuery_DelegatesToDefaultScopeAsync() {
    await using var context = CreateDbContext();
    var accessor = _createScopeContext(tenantId: "tenant-1");
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options(QueryScope.Tenant));

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-2");

    // Legacy .Query should use default scope (Tenant)
    var result = await lensQuery.Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Scope.TenantId).IsEqualTo("tenant-1");
  }

  // === Error cases ===

  [Test]
  public async Task Scope_Tenant_WhenNoScopeContext_ThrowsAsync() {
    await using var context = CreateDbContext();
    // No scope context set
    var accessor = new TestScopeContextAccessor();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, _options());

    await Assert.That(() => lensQuery.Scope(QueryScope.Tenant))
        .Throws<InvalidOperationException>();
  }

  // === Test helpers ===

  private sealed class TestScopeContextAccessor : IScopeContextAccessor {
    public IScopeContext? Current { get; set; }
    public IMessageContext? InitiatingContext { get; set; }
  }

  private sealed class TestScopeContext : IScopeContext {
    public PerspectiveScope Scope { get; init; } = new();
    public IReadOnlySet<string> Roles { get; init; } = new HashSet<string>();
    public IReadOnlySet<Permission> Permissions { get; init; } = new HashSet<Permission>();
    public IReadOnlySet<SecurityPrincipalId> SecurityPrincipals { get; init; } = new HashSet<SecurityPrincipalId>();
    public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();
    public string? ActualPrincipal => null;
    public string? EffectivePrincipal => null;
    public SecurityContextType ContextType => SecurityContextType.User;
    public bool HasPermission(Permission permission) => Permissions.Contains(permission);
    public bool HasAnyPermission(params Permission[] permissions) => permissions.Any(Permissions.Contains);
    public bool HasAllPermissions(params Permission[] permissions) => permissions.All(Permissions.Contains);
    public bool HasRole(string roleName) => Roles.Contains(roleName);
    public bool HasAnyRole(params string[] roleNames) => roleNames.Any(Roles.Contains);
    public bool IsMemberOfAny(params SecurityPrincipalId[] principals) => principals.Any(SecurityPrincipals.Contains);
    public bool IsMemberOfAll(params SecurityPrincipalId[] principals) => principals.All(SecurityPrincipals.Contains);
  }
}
