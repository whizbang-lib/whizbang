#pragma warning disable CS0618
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

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Test scope context accessor shared by the filterable-lens scoped-access test classes below.
/// </summary>
internal sealed class FilterableLensScopeContextAccessor : IScopeContextAccessor {
  public IScopeContext? Current { get; set; }
  public IMessageContext? InitiatingContext { get; set; }
}

/// <summary>
/// Minimal IScopeContext implementation for driving ambient scope values in tests.
/// </summary>
internal sealed class FilterableLensScopeContext : IScopeContext {
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

/// <summary>
/// Integration tests for the fluent scope API of EFCoreFilterableLensQuery:
/// Scope(), ScopeOverride(), DefaultScope, the empty-filter delegation of
/// Query/GetByIdAsync, the OverrideScopeContext wrapper, and constructor guards.
/// Complements EFCoreFilterableLensQueryTests, which covers ApplyFilter-driven filtering.
/// </summary>
[Category("Integration")]
public class EFCoreFilterableLensQueryScopedAccessTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new();

  // === Helper Methods ===

  private static FilterableLensScopeContextAccessor CreateScopeAccessor(
      string? tenantId = null,
      string? userId = null,
      HashSet<SecurityPrincipalId>? principals = null) {
    return new FilterableLensScopeContextAccessor {
      Current = new FilterableLensScopeContext {
        Scope = new PerspectiveScope {
          TenantId = tenantId,
          UserId = userId
        },
        SecurityPrincipals = principals ?? []
      }
    };
  }

  private static IOptions<WhizbangCoreOptions> CreateOptions(QueryScope defaultScope = QueryScope.Tenant) {
    return Options.Create(new WhizbangCoreOptions { DefaultQueryScope = defaultScope });
  }

  private static EFCoreFilterableLensQuery<Order> CreateLensQuery(
      DbContext context,
      IScopeContextAccessor accessor,
      QueryScope defaultScope = QueryScope.Tenant) {
    return new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", accessor, CreateOptions(defaultScope));
  }

  private async Task<Guid> _seedOrderAsync(
      DbContext context,
      decimal amount,
      string? tenantId = null,
      string? userId = null,
      List<string>? allowedPrincipals = null) {
    var orderId = _idProvider.NewGuid();
    var row = new PerspectiveRow<Order> {
      Id = orderId,
      Data = new Order {
        OrderId = TestOrderId.From(orderId),
        Amount = amount,
        Status = "Created"
      },
      Metadata = new PerspectiveMetadata {
        EventType = "OrderCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope {
        TenantId = tenantId,
        UserId = userId,
        AllowedPrincipals = allowedPrincipals ?? []
      },
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };

    context.Set<PerspectiveRow<Order>>().Add(row);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();
    return orderId;
  }

  // === Scope() ===

  [Test]
  public async Task Scope_Global_Query_ReturnsAllRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    await _seedOrderAsync(context, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, 200m, tenantId: "tenant-2");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    // Act
    var results = await lensQuery.Scope(QueryScope.Global).Query.ToListAsync();

    // Assert - Global scope bypasses all filtering
    await Assert.That(results).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Scope_Global_GetByIdAsync_ReturnsModelAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var orderId = await _seedOrderAsync(context, 100m, tenantId: "tenant-1");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-2"));

