#pragma warning disable CS0618
#pragma warning disable WHIZ400

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests for MultiModelScopedAccess (2-10 type parameters) and MultiModelScopeHelper.
/// Tests all branches: Query dispatching per type, GetByIdAsync dispatching,
/// invalid type throws, Global scope (null filter), non-Global scope with filter,
/// and scope context null throws.
/// </summary>
[Category("Unit")]
[Category("Lenses")]
public class MultiModelScopedAccessTests {
  private readonly Uuid7IdProvider _idProvider = new();

  #region Test Models

  private sealed record M1 { public string Value { get; init; } = ""; }
  private sealed record M2 { public string Value { get; init; } = ""; }
  private sealed record M3 { public string Value { get; init; } = ""; }
  private sealed record M4 { public string Value { get; init; } = ""; }
  private sealed record M5 { public string Value { get; init; } = ""; }
  private sealed record M6 { public string Value { get; init; } = ""; }
  private sealed record M7 { public string Value { get; init; } = ""; }
  private sealed record M8 { public string Value { get; init; } = ""; }
  private sealed record M9 { public string Value { get; init; } = ""; }
  private sealed record M10 { public string Value { get; init; } = ""; }
  private sealed record MInvalid { public string Value { get; init; } = ""; }

  #endregion

  #region Test DbContext

  private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      ConfigurePerspectiveRow<M1>(modelBuilder);
      ConfigurePerspectiveRow<M2>(modelBuilder);
      ConfigurePerspectiveRow<M3>(modelBuilder);
      ConfigurePerspectiveRow<M4>(modelBuilder);
      ConfigurePerspectiveRow<M5>(modelBuilder);
      ConfigurePerspectiveRow<M6>(modelBuilder);
      ConfigurePerspectiveRow<M7>(modelBuilder);
      ConfigurePerspectiveRow<M8>(modelBuilder);
      ConfigurePerspectiveRow<M9>(modelBuilder);
      ConfigurePerspectiveRow<M10>(modelBuilder);
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

  private TestDbContext CreateInMemoryDbContext() {
    var options = new DbContextOptionsBuilder<TestDbContext>()
        .UseInMemoryDatabase(databaseName: _idProvider.NewGuid().ToString())
        .Options;
    return new TestDbContext(options);
  }

  private static TestScopeContextAccessor CreateGlobalAccessor() {
    // Global scope does not need a scope context (filters = None)
    return new TestScopeContextAccessor();
  }

  private static TestScopeContextAccessor CreateTenantAccessor(string tenantId = "tenant-1") {
    return new TestScopeContextAccessor {
      Current = new TestScopeContext {
        Scope = new PerspectiveScope { TenantId = tenantId }
      }
    };
  }

  private static TestScopeContextAccessor CreateNullContextAccessor() {
    return new TestScopeContextAccessor { Current = null };
  }

  private async Task SeedAsync<T>(DbContext context, Guid id, T data, string? tenantId = null)
      where T : class {
    var row = new PerspectiveRow<T> {
      Id = id,
      Data = data,
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
    context.Set<PerspectiveRow<T>>().Add(row);
    await context.SaveChangesAsync();
  }

  #endregion

  // ===== MultiModelScopeHelper.BuildFilterInfo =====

  #region MultiModelScopeHelper.BuildFilterInfo Tests

  [Test]
  public async Task BuildFilterInfo_Global_ReturnsNullAsync() {
    var accessor = CreateGlobalAccessor();
    var result = MultiModelScopeHelper.BuildFilterInfo(QueryScope.Global, accessor, null);
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task BuildFilterInfo_Tenant_WithContext_ReturnsScopeFilterInfoAsync() {
    var accessor = CreateTenantAccessor("tenant-42");
    var result = MultiModelScopeHelper.BuildFilterInfo(QueryScope.Tenant, accessor, null);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.TenantId).IsEqualTo("tenant-42");
  }

  [Test]
  public async Task BuildFilterInfo_NonGlobal_NullContext_ThrowsInvalidOperationExceptionAsync() {
    var accessor = CreateNullContextAccessor();
    await Assert.That(() => MultiModelScopeHelper.BuildFilterInfo(QueryScope.Tenant, accessor, null))
        .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task BuildFilterInfo_WithOverride_AppliesOverrideValuesAsync() {
    var accessor = CreateTenantAccessor("original-tenant");
    var overrides = new ScopeFilterOverride { TenantId = "overridden-tenant" };
    var result = MultiModelScopeHelper.BuildFilterInfo(QueryScope.Tenant, accessor, overrides);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.TenantId).IsEqualTo("overridden-tenant");
  }

  [Test]
  public async Task BuildFilterInfo_WithNullOverride_UsesOriginalContextAsync() {
    var accessor = CreateTenantAccessor("original-tenant");
    var result = MultiModelScopeHelper.BuildFilterInfo(QueryScope.Tenant, accessor, null);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.TenantId).IsEqualTo("original-tenant");
  }

