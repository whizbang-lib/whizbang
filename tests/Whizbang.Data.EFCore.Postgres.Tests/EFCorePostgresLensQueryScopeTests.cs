#pragma warning disable CS0618, WHIZ400

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for the scope-filtering paths of EFCorePostgresLensQuery:
/// ScopedAccessHelper (null scope context guard, override wrapping, filter application),
/// FilteredScopedAccess (Query and GetByIdAsync), OverrideScopeContext delegation,
/// and Scope/ScopeOverride on the arity-2/3 multi-generic lens queries.
/// </summary>
[Category("EFCore")]
[Category("Lenses")]
[Category("Unit")]
[Category("Shard2")]
public class EFCorePostgresLensQueryScopeTests {
  private readonly Uuid7IdProvider _idProvider = new();

  #region Test Models

  private sealed record ScopedItem { public string Name { get; init; } = ""; }
  private sealed record PairedItem { public string Name { get; init; } = ""; }
  private sealed record TripleItem { public string Name { get; init; } = ""; }

  #endregion

  #region Test DbContext

  private sealed class ScopeTestDbContext(DbContextOptions<ScopeTestDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      ConfigurePerspectiveRow<ScopedItem>(modelBuilder);
      ConfigurePerspectiveRow<PairedItem>(modelBuilder);
      ConfigurePerspectiveRow<TripleItem>(modelBuilder);
    }