    // Act - Global scope ignores the (mismatched) ambient tenant
    var result = await lensQuery.Scope(QueryScope.Global).GetByIdAsync(orderId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Amount).IsEqualTo(100m);
  }

  [Test]
  public async Task Scope_Tenant_Query_ReturnsOnlyAmbientTenantRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var mineId = await _seedOrderAsync(context, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, 200m, tenantId: "tenant-2");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    // Act
    var results = await lensQuery.Scope(QueryScope.Tenant).Query.ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Id).IsEqualTo(mineId);
    await Assert.That(results[0].Scope.TenantId).IsEqualTo("tenant-1");
  }

  [Test]
  public async Task Scope_Tenant_GetByIdAsync_MatchingTenant_ReturnsModelAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var orderId = await _seedOrderAsync(context, 100m, tenantId: "tenant-1");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    // Act
    var result = await lensQuery.Scope(QueryScope.Tenant).GetByIdAsync(orderId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Amount).IsEqualTo(100m);
  }

  [Test]
  public async Task Scope_Tenant_GetByIdAsync_WrongTenant_ReturnsNullAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var orderId = await _seedOrderAsync(context, 100m, tenantId: "tenant-1");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-2"));

    // Act
    var result = await lensQuery.Scope(QueryScope.Tenant).GetByIdAsync(orderId);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task Scope_Tenant_WhenScopeContextIsNull_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var accessor = new FilterableLensScopeContextAccessor { Current = null };
    var lensQuery = CreateLensQuery(context, accessor);

    // Act
    InvalidOperationException? exception = null;
    try {
      _ = lensQuery.Scope(QueryScope.Tenant);
    } catch (InvalidOperationException ex) {
      exception = ex;
    }

    // Assert
    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.Message).Contains("Scope 'Tenant' requires ambient scope context");
    await Assert.That(exception.Message).Contains("IScopeContextAccessor.Current is null");
  }

  // === ScopeOverride() ===

  [Test]
  public async Task ScopeOverride_Tenant_QueriesOverriddenTenantAsync() {
    // Arrange
    await using var context = CreateDbContext();
    await _seedOrderAsync(context, 100m, tenantId: "tenant-1");
    var overriddenId = await _seedOrderAsync(context, 200m, tenantId: "tenant-2");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    // Act - override wins over the ambient tenant
    var results = await lensQuery
        .ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride { TenantId = "tenant-2" })
        .Query.ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Id).IsEqualTo(overriddenId);
  }

  [Test]
  public async Task ScopeOverride_PartialOverride_FallsBackToAmbientForUnsetFieldsAsync() {
    // Arrange - ambient tenant-1/user-alice; only the user is overridden
    await using var context = CreateDbContext();
    var bobInTenant1Id = await _seedOrderAsync(context, 100m, tenantId: "tenant-1", userId: "user-bob");
    await _seedOrderAsync(context, 200m, tenantId: "tenant-1", userId: "user-alice");
    await _seedOrderAsync(context, 300m, tenantId: "tenant-2", userId: "user-bob");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1", userId: "user-alice"));

    // Act
    var results = await lensQuery
        .ScopeOverride(QueryScope.User, new ScopeFilterOverride { UserId = "user-bob" })
        .Query.ToListAsync();

    // Assert - tenant came from ambient context, user from the override
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Id).IsEqualTo(bobInTenant1Id);
  }

  [Test]
  public async Task ScopeOverride_Principal_UsesAmbientPrincipalsWithOverriddenTenantAsync() {
    // Arrange - principal set comes from ambient context, tenant from the override
    await using var context = CreateDbContext();
    await _seedOrderAsync(context, 100m, tenantId: "tenant-1", allowedPrincipals: ["group:sales-team"]);
    var sharedInTenant2Id = await _seedOrderAsync(context, 200m, tenantId: "tenant-2", allowedPrincipals: ["group:sales-team"]);
    await _seedOrderAsync(context, 300m, tenantId: "tenant-2", allowedPrincipals: ["group:engineering-team"]);

    var accessor = CreateScopeAccessor(
        tenantId: "tenant-1",
        principals: [SecurityPrincipalId.Group("sales-team")]);
    var lensQuery = CreateLensQuery(context, accessor);

    // Act
    var results = await lensQuery
        .ScopeOverride(QueryScope.Principal, new ScopeFilterOverride { TenantId = "tenant-2" })
        .Query.ToListAsync();

    // Assert - only the tenant-2 row shared with sales-team
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Id).IsEqualTo(sharedInTenant2Id);
  }

  [Test]
  public async Task ScopeOverride_Global_IgnoresOverrideAndReturnsAllRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    await _seedOrderAsync(context, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, 200m, tenantId: "tenant-2");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    // Act - Global maps to ScopeFilters.None, so the override is irrelevant
    var results = await lensQuery
        .ScopeOverride(QueryScope.Global, new ScopeFilterOverride { TenantId = "tenant-2" })
        .Query.ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
  }

  // === DefaultScope ===

  [Test]
  public async Task DefaultScope_WithTenantDefault_FiltersByAmbientTenantAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var mineId = await _seedOrderAsync(context, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, 200m, tenantId: "tenant-2");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"), defaultScope: QueryScope.Tenant);

    // Act
    var results = await lensQuery.DefaultScope.Query.ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Id).IsEqualTo(mineId);
  }

  [Test]
  public async Task DefaultScope_WithNullOptions_DefaultsToTenantScopeAsync() {
    // Arrange - null options must fall back to QueryScope.Tenant
    await using var context = CreateDbContext();
    var mineId = await _seedOrderAsync(context, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, 200m, tenantId: "tenant-2");
    var lensQuery = new EFCoreFilterableLensQuery<Order>(
        context, "wh_per_order", CreateScopeAccessor(tenantId: "tenant-1"), null!);

    // Act
    var results = await lensQuery.DefaultScope.Query.ToListAsync();

    // Assert - tenant filtering applied, proving the Tenant fallback
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Id).IsEqualTo(mineId);
  }

  // === Query / GetByIdAsync empty-filter delegation ===

  [Test]
  public async Task Query_WithoutApplyFilter_DelegatesToDefaultScopeAsync() {
    // Arrange - no ApplyFilter call, so _filterInfo is empty
    await using var context = CreateDbContext();
    var mineId = await _seedOrderAsync(context, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, 200m, tenantId: "tenant-2");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    // Act
    var results = await lensQuery.Query.ToListAsync();

    // Assert - default Tenant scope filtering was applied
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Id).IsEqualTo(mineId);
  }

  [Test]
  public async Task GetByIdAsync_WithoutApplyFilter_DelegatesToDefaultScopeAsync() {
    // Arrange - no ApplyFilter call, so _filterInfo is empty
    await using var context = CreateDbContext();
    var mineId = await _seedOrderAsync(context, 100m, tenantId: "tenant-1");
    var otherTenantId = await _seedOrderAsync(context, 200m, tenantId: "tenant-2");
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1"));

    // Act
    var visible = await lensQuery.GetByIdAsync(mineId);
    var hidden = await lensQuery.GetByIdAsync(otherTenantId);

    // Assert - default Tenant scope applies to both lookups
    await Assert.That(visible).IsNotNull();
    await Assert.That(visible!.Amount).IsEqualTo(100m);
    await Assert.That(hidden).IsNull();
  }

  // === Constructor Guards ===

  [Test]
  public async Task Constructor_WithNullScopeContextAccessor_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    await using var context = CreateDbContext();

    // Act & Assert
    await Assert.That(() => new EFCoreFilterableLensQuery<Order>(context, "wh_per_order", null!, CreateOptions()))
        .Throws<ArgumentNullException>();
  }
}