  #endregion

  // ===== MultiModelScopeHelper.GetQuery =====

  #region MultiModelScopeHelper.GetQuery Tests

  [Test]
  public async Task GetQuery_NullFilter_ReturnsUnfilteredQueryAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "test" });

    var query = MultiModelScopeHelper.GetQuery<M1>(context, null);
    var results = await query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
  }

  [Test]
  public async Task GetQuery_EmptyFilter_ReturnsUnfilteredQueryAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "test" });

    var filterInfo = new ScopeFilterInfo { Filters = ScopeFilters.None };
    var query = MultiModelScopeHelper.GetQuery<M1>(context, filterInfo);
    var results = await query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
  }

  [Test]
  public async Task GetQuery_WithFilter_AppliesFilterAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new M1 { Value = "a" }, tenantId: "t1");
    await SeedAsync(context, _idProvider.NewGuid(), new M1 { Value = "b" }, tenantId: "t2");

    var filterInfo = new ScopeFilterInfo {
      Filters = ScopeFilters.Tenant,
      TenantId = "t1",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    };
    var query = MultiModelScopeHelper.GetQuery<M1>(context, filterInfo);
    var results = await query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Value).IsEqualTo("a");
  }

  #endregion

  // ===== MultiModelScopeHelper.GetByIdAsync =====

  #region MultiModelScopeHelper.GetByIdAsync Tests

  [Test]
  public async Task GetByIdAsync_ExistingId_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "found" });

    var result = await MultiModelScopeHelper.GetByIdAsync<M1>(context, null, id, CancellationToken.None);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo("found");
  }

  [Test]
  public async Task GetByIdAsync_NonExistingId_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();

    var result = await MultiModelScopeHelper.GetByIdAsync<M1>(context, null, Guid.NewGuid(), CancellationToken.None);

    await Assert.That(result).IsNull();
  }

  #endregion

  // ===== 2-Model MultiModelScopedAccess =====

  #region 2-Model Tests

  [Test]
  public async Task TwoModel_Query_T1_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var query = sut.Query<M1>();
    await Assert.That(query).IsNotNull();
  }

  [Test]
  public async Task TwoModel_Query_T2_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var query = sut.Query<M2>();
    await Assert.That(query).IsNotNull();
  }

  [Test]
  public async Task TwoModel_Query_InvalidType_ThrowsArgumentExceptionAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(() => sut.Query<MInvalid>()).Throws<ArgumentException>();
  }

  [Test]
  public async Task TwoModel_GetByIdAsync_T1_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "m1" });
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Global, CreateGlobalAccessor(), null);

    var result = await sut.GetByIdAsync<M1>(id);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo("m1");
  }

  [Test]
  public async Task TwoModel_GetByIdAsync_T2_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M2 { Value = "m2" });
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Global, CreateGlobalAccessor(), null);

    var result = await sut.GetByIdAsync<M2>(id);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo("m2");
  }

  [Test]
  public async Task TwoModel_GetByIdAsync_NotFound_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Global, CreateGlobalAccessor(), null);

    var result = await sut.GetByIdAsync<M1>(Guid.NewGuid());
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task TwoModel_GetByIdAsync_InvalidType_ThrowsArgumentExceptionAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(async () => await sut.GetByIdAsync<MInvalid>(Guid.NewGuid())).Throws<ArgumentException>();
  }

  [Test]
  public async Task TwoModel_NonGlobalScope_NullContext_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await Assert.That(() => new MultiModelScopedAccess<M1, M2>(
        context, QueryScope.Tenant, CreateNullContextAccessor(), null))
        .Throws<InvalidOperationException>();
  }

  #endregion

  // ===== 3-Model MultiModelScopedAccess =====

  #region 3-Model Tests

  [Test]
  public async Task ThreeModel_Query_T1_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M1>()).IsNotNull();
  }

  [Test]
  public async Task ThreeModel_Query_T2_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M2>()).IsNotNull();
  }

  [Test]
  public async Task ThreeModel_Query_T3_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M3>()).IsNotNull();
  }

  [Test]
  public async Task ThreeModel_Query_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(() => sut.Query<MInvalid>()).Throws<ArgumentException>();
  }

  [Test]
  public async Task ThreeModel_GetByIdAsync_T1_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "v1" });
    var sut = new MultiModelScopedAccess<M1, M2, M3>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M1>(id);
    await Assert.That(result!.Value).IsEqualTo("v1");
  }

  [Test]
  public async Task ThreeModel_GetByIdAsync_T2_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M2 { Value = "v2" });
    var sut = new MultiModelScopedAccess<M1, M2, M3>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M2>(id);
    await Assert.That(result!.Value).IsEqualTo("v2");
  }

  [Test]
  public async Task ThreeModel_GetByIdAsync_T3_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M3 { Value = "v3" });
    var sut = new MultiModelScopedAccess<M1, M2, M3>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M3>(id);
    await Assert.That(result!.Value).IsEqualTo("v3");
  }

  [Test]
  public async Task ThreeModel_GetByIdAsync_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(async () => await sut.GetByIdAsync<MInvalid>(Guid.NewGuid())).Throws<ArgumentException>();
  }

  #endregion

  // ===== 4-Model MultiModelScopedAccess =====

  #region 4-Model Tests

  [Test]
  public async Task FourModel_Query_T1_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M1>()).IsNotNull();
  }

  [Test]
  public async Task FourModel_Query_T2_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M2>()).IsNotNull();
  }

  [Test]
  public async Task FourModel_Query_T3_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M3>()).IsNotNull();
  }

  [Test]
  public async Task FourModel_Query_T4_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M4>()).IsNotNull();
  }

  [Test]
  public async Task FourModel_Query_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(() => sut.Query<MInvalid>()).Throws<ArgumentException>();
  }

  [Test]
  public async Task FourModel_GetByIdAsync_T1_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "v1" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M1>(id);
    await Assert.That(result!.Value).IsEqualTo("v1");
  }

  [Test]
  public async Task FourModel_GetByIdAsync_T2_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M2 { Value = "v2" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M2>(id);
    await Assert.That(result!.Value).IsEqualTo("v2");
  }

  [Test]
  public async Task FourModel_GetByIdAsync_T3_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M3 { Value = "v3" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M3>(id);
    await Assert.That(result!.Value).IsEqualTo("v3");
  }

  [Test]
  public async Task FourModel_GetByIdAsync_T4_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M4 { Value = "v4" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M4>(id);
    await Assert.That(result!.Value).IsEqualTo("v4");
  }

  [Test]
  public async Task FourModel_GetByIdAsync_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(async () => await sut.GetByIdAsync<MInvalid>(Guid.NewGuid())).Throws<ArgumentException>();
  }

  #endregion

  // ===== 5-Model MultiModelScopedAccess =====

  #region 5-Model Tests

  [Test]
  public async Task FiveModel_Query_T1_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M1>()).IsNotNull();
  }

  [Test]
  public async Task FiveModel_Query_T2_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M2>()).IsNotNull();
  }

  [Test]
  public async Task FiveModel_Query_T3_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M3>()).IsNotNull();
  }

  [Test]
  public async Task FiveModel_Query_T4_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M4>()).IsNotNull();
  }

  [Test]
  public async Task FiveModel_Query_T5_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M5>()).IsNotNull();
  }

  [Test]
  public async Task FiveModel_Query_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(() => sut.Query<MInvalid>()).Throws<ArgumentException>();
  }

  [Test]
  public async Task FiveModel_GetByIdAsync_T1_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "v1" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M1>(id);
    await Assert.That(result!.Value).IsEqualTo("v1");
  }

  [Test]
  public async Task FiveModel_GetByIdAsync_T2_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M2 { Value = "v2" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M2>(id);
    await Assert.That(result!.Value).IsEqualTo("v2");
  }

  [Test]
  public async Task FiveModel_GetByIdAsync_T3_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M3 { Value = "v3" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M3>(id);
    await Assert.That(result!.Value).IsEqualTo("v3");
  }

  [Test]
  public async Task FiveModel_GetByIdAsync_T4_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M4 { Value = "v4" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M4>(id);
    await Assert.That(result!.Value).IsEqualTo("v4");
  }

  [Test]
  public async Task FiveModel_GetByIdAsync_T5_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M5 { Value = "v5" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M5>(id);
    await Assert.That(result!.Value).IsEqualTo("v5");
  }

  [Test]
  public async Task FiveModel_GetByIdAsync_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(async () => await sut.GetByIdAsync<MInvalid>(Guid.NewGuid())).Throws<ArgumentException>();
  }

  #endregion

  // ===== 6-Model MultiModelScopedAccess =====

  #region 6-Model Tests

  [Test]
  public async Task SixModel_Query_T1_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M1>()).IsNotNull();
  }

  [Test]
  public async Task SixModel_Query_T2_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M2>()).IsNotNull();
  }

  [Test]
  public async Task SixModel_Query_T3_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M3>()).IsNotNull();
  }

  [Test]
  public async Task SixModel_Query_T4_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M4>()).IsNotNull();
  }

  [Test]
  public async Task SixModel_Query_T5_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M5>()).IsNotNull();
  }

  [Test]
  public async Task SixModel_Query_T6_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M6>()).IsNotNull();
  }

  [Test]
  public async Task SixModel_Query_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(() => sut.Query<MInvalid>()).Throws<ArgumentException>();
  }

  [Test]
  public async Task SixModel_GetByIdAsync_T1_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "v1" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M1>(id);
    await Assert.That(result!.Value).IsEqualTo("v1");
  }

  [Test]
  public async Task SixModel_GetByIdAsync_T2_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M2 { Value = "v2" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M2>(id);
    await Assert.That(result!.Value).IsEqualTo("v2");
  }

  [Test]
  public async Task SixModel_GetByIdAsync_T3_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M3 { Value = "v3" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M3>(id);
    await Assert.That(result!.Value).IsEqualTo("v3");
  }

  [Test]
  public async Task SixModel_GetByIdAsync_T4_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M4 { Value = "v4" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M4>(id);
    await Assert.That(result!.Value).IsEqualTo("v4");
  }

  [Test]
  public async Task SixModel_GetByIdAsync_T5_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M5 { Value = "v5" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M5>(id);
    await Assert.That(result!.Value).IsEqualTo("v5");
  }

  [Test]
  public async Task SixModel_GetByIdAsync_T6_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M6 { Value = "v6" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M6>(id);
    await Assert.That(result!.Value).IsEqualTo("v6");
  }

  [Test]
  public async Task SixModel_GetByIdAsync_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(async () => await sut.GetByIdAsync<MInvalid>(Guid.NewGuid())).Throws<ArgumentException>();
  }

  #endregion

  // ===== 7-Model MultiModelScopedAccess =====

  #region 7-Model Tests

  [Test]
  public async Task SevenModel_Query_T1_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M1>()).IsNotNull();
  }

  [Test]
  public async Task SevenModel_Query_T2_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M2>()).IsNotNull();
  }

  [Test]
  public async Task SevenModel_Query_T3_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M3>()).IsNotNull();
  }

  [Test]
  public async Task SevenModel_Query_T4_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M4>()).IsNotNull();
  }

  [Test]
  public async Task SevenModel_Query_T5_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M5>()).IsNotNull();
  }

  [Test]
  public async Task SevenModel_Query_T6_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M6>()).IsNotNull();
  }

  [Test]
  public async Task SevenModel_Query_T7_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M7>()).IsNotNull();
  }

  [Test]
  public async Task SevenModel_Query_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(() => sut.Query<MInvalid>()).Throws<ArgumentException>();
  }

  [Test]
  public async Task SevenModel_GetByIdAsync_T1_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "v1" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M1>(id);
    await Assert.That(result!.Value).IsEqualTo("v1");
  }

  [Test]
  public async Task SevenModel_GetByIdAsync_T2_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M2 { Value = "v2" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M2>(id);
    await Assert.That(result!.Value).IsEqualTo("v2");
  }

  [Test]
  public async Task SevenModel_GetByIdAsync_T3_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M3 { Value = "v3" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M3>(id);
    await Assert.That(result!.Value).IsEqualTo("v3");
  }

  [Test]
  public async Task SevenModel_GetByIdAsync_T4_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M4 { Value = "v4" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M4>(id);
    await Assert.That(result!.Value).IsEqualTo("v4");
  }

  [Test]
  public async Task SevenModel_GetByIdAsync_T5_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M5 { Value = "v5" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M5>(id);
    await Assert.That(result!.Value).IsEqualTo("v5");
  }

  [Test]
  public async Task SevenModel_GetByIdAsync_T6_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M6 { Value = "v6" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M6>(id);
    await Assert.That(result!.Value).IsEqualTo("v6");
  }

  [Test]
  public async Task SevenModel_GetByIdAsync_T7_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M7 { Value = "v7" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M7>(id);
    await Assert.That(result!.Value).IsEqualTo("v7");
  }

  [Test]
  public async Task SevenModel_GetByIdAsync_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(async () => await sut.GetByIdAsync<MInvalid>(Guid.NewGuid())).Throws<ArgumentException>();
  }

  #endregion

  // ===== 8-Model MultiModelScopedAccess =====

  #region 8-Model Tests

  [Test]
  public async Task EightModel_Query_T1_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M1>()).IsNotNull();
  }

  [Test]
  public async Task EightModel_Query_T2_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M2>()).IsNotNull();
  }

  [Test]
  public async Task EightModel_Query_T3_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M3>()).IsNotNull();
  }

  [Test]
  public async Task EightModel_Query_T4_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M4>()).IsNotNull();
  }

  [Test]
  public async Task EightModel_Query_T5_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M5>()).IsNotNull();
  }

  [Test]
  public async Task EightModel_Query_T6_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M6>()).IsNotNull();
  }

  [Test]
  public async Task EightModel_Query_T7_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M7>()).IsNotNull();
  }

  [Test]
  public async Task EightModel_Query_T8_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M8>()).IsNotNull();
  }

  [Test]
  public async Task EightModel_Query_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(() => sut.Query<MInvalid>()).Throws<ArgumentException>();
  }

  [Test]
  public async Task EightModel_GetByIdAsync_T1_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "v1" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M1>(id);
    await Assert.That(result!.Value).IsEqualTo("v1");
  }

  [Test]
  public async Task EightModel_GetByIdAsync_T2_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M2 { Value = "v2" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M2>(id);
    await Assert.That(result!.Value).IsEqualTo("v2");
  }

  [Test]
  public async Task EightModel_GetByIdAsync_T3_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M3 { Value = "v3" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M3>(id);
    await Assert.That(result!.Value).IsEqualTo("v3");
  }

  [Test]
  public async Task EightModel_GetByIdAsync_T4_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M4 { Value = "v4" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M4>(id);
    await Assert.That(result!.Value).IsEqualTo("v4");
  }

  [Test]
  public async Task EightModel_GetByIdAsync_T5_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M5 { Value = "v5" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M5>(id);
    await Assert.That(result!.Value).IsEqualTo("v5");
  }

  [Test]
  public async Task EightModel_GetByIdAsync_T6_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M6 { Value = "v6" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M6>(id);
    await Assert.That(result!.Value).IsEqualTo("v6");
  }

  [Test]
  public async Task EightModel_GetByIdAsync_T7_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M7 { Value = "v7" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M7>(id);
    await Assert.That(result!.Value).IsEqualTo("v7");
  }

  [Test]
  public async Task EightModel_GetByIdAsync_T8_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M8 { Value = "v8" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M8>(id);
    await Assert.That(result!.Value).IsEqualTo("v8");
  }

  [Test]
  public async Task EightModel_GetByIdAsync_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(async () => await sut.GetByIdAsync<MInvalid>(Guid.NewGuid())).Throws<ArgumentException>();
  }

  #endregion

  // ===== 9-Model MultiModelScopedAccess =====

  #region 9-Model Tests

  [Test]
  public async Task NineModel_Query_T1_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M1>()).IsNotNull();
  }

  [Test]
  public async Task NineModel_Query_T2_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M2>()).IsNotNull();
  }

  [Test]
  public async Task NineModel_Query_T3_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M3>()).IsNotNull();
  }

  [Test]
  public async Task NineModel_Query_T4_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M4>()).IsNotNull();
  }

  [Test]
  public async Task NineModel_Query_T5_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M5>()).IsNotNull();
  }

  [Test]
  public async Task NineModel_Query_T6_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M6>()).IsNotNull();
  }

  [Test]
  public async Task NineModel_Query_T7_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M7>()).IsNotNull();
  }

  [Test]
  public async Task NineModel_Query_T8_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M8>()).IsNotNull();
  }

  [Test]
  public async Task NineModel_Query_T9_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M9>()).IsNotNull();
  }

  [Test]
  public async Task NineModel_Query_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(() => sut.Query<MInvalid>()).Throws<ArgumentException>();
  }

  [Test]
  public async Task NineModel_GetByIdAsync_T1_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "v1" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M1>(id);
    await Assert.That(result!.Value).IsEqualTo("v1");
  }

  [Test]
  public async Task NineModel_GetByIdAsync_T2_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M2 { Value = "v2" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M2>(id);
    await Assert.That(result!.Value).IsEqualTo("v2");
  }

  [Test]
  public async Task NineModel_GetByIdAsync_T3_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M3 { Value = "v3" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M3>(id);
    await Assert.That(result!.Value).IsEqualTo("v3");
  }

  [Test]
  public async Task NineModel_GetByIdAsync_T4_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M4 { Value = "v4" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M4>(id);
    await Assert.That(result!.Value).IsEqualTo("v4");
  }

  [Test]
  public async Task NineModel_GetByIdAsync_T5_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M5 { Value = "v5" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M5>(id);
    await Assert.That(result!.Value).IsEqualTo("v5");
  }

  [Test]
  public async Task NineModel_GetByIdAsync_T6_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M6 { Value = "v6" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M6>(id);
    await Assert.That(result!.Value).IsEqualTo("v6");
  }

  [Test]
  public async Task NineModel_GetByIdAsync_T7_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M7 { Value = "v7" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M7>(id);
    await Assert.That(result!.Value).IsEqualTo("v7");
  }

  [Test]
  public async Task NineModel_GetByIdAsync_T8_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M8 { Value = "v8" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M8>(id);
    await Assert.That(result!.Value).IsEqualTo("v8");
  }

  [Test]
  public async Task NineModel_GetByIdAsync_T9_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M9 { Value = "v9" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M9>(id);
    await Assert.That(result!.Value).IsEqualTo("v9");
  }

  [Test]
  public async Task NineModel_GetByIdAsync_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(async () => await sut.GetByIdAsync<MInvalid>(Guid.NewGuid())).Throws<ArgumentException>();
  }

  #endregion

  // ===== 10-Model MultiModelScopedAccess =====

  #region 10-Model Tests

  [Test]
  public async Task TenModel_Query_T1_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M1>()).IsNotNull();
  }

  [Test]
  public async Task TenModel_Query_T2_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M2>()).IsNotNull();
  }

  [Test]
  public async Task TenModel_Query_T3_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M3>()).IsNotNull();
  }

  [Test]
  public async Task TenModel_Query_T4_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M4>()).IsNotNull();
  }

  [Test]
  public async Task TenModel_Query_T5_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M5>()).IsNotNull();
  }

  [Test]
  public async Task TenModel_Query_T6_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M6>()).IsNotNull();
  }

  [Test]
  public async Task TenModel_Query_T7_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M7>()).IsNotNull();
  }

  [Test]
  public async Task TenModel_Query_T8_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M8>()).IsNotNull();
  }

  [Test]
  public async Task TenModel_Query_T9_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M9>()).IsNotNull();
  }

  [Test]
  public async Task TenModel_Query_T10_ReturnsQueryableAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut.Query<M10>()).IsNotNull();
  }

  [Test]
  public async Task TenModel_Query_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(() => sut.Query<MInvalid>()).Throws<ArgumentException>();
  }

  [Test]
  public async Task TenModel_GetByIdAsync_T1_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "v1" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M1>(id);
    await Assert.That(result!.Value).IsEqualTo("v1");
  }

  [Test]
  public async Task TenModel_GetByIdAsync_T2_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M2 { Value = "v2" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M2>(id);
    await Assert.That(result!.Value).IsEqualTo("v2");
  }

  [Test]
  public async Task TenModel_GetByIdAsync_T3_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M3 { Value = "v3" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M3>(id);
    await Assert.That(result!.Value).IsEqualTo("v3");
  }

  [Test]
  public async Task TenModel_GetByIdAsync_T4_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M4 { Value = "v4" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M4>(id);
    await Assert.That(result!.Value).IsEqualTo("v4");
  }

  [Test]
  public async Task TenModel_GetByIdAsync_T5_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M5 { Value = "v5" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M5>(id);
    await Assert.That(result!.Value).IsEqualTo("v5");
  }

  [Test]
  public async Task TenModel_GetByIdAsync_T6_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M6 { Value = "v6" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M6>(id);
    await Assert.That(result!.Value).IsEqualTo("v6");
  }

  [Test]
  public async Task TenModel_GetByIdAsync_T7_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M7 { Value = "v7" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M7>(id);
    await Assert.That(result!.Value).IsEqualTo("v7");
  }

  [Test]
  public async Task TenModel_GetByIdAsync_T8_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M8 { Value = "v8" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M8>(id);
    await Assert.That(result!.Value).IsEqualTo("v8");
  }

  [Test]
  public async Task TenModel_GetByIdAsync_T9_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M9 { Value = "v9" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M9>(id);
    await Assert.That(result!.Value).IsEqualTo("v9");
  }

  [Test]
  public async Task TenModel_GetByIdAsync_T10_ReturnsDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M10 { Value = "v10" });
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    var result = await sut.GetByIdAsync<M10>(id);
    await Assert.That(result!.Value).IsEqualTo("v10");
  }

  [Test]
  public async Task TenModel_GetByIdAsync_InvalidType_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(async () => await sut.GetByIdAsync<MInvalid>(Guid.NewGuid())).Throws<ArgumentException>();
  }

  #endregion

  // ===== Cross-Cutting: Scope Filtering & Construction Error Paths =====

  #region Scope Filtering Tests

  [Test]
  public async Task ThreeModel_NonGlobalScope_NullContext_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await Assert.That(() => new MultiModelScopedAccess<M1, M2, M3>(
        context, QueryScope.Tenant, CreateNullContextAccessor(), null))
        .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task FourModel_NonGlobalScope_NullContext_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await Assert.That(() => new MultiModelScopedAccess<M1, M2, M3, M4>(
        context, QueryScope.Tenant, CreateNullContextAccessor(), null))
        .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task FiveModel_NonGlobalScope_NullContext_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await Assert.That(() => new MultiModelScopedAccess<M1, M2, M3, M4, M5>(
        context, QueryScope.Tenant, CreateNullContextAccessor(), null))
        .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task SixModel_NonGlobalScope_NullContext_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await Assert.That(() => new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(
        context, QueryScope.Tenant, CreateNullContextAccessor(), null))
        .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task SevenModel_NonGlobalScope_NullContext_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await Assert.That(() => new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(
        context, QueryScope.Tenant, CreateNullContextAccessor(), null))
        .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task EightModel_NonGlobalScope_NullContext_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await Assert.That(() => new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(
        context, QueryScope.Tenant, CreateNullContextAccessor(), null))
        .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task NineModel_NonGlobalScope_NullContext_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await Assert.That(() => new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(
        context, QueryScope.Tenant, CreateNullContextAccessor(), null))
        .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task TenModel_NonGlobalScope_NullContext_ThrowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await Assert.That(() => new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(
        context, QueryScope.Tenant, CreateNullContextAccessor(), null))
        .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task TwoModel_TenantScope_WithContext_FiltersCorrectlyAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new M1 { Value = "t1" }, tenantId: "tenant-A");
    await SeedAsync(context, _idProvider.NewGuid(), new M1 { Value = "t2" }, tenantId: "tenant-B");

    var accessor = CreateTenantAccessor("tenant-A");
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Tenant, accessor, null);

    var results = await sut.Query<M1>().ToListAsync();
    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Value).IsEqualTo("t1");
  }

  [Test]
  public async Task TwoModel_TenantScope_WithOverride_FiltersWithOverrideAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new M1 { Value = "t1" }, tenantId: "tenant-A");
    await SeedAsync(context, _idProvider.NewGuid(), new M1 { Value = "t2" }, tenantId: "tenant-B");

    var accessor = CreateTenantAccessor("tenant-A");
    var overrides = new ScopeFilterOverride { TenantId = "tenant-B" };
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Tenant, accessor, overrides);

    var results = await sut.Query<M1>().ToListAsync();
    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Value).IsEqualTo("t2");
  }

  [Test]
  public async Task TwoModel_GetByIdAsync_WithTenantScope_ReturnsFilteredResultAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "found" }, tenantId: "tenant-A");

    var accessor = CreateTenantAccessor("tenant-A");
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Tenant, accessor, null);

    var result = await sut.GetByIdAsync<M1>(id);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo("found");
  }

  [Test]
  public async Task TwoModel_GetByIdAsync_WithTenantScope_WrongTenant_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "hidden" }, tenantId: "tenant-A");

    var accessor = CreateTenantAccessor("tenant-B");
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Tenant, accessor, null);

    var result = await sut.GetByIdAsync<M1>(id);
    await Assert.That(result).IsNull();
  }

  #endregion

  // ===== Interface Implementation Verification =====

  #region Interface Verification

  [Test]
  public async Task TwoModel_ImplementsIScopedMultiLensAccessAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut).IsAssignableTo<IScopedMultiLensAccess<M1, M2>>();
  }

  [Test]
  public async Task ThreeModel_ImplementsIScopedMultiLensAccessAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut).IsAssignableTo<IScopedMultiLensAccess<M1, M2, M3>>();
  }

  [Test]
  public async Task FourModel_ImplementsIScopedMultiLensAccessAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut).IsAssignableTo<IScopedMultiLensAccess<M1, M2, M3, M4>>();
  }

  [Test]
  public async Task FiveModel_ImplementsIScopedMultiLensAccessAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut).IsAssignableTo<IScopedMultiLensAccess<M1, M2, M3, M4, M5>>();
  }

  [Test]
  public async Task SixModel_ImplementsIScopedMultiLensAccessAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut).IsAssignableTo<IScopedMultiLensAccess<M1, M2, M3, M4, M5, M6>>();
  }

  [Test]
  public async Task SevenModel_ImplementsIScopedMultiLensAccessAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut).IsAssignableTo<IScopedMultiLensAccess<M1, M2, M3, M4, M5, M6, M7>>();
  }

  [Test]
  public async Task EightModel_ImplementsIScopedMultiLensAccessAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut).IsAssignableTo<IScopedMultiLensAccess<M1, M2, M3, M4, M5, M6, M7, M8>>();
  }

  [Test]
  public async Task NineModel_ImplementsIScopedMultiLensAccessAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut).IsAssignableTo<IScopedMultiLensAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9>>();
  }

  [Test]
  public async Task TenModel_ImplementsIScopedMultiLensAccessAsync() {
    await using var context = CreateInMemoryDbContext();
    var sut = new MultiModelScopedAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>(context, QueryScope.Global, CreateGlobalAccessor(), null);
    await Assert.That(sut).IsAssignableTo<IScopedMultiLensAccess<M1, M2, M3, M4, M5, M6, M7, M8, M9, M10>>();
  }

  #endregion

  // ===== Query returning actual data across all types =====

  #region Query Data Verification

  [Test]
  public async Task TwoModel_Query_T1_ReturnsSeededDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M1 { Value = "seeded" });
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Global, CreateGlobalAccessor(), null);

    var results = await sut.Query<M1>().ToListAsync();
    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Value).IsEqualTo("seeded");
  }

  [Test]
  public async Task TwoModel_Query_T2_ReturnsSeededDataAsync() {
    await using var context = CreateInMemoryDbContext();
    var id = _idProvider.NewGuid();
    await SeedAsync(context, id, new M2 { Value = "seeded-m2" });
    var sut = new MultiModelScopedAccess<M1, M2>(context, QueryScope.Global, CreateGlobalAccessor(), null);

    var results = await sut.Query<M2>().ToListAsync();
    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Value).IsEqualTo("seeded-m2");
  }

  #endregion

  // ===== Additional scope types on BuildFilterInfo =====

  #region Additional QueryScope types

  [Test]
  public async Task BuildFilterInfo_Organization_ReturnsFilterInfoAsync() {
    var accessor = new TestScopeContextAccessor {
      Current = new TestScopeContext {
        Scope = new PerspectiveScope { TenantId = "t1", OrganizationId = "org-1" }
      }
    };
    var result = MultiModelScopeHelper.BuildFilterInfo(QueryScope.Organization, accessor, null);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.TenantId).IsEqualTo("t1");
    await Assert.That(result!.Value.OrganizationId).IsEqualTo("org-1");
  }

  [Test]
  public async Task BuildFilterInfo_Customer_ReturnsFilterInfoAsync() {
    var accessor = new TestScopeContextAccessor {
      Current = new TestScopeContext {
        Scope = new PerspectiveScope { TenantId = "t1", CustomerId = "cust-1" }
      }
    };
    var result = MultiModelScopeHelper.BuildFilterInfo(QueryScope.Customer, accessor, null);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.CustomerId).IsEqualTo("cust-1");
  }

  [Test]
  public async Task BuildFilterInfo_User_ReturnsFilterInfoAsync() {
    var accessor = new TestScopeContextAccessor {
      Current = new TestScopeContext {
        Scope = new PerspectiveScope { TenantId = "t1", UserId = "user-1" }
      }
    };
    var result = MultiModelScopeHelper.BuildFilterInfo(QueryScope.User, accessor, null);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.UserId).IsEqualTo("user-1");
  }

  [Test]
  public async Task BuildFilterInfo_Principal_ReturnsFilterInfoAsync() {
    var accessor = new TestScopeContextAccessor {
      Current = new TestScopeContext {
        Scope = new PerspectiveScope { TenantId = "t1" },
        SecurityPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.Group("admins") }
      }
    };
    var result = MultiModelScopeHelper.BuildFilterInfo(QueryScope.Principal, accessor, null);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SecurityPrincipals.Count).IsGreaterThan(0);
  }

  [Test]
  public async Task BuildFilterInfo_UserOrPrincipal_ReturnsFilterInfoWithOrLogicAsync() {
    var accessor = new TestScopeContextAccessor {
      Current = new TestScopeContext {
        Scope = new PerspectiveScope { TenantId = "t1", UserId = "user-1" },
        SecurityPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.Group("team") }
      }
    };
    var result = MultiModelScopeHelper.BuildFilterInfo(QueryScope.UserOrPrincipal, accessor, null);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.UseOrLogicForUserAndPrincipal).IsTrue();
  }

  #endregion
}