    private static void ConfigurePerspectiveRow<TModel>(ModelBuilder modelBuilder) where TModel : class {
      modelBuilder.Entity<PerspectiveRow<TModel>>(entity => {
        entity.HasKey(e => e.Id);
        entity.OwnsOne(e => e.Data, data => data.WithOwner());
        entity.OwnsOne(e => e.Metadata, metadata => {
          metadata.WithOwner();
          metadata.Property(m => m.EventType).IsRequired();
          metadata.Property(m => m.EventId).IsRequired();
          metadata.Property(m => m.Timestamp).IsRequired();
        });
        entity.Property(e => e.Scope)
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);
      });
    }
  }

  #endregion

  #region Test Helpers

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
    public string? ActualPrincipal { get; init; }
    public string? EffectivePrincipal { get; init; }
    public SecurityContextType ContextType { get; init; } = SecurityContextType.User;
    public bool HasPermission(Permission permission) => Permissions.Contains(permission);
    public bool HasAnyPermission(params Permission[] permissions) => permissions.Any(Permissions.Contains);
    public bool HasAllPermissions(params Permission[] permissions) => permissions.All(Permissions.Contains);
    public bool HasRole(string roleName) => Roles.Contains(roleName);
    public bool HasAnyRole(params string[] roleNames) => roleNames.Any(Roles.Contains);
    public bool IsMemberOfAny(params SecurityPrincipalId[] principals) => principals.Any(SecurityPrincipals.Contains);
    public bool IsMemberOfAll(params SecurityPrincipalId[] principals) => principals.All(SecurityPrincipals.Contains);
  }

  private ScopeTestDbContext CreateInMemoryDbContext() {
    var options = new DbContextOptionsBuilder<ScopeTestDbContext>()
        .UseInMemoryDatabase(databaseName: _idProvider.NewGuid().ToString())
        .Options;
    return new ScopeTestDbContext(options);
  }

  private static TestScopeContextAccessor CreateScopeAccessor(
      string? tenantId = null,
      string? userId = null,
      string? organizationId = null,
      string? customerId = null,
      HashSet<SecurityPrincipalId>? principals = null) {
    return new TestScopeContextAccessor {
      Current = new TestScopeContext {
        Scope = new PerspectiveScope {
          TenantId = tenantId,
          UserId = userId,
          OrganizationId = organizationId,
          CustomerId = customerId
        },
        SecurityPrincipals = principals ?? []
      }
    };
  }

  private static IOptions<WhizbangCoreOptions> CreateOptions(QueryScope defaultScope = QueryScope.Tenant) {
    return Options.Create(new WhizbangCoreOptions { DefaultQueryScope = defaultScope });
  }

  private static Dictionary<Type, string> CreateTableNames() => new() {
    { typeof(ScopedItem), "scoped_items" },
    { typeof(PairedItem), "paired_items" },
    { typeof(TripleItem), "triple_items" }
  };

  private async Task SeedAsync<T>(
      DbContext context,
      Guid id,
      T data,
      string? tenantId = null,
      string? userId = null,
      string? organizationId = null,
      string? customerId = null,
      List<string>? allowedPrincipals = null)
      where T : class {
    var row = new PerspectiveRow<T> {
      Id = id,
      Data = data,
      Metadata = new PerspectiveMetadata {
        EventType = "Created",
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
    context.Set<PerspectiveRow<T>>().Add(row);
    await context.SaveChangesAsync();
  }

  private EFCorePostgresLensQuery<ScopedItem> CreateLensQuery(
      DbContext context,
      IScopeContextAccessor accessor,
      QueryScope defaultScope = QueryScope.Tenant) {
    return new EFCorePostgresLensQuery<ScopedItem>(context, "scoped_items", accessor, CreateOptions(defaultScope));
  }

  #endregion

  // ===== Single-model Scope() filtering =====

  #region Single-Model Scope Filtering

  [Test]
  public async Task Scope_Global_ReturnsAllRowsRegardlessOfTenantAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "a" }, tenantId: "tenant-1");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "b" }, tenantId: "tenant-2");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    var results = await lensQuery.Scope(QueryScope.Global).Query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Scope_Tenant_ReturnsOnlyAmbientTenantRowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "mine" }, tenantId: "tenant-1");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "other" }, tenantId: "tenant-2");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    var results = await lensQuery.Scope(QueryScope.Tenant).Query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("mine");
    await Assert.That(results[0].Scope.TenantId).IsEqualTo("tenant-1");
  }

  [Test]
  public async Task Scope_Tenant_WhenScopeContextIsNull_ThrowsInvalidOperationExceptionAsync() {
    await using var context = CreateInMemoryDbContext();
    var accessor = new TestScopeContextAccessor { Current = null };
    var lensQuery = CreateLensQuery(context, accessor);

    InvalidOperationException? exception = null;
    try {
      _ = lensQuery.Scope(QueryScope.Tenant);
    } catch (InvalidOperationException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.Message).Contains("Scope 'Tenant' requires ambient scope context");
    await Assert.That(exception.Message).Contains("IScopeContextAccessor.Current is null");
  }

  [Test]
  public async Task Scope_Organization_FiltersByTenantAndOrganizationAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "sales" }, tenantId: "tenant-1", organizationId: "org-sales");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "eng" }, tenantId: "tenant-1", organizationId: "org-eng");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "other-tenant" }, tenantId: "tenant-2", organizationId: "org-sales");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1", organizationId: "org-sales"));

    var results = await lensQuery.Scope(QueryScope.Organization).Query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("sales");
  }

  [Test]
  public async Task Scope_Customer_FiltersByTenantAndCustomerAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "acme" }, tenantId: "tenant-1", customerId: "customer-acme");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "globex" }, tenantId: "tenant-1", customerId: "customer-globex");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "other-tenant" }, tenantId: "tenant-2", customerId: "customer-acme");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1", customerId: "customer-acme"));

    var results = await lensQuery.Scope(QueryScope.Customer).Query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("acme");
  }

  [Test]
  public async Task Scope_User_FiltersByTenantAndUserAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "alice" }, tenantId: "tenant-1", userId: "user-alice");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "bob" }, tenantId: "tenant-1", userId: "user-bob");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "alice-elsewhere" }, tenantId: "tenant-2", userId: "user-alice");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1", userId: "user-alice"));

    var results = await lensQuery.Scope(QueryScope.User).Query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("alice");
  }

  [Test]
  public async Task Scope_Tenant_GetByIdAsync_MatchingTenant_ReturnsModelAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new ScopedItem { Name = "found" }, tenantId: "tenant-1");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    var result = await lensQuery.Scope(QueryScope.Tenant).GetByIdAsync(id);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("found");
  }

  [Test]
  public async Task Scope_Tenant_GetByIdAsync_WrongTenant_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new ScopedItem { Name = "hidden" }, tenantId: "tenant-1");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-2"));

    var result = await lensQuery.Scope(QueryScope.Tenant).GetByIdAsync(id);

    await Assert.That(result).IsNull();
  }

  #endregion

  // ===== Single-model ScopeOverride() =====

  #region Single-Model ScopeOverride

  [Test]
  public async Task ScopeOverride_Tenant_QueriesOverriddenTenantAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "ambient" }, tenantId: "tenant-1");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "overridden" }, tenantId: "tenant-2");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    var results = await lensQuery
        .ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride { TenantId = "tenant-2" })
        .Query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("overridden");
    await Assert.That(results[0].Scope.TenantId).IsEqualTo("tenant-2");
  }

  [Test]
  public async Task ScopeOverride_PartialOverride_FallsBackToAmbientForUnsetFieldsAsync() {
    await using var context = CreateInMemoryDbContext();
    // Ambient: tenant-1 / user-alice. Override only the user - tenant must come from ambient context.
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "bob-in-tenant-1" },
        tenantId: "tenant-1", userId: "user-bob");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "alice-in-tenant-1" },
        tenantId: "tenant-1", userId: "user-alice");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "bob-in-tenant-2" },
        tenantId: "tenant-2", userId: "user-bob");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1", userId: "user-alice"));

    var results = await lensQuery
        .ScopeOverride(QueryScope.User, new ScopeFilterOverride { UserId = "user-bob" })
        .Query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("bob-in-tenant-1");
  }

  [Test]
  public async Task ScopeOverride_Global_IgnoresOverrideAndReturnsAllRowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "a" }, tenantId: "tenant-1");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "b" }, tenantId: "tenant-2");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    var results = await lensQuery
        .ScopeOverride(QueryScope.Global, new ScopeFilterOverride { TenantId = "tenant-2" })
        .Query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(2);
  }

  #endregion

  // ===== OverrideScopeContext delegation =====

  #region OverrideScopeContext Delegation

  [Test]
  public async Task OverrideScopeContext_Scope_PrefersOverrideValuesAsync() {
    var inner = new TestScopeContext {
      Scope = new PerspectiveScope {
        TenantId = "inner-tenant",
        UserId = "inner-user",
        OrganizationId = "inner-org",
        CustomerId = "inner-cust"
      }
    };
    var overrides = new ScopeFilterOverride {
      TenantId = "ov-tenant",
      UserId = "ov-user",
      OrganizationId = "ov-org",
      CustomerId = "ov-cust"
    };

    var sut = new OverrideScopeContext(inner, overrides);

    await Assert.That(sut.Scope.TenantId).IsEqualTo("ov-tenant");
    await Assert.That(sut.Scope.UserId).IsEqualTo("ov-user");
    await Assert.That(sut.Scope.OrganizationId).IsEqualTo("ov-org");
    await Assert.That(sut.Scope.CustomerId).IsEqualTo("ov-cust");
  }

  [Test]
  public async Task OverrideScopeContext_Scope_FallsBackToInnerWhenOverrideUnsetAsync() {
    var inner = new TestScopeContext {
      Scope = new PerspectiveScope {
        TenantId = "inner-tenant",
        UserId = "inner-user",
        OrganizationId = "inner-org",
        CustomerId = "inner-cust"
      }
    };

    var sut = new OverrideScopeContext(inner, new ScopeFilterOverride { UserId = "ov-user" });

    await Assert.That(sut.Scope.TenantId).IsEqualTo("inner-tenant");
    await Assert.That(sut.Scope.UserId).IsEqualTo("ov-user");
    await Assert.That(sut.Scope.OrganizationId).IsEqualTo("inner-org");
    await Assert.That(sut.Scope.CustomerId).IsEqualTo("inner-cust");
  }

  [Test]
  public async Task OverrideScopeContext_DelegatesIdentityAndCollectionsToInnerAsync() {
    var principal = SecurityPrincipalId.Group("sales-team");
    var inner = new TestScopeContext {
      Roles = new HashSet<string> { "admin" },
      Permissions = new HashSet<Permission> { new("orders:read") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> { principal },
      Claims = new Dictionary<string, string> { { "dept", "sales" } },
      ActualPrincipal = "actual-user",
      EffectivePrincipal = "effective-user",
      ContextType = SecurityContextType.ServiceAccount
    };

    var sut = new OverrideScopeContext(inner, new ScopeFilterOverride());

    await Assert.That(sut.Roles.Contains("admin")).IsTrue();
    await Assert.That(sut.Permissions.Contains(new Permission("orders:read"))).IsTrue();
    await Assert.That(sut.SecurityPrincipals.Contains(principal)).IsTrue();
    await Assert.That(sut.Claims["dept"]).IsEqualTo("sales");
    await Assert.That(sut.ActualPrincipal).IsEqualTo("actual-user");
    await Assert.That(sut.EffectivePrincipal).IsEqualTo("effective-user");
    await Assert.That(sut.ContextType).IsEqualTo(SecurityContextType.ServiceAccount);
  }

  [Test]
  public async Task OverrideScopeContext_DelegatesPredicateMethodsToInnerAsync() {
    var salesTeam = SecurityPrincipalId.Group("sales-team");
    var engineering = SecurityPrincipalId.Group("engineering");
    var inner = new TestScopeContext {
      Roles = new HashSet<string> { "admin" },
      Permissions = new HashSet<Permission> { new("orders:read") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> { salesTeam }
    };

    var sut = new OverrideScopeContext(inner, new ScopeFilterOverride { TenantId = "any" });

    await Assert.That(sut.HasPermission(new Permission("orders:read"))).IsTrue();
    await Assert.That(sut.HasPermission(new Permission("orders:write"))).IsFalse();
    await Assert.That(sut.HasAnyPermission(new Permission("orders:write"), new Permission("orders:read"))).IsTrue();
    await Assert.That(sut.HasAllPermissions(new Permission("orders:read"))).IsTrue();
    await Assert.That(sut.HasAllPermissions(new Permission("orders:read"), new Permission("orders:write"))).IsFalse();
    await Assert.That(sut.HasRole("admin")).IsTrue();
    await Assert.That(sut.HasRole("viewer")).IsFalse();
    await Assert.That(sut.HasAnyRole("viewer", "admin")).IsTrue();
    await Assert.That(sut.IsMemberOfAny(salesTeam)).IsTrue();
    await Assert.That(sut.IsMemberOfAny(engineering)).IsFalse();
    await Assert.That(sut.IsMemberOfAll(salesTeam)).IsTrue();
    await Assert.That(sut.IsMemberOfAll(salesTeam, engineering)).IsFalse();
  }

  #endregion

  // ===== Arity-2/3 Scope and ScopeOverride delegation =====

  #region Multi-Generic Scope Delegation

  [Test]
  public async Task TwoGeneric_Scope_Tenant_FiltersRowsByAmbientTenantAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "mine" }, tenantId: "tenant-1");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "other" }, tenantId: "tenant-2");
    var lensQuery = new EFCorePostgresLensQuery<ScopedItem, PairedItem>(
        context, CreateTableNames(), CreateScopeAccessor(tenantId: "tenant-1"), CreateOptions());

    var results = await lensQuery.Scope(QueryScope.Tenant).Query<ScopedItem>().ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("mine");
  }

  [Test]
  public async Task TwoGeneric_ScopeOverride_Tenant_QueriesOverriddenTenantAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "ambient" }, tenantId: "tenant-1");
    await SeedAsync(context, _idProvider.NewGuid(), new ScopedItem { Name = "overridden" }, tenantId: "tenant-2");
    var lensQuery = new EFCorePostgresLensQuery<ScopedItem, PairedItem>(
        context, CreateTableNames(), CreateScopeAccessor(tenantId: "tenant-1"), CreateOptions());

    var results = await lensQuery
        .ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride { TenantId = "tenant-2" })
        .Query<ScopedItem>().ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("overridden");
  }

  [Test]
  public async Task ThreeGeneric_Scope_Tenant_FiltersRowsByAmbientTenantAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new TripleItem { Name = "mine" }, tenantId: "tenant-1");
    await SeedAsync(context, _idProvider.NewGuid(), new TripleItem { Name = "other" }, tenantId: "tenant-2");
    var lensQuery = new EFCorePostgresLensQuery<ScopedItem, PairedItem, TripleItem>(
        context, CreateTableNames(), CreateScopeAccessor(tenantId: "tenant-1"), CreateOptions());

    var results = await lensQuery.Scope(QueryScope.Tenant).Query<TripleItem>().ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("mine");
  }

  [Test]
  public async Task ThreeGeneric_ScopeOverride_Tenant_QueriesOverriddenTenantAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new TripleItem { Name = "ambient" }, tenantId: "tenant-1");
    await SeedAsync(context, _idProvider.NewGuid(), new TripleItem { Name = "overridden" }, tenantId: "tenant-2");
    var lensQuery = new EFCorePostgresLensQuery<ScopedItem, PairedItem, TripleItem>(
        context, CreateTableNames(), CreateScopeAccessor(tenantId: "tenant-1"), CreateOptions());

    var results = await lensQuery
        .ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride { TenantId = "tenant-2" })
        .Query<TripleItem>().ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("overridden");
  }

  #endregion
}