/// <summary>
/// Unit tests for the Split-mode branches of EFCoreFilterableLensQuery.
/// When a hydrator is registered for PerspectiveRow&lt;TModel&gt;, the Query paths must
/// use tracking queries (AsQueryable + EnsureHooked) instead of AsNoTracking so the
/// ChangeTracker hydrator can populate physical fields.
/// Uses a dedicated model type so the process-global hydrator registration cannot
/// affect any other test.
/// </summary>
[Category("Unit")]
public class EFCoreFilterableLensQuerySplitModeTests {
  private readonly Uuid7IdProvider _idProvider = new();

  private sealed record SplitLensItem {
    public string Name { get; init; } = "";
  }

  private sealed class SplitLensDbContext(DbContextOptions<SplitLensDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<SplitLensItem>>(entity => {
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

  private static void RegisterNoOpHydrator() {
    // Idempotent; keeps the row tracked so tests can observe the tracking behavior.
    SplitModeChangeTrackerHydrator.Register(typeof(PerspectiveRow<SplitLensItem>), static _ => { });
  }

  private SplitLensDbContext CreateInMemoryDbContext() {
    var options = new DbContextOptionsBuilder<SplitLensDbContext>()
        .UseInMemoryDatabase(databaseName: _idProvider.NewGuid().ToString())
        .Options;
    return new SplitLensDbContext(options);
  }

  private async Task SeedAsync(DbContext context, string name, string? tenantId) {
    var row = new PerspectiveRow<SplitLensItem> {
      Id = _idProvider.NewGuid(),
      Data = new SplitLensItem { Name = name },
      Metadata = new PerspectiveMetadata {
        EventType = "Created",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope { TenantId = tenantId },
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };
    context.Set<PerspectiveRow<SplitLensItem>>().Add(row);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();
  }

  [Test]
  public async Task Query_WithFilterAndSplitModeModel_UsesTrackingQueryAsync() {
    // Arrange
    RegisterNoOpHydrator();
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, "mine", tenantId: "tenant-1");
    await SeedAsync(context, "other", tenantId: "tenant-2");

    var lensQuery = new EFCoreFilterableLensQuery<SplitLensItem>(context, "split_lens_items");
    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilters.Tenant,
      TenantId = "tenant-1",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var results = await lensQuery.Query.ToListAsync();

    // Assert - filtered result AND tracked entity (split mode skips AsNoTracking)
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("mine");
    var trackedEntries = context.ChangeTracker.Entries<PerspectiveRow<SplitLensItem>>().ToList();
    await Assert.That(trackedEntries).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Scope_Global_WithSplitModeModel_UsesTrackingQueryAsync() {
    // Arrange
    RegisterNoOpHydrator();
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, "a", tenantId: "tenant-1");
    await SeedAsync(context, "b", tenantId: "tenant-2");

    var lensQuery = new EFCoreFilterableLensQuery<SplitLensItem>(context, "split_lens_items");

    // Act - UnfilteredScopedAccess split-mode branch
    var results = await lensQuery.Scope(QueryScope.Global).Query.ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    var trackedEntries = context.ChangeTracker.Entries<PerspectiveRow<SplitLensItem>>().ToList();
    await Assert.That(trackedEntries).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Scope_Tenant_WithSplitModeModel_UsesTrackingQueryAsync() {
    // Arrange
    RegisterNoOpHydrator();
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, "mine", tenantId: "tenant-1");
    await SeedAsync(context, "other", tenantId: "tenant-2");

    var accessor = new FilterableLensScopeContextAccessor {
      Current = new FilterableLensScopeContext {
        Scope = new PerspectiveScope { TenantId = "tenant-1" }
      }
    };
    var lensQuery = new EFCoreFilterableLensQuery<SplitLensItem>(
        context,
        "split_lens_items",
        accessor,
        Options.Create(new WhizbangCoreOptions { DefaultQueryScope = QueryScope.Tenant }));

    // Act - FilteredScopedAccess split-mode branch
    var results = await lensQuery.Scope(QueryScope.Tenant).Query.ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("mine");
    var trackedEntries = context.ChangeTracker.Entries<PerspectiveRow<SplitLensItem>>().ToList();
    await Assert.That(trackedEntries).Count().IsEqualTo(1);
  }
}
