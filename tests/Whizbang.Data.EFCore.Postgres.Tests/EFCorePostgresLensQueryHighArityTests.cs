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
/// Tests for the high-arity (4 through 10 type parameters) EFCorePostgresLensQuery implementations.
/// Mirrors EFCorePostgresLensQueryMultiGenericTests (arity 2/3) and verifies for each arity:
/// Query&lt;T&gt;() returns seeded rows for every type parameter slot, GetByIdAsync hit and miss,
/// constructor null-argument guards, Dispose/DisposeAsync idempotency, and the
/// Scope/ScopeOverride/DefaultScope accessors.
/// Also closes small coverage gaps on the arity 2/3 classes (sync Dispose, GetByIdAsync).
/// </summary>
[Category("EFCore")]
[Category("Lenses")]
[Category("Unit")]
public class EFCorePostgresLensQueryHighArityTests {
  private readonly Uuid7IdProvider _idProvider = new();

  #region Test Models

  private sealed record HA1 { public string Value { get; init; } = ""; }
  private sealed record HA2 { public string Value { get; init; } = ""; }
  private sealed record HA3 { public string Value { get; init; } = ""; }
  private sealed record HA4 { public string Value { get; init; } = ""; }
  private sealed record HA5 { public string Value { get; init; } = ""; }
  private sealed record HA6 { public string Value { get; init; } = ""; }
  private sealed record HA7 { public string Value { get; init; } = ""; }
  private sealed record HA8 { public string Value { get; init; } = ""; }
  private sealed record HA9 { public string Value { get; init; } = ""; }
  private sealed record HA10 { public string Value { get; init; } = ""; }

  #endregion

  #region Test DbContext

  private sealed class HighArityDbContext(DbContextOptions<HighArityDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      ConfigurePerspectiveRow<HA1>(modelBuilder);
      ConfigurePerspectiveRow<HA2>(modelBuilder);
      ConfigurePerspectiveRow<HA3>(modelBuilder);
      ConfigurePerspectiveRow<HA4>(modelBuilder);
      ConfigurePerspectiveRow<HA5>(modelBuilder);
      ConfigurePerspectiveRow<HA6>(modelBuilder);
      ConfigurePerspectiveRow<HA7>(modelBuilder);
      ConfigurePerspectiveRow<HA8>(modelBuilder);
      ConfigurePerspectiveRow<HA9>(modelBuilder);
      ConfigurePerspectiveRow<HA10>(modelBuilder);
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

  private HighArityDbContext CreateInMemoryDbContext() {
    var options = new DbContextOptionsBuilder<HighArityDbContext>()
        .UseInMemoryDatabase(databaseName: _idProvider.NewGuid().ToString())
        .Options;
    return new HighArityDbContext(options);
  }

  private static Dictionary<Type, string> CreateTableNames() => new() {
    { typeof(HA1), "ha1" },
    { typeof(HA2), "ha2" },
    { typeof(HA3), "ha3" },
    { typeof(HA4), "ha4" },
    { typeof(HA5), "ha5" },
    { typeof(HA6), "ha6" },
    { typeof(HA7), "ha7" },
    { typeof(HA8), "ha8" },
    { typeof(HA9), "ha9" },
    { typeof(HA10), "ha10" }
  };

  private static TestScopeContextAccessor CreateTenantAccessor(string tenantId = "tenant-a") {
    return new TestScopeContextAccessor {
      Current = new TestScopeContext {
        Scope = new PerspectiveScope { TenantId = tenantId }
      }
    };
  }

  private static IOptions<WhizbangCoreOptions> CreateOptions(QueryScope defaultScope = QueryScope.Tenant) {
    return Options.Create(new WhizbangCoreOptions { DefaultQueryScope = defaultScope });
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

  // ===== 4-Generic EFCorePostgresLensQuery =====

  #region 4-Generic Tests

  [Test]
  public async Task FourGeneric_Query_EachTypeSlot_ReturnsSeededRowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "v1" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA2 { Value = "v2" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA3 { Value = "v3" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA4 { Value = "v4" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4>(context, CreateTableNames());

    var r1 = await lensQuery.Query<HA1>().ToListAsync();
    var r2 = await lensQuery.Query<HA2>().ToListAsync();
    var r3 = await lensQuery.Query<HA3>().ToListAsync();
    var r4 = await lensQuery.Query<HA4>().ToListAsync();

    await Assert.That(r1.Count).IsEqualTo(1);
    await Assert.That(r1[0].Data.Value).IsEqualTo("v1");
    await Assert.That(r2.Count).IsEqualTo(1);
    await Assert.That(r2[0].Data.Value).IsEqualTo("v2");
    await Assert.That(r3.Count).IsEqualTo(1);
    await Assert.That(r3[0].Data.Value).IsEqualTo("v3");
    await Assert.That(r4.Count).IsEqualTo(1);
    await Assert.That(r4[0].Data.Value).IsEqualTo("v4");
  }

  [Test]
  public async Task FourGeneric_GetByIdAsync_EachTypeSlot_ReturnsSeededModelAsync() {
    await using var context = CreateInMemoryDbContext();
    var id1 = _idProvider.NewGuid();
    var id2 = _idProvider.NewGuid();
    var id3 = _idProvider.NewGuid();
    var id4 = _idProvider.NewGuid();
    await SeedAsync(context, id1, new HA1 { Value = "v1" });
    await SeedAsync(context, id2, new HA2 { Value = "v2" });
    await SeedAsync(context, id3, new HA3 { Value = "v3" });
    await SeedAsync(context, id4, new HA4 { Value = "v4" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4>(context, CreateTableNames());

    await Assert.That((await lensQuery.GetByIdAsync<HA1>(id1))!.Value).IsEqualTo("v1");
    await Assert.That((await lensQuery.GetByIdAsync<HA2>(id2))!.Value).IsEqualTo("v2");
    await Assert.That((await lensQuery.GetByIdAsync<HA3>(id3))!.Value).IsEqualTo("v3");
    await Assert.That((await lensQuery.GetByIdAsync<HA4>(id4))!.Value).IsEqualTo("v4");
  }

  [Test]
  public async Task FourGeneric_GetByIdAsync_WhenNotExists_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4>(context, CreateTableNames());

    var result = await lensQuery.GetByIdAsync<HA1>(_idProvider.NewGuid());

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task FourGeneric_Constructor_WithNullDbContext_ThrowsArgumentNullExceptionAsync() {
    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4>(null!, CreateTableNames());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("dbContext");
  }

  [Test]
  public async Task FourGeneric_Constructor_WithNullTableNames_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4>(context, null!);
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("tableNames");
  }

  [Test]
  public async Task FourGeneric_Constructor_WithNullScopeContextAccessor_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4>(context, CreateTableNames(), null!, CreateOptions());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("scopeContextAccessor");
  }

  [Test]
  public async Task FourGeneric_Dispose_CalledTwice_DisposesContextOnceAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4>(context, CreateTableNames());

    lensQuery.Dispose();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(() => lensQuery.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task FourGeneric_DisposeAsync_CalledTwice_DoesNotThrowAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4>(context, CreateTableNames());

    await lensQuery.DisposeAsync();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(async () => await lensQuery.DisposeAsync()).ThrowsNothing();
  }

  [Test]
  public async Task FourGeneric_ScopeAccessors_FilterAndOverrideCorrectlyAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-a" }, tenantId: "tenant-a");
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-b" }, tenantId: "tenant-b");
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4>(
        context, CreateTableNames(), CreateTenantAccessor("tenant-a"), CreateOptions(QueryScope.Tenant));

    var scoped = await lensQuery.Scope(QueryScope.Tenant).Query<HA1>().ToListAsync();
    var overridden = await lensQuery
        .ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride { TenantId = "tenant-b" })
        .Query<HA1>().ToListAsync();
    var byDefault = await lensQuery.DefaultScope.Query<HA1>().ToListAsync();
    var global = await lensQuery.Scope(QueryScope.Global).Query<HA1>().ToListAsync();

    await Assert.That(scoped.Count).IsEqualTo(1);
    await Assert.That(scoped[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(overridden.Count).IsEqualTo(1);
    await Assert.That(overridden[0].Data.Value).IsEqualTo("row-b");
    await Assert.That(byDefault.Count).IsEqualTo(1);
    await Assert.That(byDefault[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(global.Count).IsEqualTo(2);
  }

  #endregion

  // ===== 5-Generic EFCorePostgresLensQuery =====

  #region 5-Generic Tests

  [Test]
  public async Task FiveGeneric_Query_EachTypeSlot_ReturnsSeededRowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "v1" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA2 { Value = "v2" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA3 { Value = "v3" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA4 { Value = "v4" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA5 { Value = "v5" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5>(context, CreateTableNames());

    var r1 = await lensQuery.Query<HA1>().ToListAsync();
    var r2 = await lensQuery.Query<HA2>().ToListAsync();
    var r3 = await lensQuery.Query<HA3>().ToListAsync();
    var r4 = await lensQuery.Query<HA4>().ToListAsync();
    var r5 = await lensQuery.Query<HA5>().ToListAsync();

    await Assert.That(r1.Count).IsEqualTo(1);
    await Assert.That(r1[0].Data.Value).IsEqualTo("v1");
    await Assert.That(r2.Count).IsEqualTo(1);
    await Assert.That(r2[0].Data.Value).IsEqualTo("v2");
    await Assert.That(r3.Count).IsEqualTo(1);
    await Assert.That(r3[0].Data.Value).IsEqualTo("v3");
    await Assert.That(r4.Count).IsEqualTo(1);
    await Assert.That(r4[0].Data.Value).IsEqualTo("v4");
    await Assert.That(r5.Count).IsEqualTo(1);
    await Assert.That(r5[0].Data.Value).IsEqualTo("v5");
  }

  [Test]
  public async Task FiveGeneric_GetByIdAsync_EachTypeSlot_ReturnsSeededModelAsync() {
    await using var context = CreateInMemoryDbContext();
    var id1 = _idProvider.NewGuid();
    var id2 = _idProvider.NewGuid();
    var id3 = _idProvider.NewGuid();
    var id4 = _idProvider.NewGuid();
    var id5 = _idProvider.NewGuid();
    await SeedAsync(context, id1, new HA1 { Value = "v1" });
    await SeedAsync(context, id2, new HA2 { Value = "v2" });
    await SeedAsync(context, id3, new HA3 { Value = "v3" });
    await SeedAsync(context, id4, new HA4 { Value = "v4" });
    await SeedAsync(context, id5, new HA5 { Value = "v5" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5>(context, CreateTableNames());

    await Assert.That((await lensQuery.GetByIdAsync<HA1>(id1))!.Value).IsEqualTo("v1");
    await Assert.That((await lensQuery.GetByIdAsync<HA2>(id2))!.Value).IsEqualTo("v2");
    await Assert.That((await lensQuery.GetByIdAsync<HA3>(id3))!.Value).IsEqualTo("v3");
    await Assert.That((await lensQuery.GetByIdAsync<HA4>(id4))!.Value).IsEqualTo("v4");
    await Assert.That((await lensQuery.GetByIdAsync<HA5>(id5))!.Value).IsEqualTo("v5");
  }

  [Test]
  public async Task FiveGeneric_GetByIdAsync_WhenNotExists_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5>(context, CreateTableNames());

    var result = await lensQuery.GetByIdAsync<HA5>(_idProvider.NewGuid());

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task FiveGeneric_Constructor_WithNullDbContext_ThrowsArgumentNullExceptionAsync() {
    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5>(null!, CreateTableNames());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("dbContext");
  }

  [Test]
  public async Task FiveGeneric_Constructor_WithNullTableNames_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5>(context, null!);
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("tableNames");
  }

  [Test]
  public async Task FiveGeneric_Constructor_WithNullScopeContextAccessor_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5>(context, CreateTableNames(), null!, CreateOptions());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("scopeContextAccessor");
  }

  [Test]
  public async Task FiveGeneric_Dispose_CalledTwice_DisposesContextOnceAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5>(context, CreateTableNames());

    lensQuery.Dispose();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(() => lensQuery.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task FiveGeneric_DisposeAsync_CalledTwice_DoesNotThrowAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5>(context, CreateTableNames());

    await lensQuery.DisposeAsync();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(async () => await lensQuery.DisposeAsync()).ThrowsNothing();
  }

  [Test]
  public async Task FiveGeneric_ScopeAccessors_FilterAndOverrideCorrectlyAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-a" }, tenantId: "tenant-a");
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-b" }, tenantId: "tenant-b");
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5>(
        context, CreateTableNames(), CreateTenantAccessor("tenant-a"), CreateOptions(QueryScope.Tenant));

    var scoped = await lensQuery.Scope(QueryScope.Tenant).Query<HA1>().ToListAsync();
    var overridden = await lensQuery
        .ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride { TenantId = "tenant-b" })
        .Query<HA1>().ToListAsync();
    var byDefault = await lensQuery.DefaultScope.Query<HA1>().ToListAsync();
    var global = await lensQuery.Scope(QueryScope.Global).Query<HA1>().ToListAsync();

    await Assert.That(scoped.Count).IsEqualTo(1);
    await Assert.That(scoped[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(overridden.Count).IsEqualTo(1);
    await Assert.That(overridden[0].Data.Value).IsEqualTo("row-b");
    await Assert.That(byDefault.Count).IsEqualTo(1);
    await Assert.That(byDefault[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(global.Count).IsEqualTo(2);
  }

  #endregion

  // ===== 6-Generic EFCorePostgresLensQuery =====

  #region 6-Generic Tests

  [Test]
  public async Task SixGeneric_Query_EachTypeSlot_ReturnsSeededRowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "v1" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA2 { Value = "v2" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA3 { Value = "v3" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA4 { Value = "v4" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA5 { Value = "v5" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA6 { Value = "v6" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6>(context, CreateTableNames());

    var r1 = await lensQuery.Query<HA1>().ToListAsync();
    var r2 = await lensQuery.Query<HA2>().ToListAsync();
    var r3 = await lensQuery.Query<HA3>().ToListAsync();
    var r4 = await lensQuery.Query<HA4>().ToListAsync();
    var r5 = await lensQuery.Query<HA5>().ToListAsync();
    var r6 = await lensQuery.Query<HA6>().ToListAsync();

    await Assert.That(r1.Count).IsEqualTo(1);
    await Assert.That(r1[0].Data.Value).IsEqualTo("v1");
    await Assert.That(r2.Count).IsEqualTo(1);
    await Assert.That(r2[0].Data.Value).IsEqualTo("v2");
    await Assert.That(r3.Count).IsEqualTo(1);
    await Assert.That(r3[0].Data.Value).IsEqualTo("v3");
    await Assert.That(r4.Count).IsEqualTo(1);
    await Assert.That(r4[0].Data.Value).IsEqualTo("v4");
    await Assert.That(r5.Count).IsEqualTo(1);
    await Assert.That(r5[0].Data.Value).IsEqualTo("v5");
    await Assert.That(r6.Count).IsEqualTo(1);
    await Assert.That(r6[0].Data.Value).IsEqualTo("v6");
  }

  [Test]
  public async Task SixGeneric_GetByIdAsync_EachTypeSlot_ReturnsSeededModelAsync() {
    await using var context = CreateInMemoryDbContext();
    var id1 = _idProvider.NewGuid();
    var id2 = _idProvider.NewGuid();
    var id3 = _idProvider.NewGuid();
    var id4 = _idProvider.NewGuid();
    var id5 = _idProvider.NewGuid();
    var id6 = _idProvider.NewGuid();
    await SeedAsync(context, id1, new HA1 { Value = "v1" });
    await SeedAsync(context, id2, new HA2 { Value = "v2" });
    await SeedAsync(context, id3, new HA3 { Value = "v3" });
    await SeedAsync(context, id4, new HA4 { Value = "v4" });
    await SeedAsync(context, id5, new HA5 { Value = "v5" });
    await SeedAsync(context, id6, new HA6 { Value = "v6" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6>(context, CreateTableNames());

    await Assert.That((await lensQuery.GetByIdAsync<HA1>(id1))!.Value).IsEqualTo("v1");
    await Assert.That((await lensQuery.GetByIdAsync<HA2>(id2))!.Value).IsEqualTo("v2");
    await Assert.That((await lensQuery.GetByIdAsync<HA3>(id3))!.Value).IsEqualTo("v3");
    await Assert.That((await lensQuery.GetByIdAsync<HA4>(id4))!.Value).IsEqualTo("v4");
    await Assert.That((await lensQuery.GetByIdAsync<HA5>(id5))!.Value).IsEqualTo("v5");
    await Assert.That((await lensQuery.GetByIdAsync<HA6>(id6))!.Value).IsEqualTo("v6");
  }

  [Test]
  public async Task SixGeneric_GetByIdAsync_WhenNotExists_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6>(context, CreateTableNames());

    var result = await lensQuery.GetByIdAsync<HA6>(_idProvider.NewGuid());

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task SixGeneric_Constructor_WithNullDbContext_ThrowsArgumentNullExceptionAsync() {
    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6>(null!, CreateTableNames());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("dbContext");
  }

  [Test]
  public async Task SixGeneric_Constructor_WithNullTableNames_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6>(context, null!);
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("tableNames");
  }

  [Test]
  public async Task SixGeneric_Constructor_WithNullScopeContextAccessor_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6>(context, CreateTableNames(), null!, CreateOptions());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("scopeContextAccessor");
  }

  [Test]
  public async Task SixGeneric_Dispose_CalledTwice_DisposesContextOnceAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6>(context, CreateTableNames());

    lensQuery.Dispose();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(() => lensQuery.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task SixGeneric_DisposeAsync_CalledTwice_DoesNotThrowAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6>(context, CreateTableNames());

    await lensQuery.DisposeAsync();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(async () => await lensQuery.DisposeAsync()).ThrowsNothing();
  }

  [Test]
  public async Task SixGeneric_ScopeAccessors_FilterAndOverrideCorrectlyAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-a" }, tenantId: "tenant-a");
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-b" }, tenantId: "tenant-b");
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6>(
        context, CreateTableNames(), CreateTenantAccessor("tenant-a"), CreateOptions(QueryScope.Tenant));

    var scoped = await lensQuery.Scope(QueryScope.Tenant).Query<HA1>().ToListAsync();
    var overridden = await lensQuery
        .ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride { TenantId = "tenant-b" })
        .Query<HA1>().ToListAsync();
    var byDefault = await lensQuery.DefaultScope.Query<HA1>().ToListAsync();
    var global = await lensQuery.Scope(QueryScope.Global).Query<HA1>().ToListAsync();

    await Assert.That(scoped.Count).IsEqualTo(1);
    await Assert.That(scoped[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(overridden.Count).IsEqualTo(1);
    await Assert.That(overridden[0].Data.Value).IsEqualTo("row-b");
    await Assert.That(byDefault.Count).IsEqualTo(1);
    await Assert.That(byDefault[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(global.Count).IsEqualTo(2);
  }

  #endregion

  // ===== 7-Generic EFCorePostgresLensQuery =====

  #region 7-Generic Tests

  [Test]
  public async Task SevenGeneric_Query_EachTypeSlot_ReturnsSeededRowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "v1" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA2 { Value = "v2" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA3 { Value = "v3" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA4 { Value = "v4" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA5 { Value = "v5" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA6 { Value = "v6" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA7 { Value = "v7" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7>(context, CreateTableNames());

    var r1 = await lensQuery.Query<HA1>().ToListAsync();
    var r2 = await lensQuery.Query<HA2>().ToListAsync();
    var r3 = await lensQuery.Query<HA3>().ToListAsync();
    var r4 = await lensQuery.Query<HA4>().ToListAsync();
    var r5 = await lensQuery.Query<HA5>().ToListAsync();
    var r6 = await lensQuery.Query<HA6>().ToListAsync();
    var r7 = await lensQuery.Query<HA7>().ToListAsync();

    await Assert.That(r1.Count).IsEqualTo(1);
    await Assert.That(r1[0].Data.Value).IsEqualTo("v1");
    await Assert.That(r2.Count).IsEqualTo(1);
    await Assert.That(r2[0].Data.Value).IsEqualTo("v2");
    await Assert.That(r3.Count).IsEqualTo(1);
    await Assert.That(r3[0].Data.Value).IsEqualTo("v3");
    await Assert.That(r4.Count).IsEqualTo(1);
    await Assert.That(r4[0].Data.Value).IsEqualTo("v4");
    await Assert.That(r5.Count).IsEqualTo(1);
    await Assert.That(r5[0].Data.Value).IsEqualTo("v5");
    await Assert.That(r6.Count).IsEqualTo(1);
    await Assert.That(r6[0].Data.Value).IsEqualTo("v6");
    await Assert.That(r7.Count).IsEqualTo(1);
    await Assert.That(r7[0].Data.Value).IsEqualTo("v7");
  }

  [Test]
  public async Task SevenGeneric_GetByIdAsync_EachTypeSlot_ReturnsSeededModelAsync() {
    await using var context = CreateInMemoryDbContext();
    var id1 = _idProvider.NewGuid();
    var id2 = _idProvider.NewGuid();
    var id3 = _idProvider.NewGuid();
    var id4 = _idProvider.NewGuid();
    var id5 = _idProvider.NewGuid();
    var id6 = _idProvider.NewGuid();
    var id7 = _idProvider.NewGuid();
    await SeedAsync(context, id1, new HA1 { Value = "v1" });
    await SeedAsync(context, id2, new HA2 { Value = "v2" });
    await SeedAsync(context, id3, new HA3 { Value = "v3" });
    await SeedAsync(context, id4, new HA4 { Value = "v4" });
    await SeedAsync(context, id5, new HA5 { Value = "v5" });
    await SeedAsync(context, id6, new HA6 { Value = "v6" });
    await SeedAsync(context, id7, new HA7 { Value = "v7" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7>(context, CreateTableNames());

    await Assert.That((await lensQuery.GetByIdAsync<HA1>(id1))!.Value).IsEqualTo("v1");
    await Assert.That((await lensQuery.GetByIdAsync<HA2>(id2))!.Value).IsEqualTo("v2");
    await Assert.That((await lensQuery.GetByIdAsync<HA3>(id3))!.Value).IsEqualTo("v3");
    await Assert.That((await lensQuery.GetByIdAsync<HA4>(id4))!.Value).IsEqualTo("v4");
    await Assert.That((await lensQuery.GetByIdAsync<HA5>(id5))!.Value).IsEqualTo("v5");
    await Assert.That((await lensQuery.GetByIdAsync<HA6>(id6))!.Value).IsEqualTo("v6");
    await Assert.That((await lensQuery.GetByIdAsync<HA7>(id7))!.Value).IsEqualTo("v7");
  }

  [Test]
  public async Task SevenGeneric_GetByIdAsync_WhenNotExists_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7>(context, CreateTableNames());

    var result = await lensQuery.GetByIdAsync<HA7>(_idProvider.NewGuid());

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task SevenGeneric_Constructor_WithNullDbContext_ThrowsArgumentNullExceptionAsync() {
    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7>(null!, CreateTableNames());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("dbContext");
  }

  [Test]
  public async Task SevenGeneric_Constructor_WithNullTableNames_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7>(context, null!);
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("tableNames");
  }

  [Test]
  public async Task SevenGeneric_Constructor_WithNullScopeContextAccessor_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7>(context, CreateTableNames(), null!, CreateOptions());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("scopeContextAccessor");
  }

  [Test]
  public async Task SevenGeneric_Dispose_CalledTwice_DisposesContextOnceAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7>(context, CreateTableNames());

    lensQuery.Dispose();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(() => lensQuery.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task SevenGeneric_DisposeAsync_CalledTwice_DoesNotThrowAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7>(context, CreateTableNames());

    await lensQuery.DisposeAsync();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(async () => await lensQuery.DisposeAsync()).ThrowsNothing();
  }

  [Test]
  public async Task SevenGeneric_ScopeAccessors_FilterAndOverrideCorrectlyAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-a" }, tenantId: "tenant-a");
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-b" }, tenantId: "tenant-b");
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7>(
        context, CreateTableNames(), CreateTenantAccessor("tenant-a"), CreateOptions(QueryScope.Tenant));

    var scoped = await lensQuery.Scope(QueryScope.Tenant).Query<HA1>().ToListAsync();
    var overridden = await lensQuery
        .ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride { TenantId = "tenant-b" })
        .Query<HA1>().ToListAsync();
    var byDefault = await lensQuery.DefaultScope.Query<HA1>().ToListAsync();
    var global = await lensQuery.Scope(QueryScope.Global).Query<HA1>().ToListAsync();

    await Assert.That(scoped.Count).IsEqualTo(1);
    await Assert.That(scoped[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(overridden.Count).IsEqualTo(1);
    await Assert.That(overridden[0].Data.Value).IsEqualTo("row-b");
    await Assert.That(byDefault.Count).IsEqualTo(1);
    await Assert.That(byDefault[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(global.Count).IsEqualTo(2);
  }

  #endregion

  // ===== 8-Generic EFCorePostgresLensQuery =====

  #region 8-Generic Tests

  [Test]
  public async Task EightGeneric_Query_EachTypeSlot_ReturnsSeededRowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "v1" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA2 { Value = "v2" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA3 { Value = "v3" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA4 { Value = "v4" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA5 { Value = "v5" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA6 { Value = "v6" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA7 { Value = "v7" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA8 { Value = "v8" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8>(context, CreateTableNames());

    var r1 = await lensQuery.Query<HA1>().ToListAsync();
    var r2 = await lensQuery.Query<HA2>().ToListAsync();
    var r3 = await lensQuery.Query<HA3>().ToListAsync();
    var r4 = await lensQuery.Query<HA4>().ToListAsync();
    var r5 = await lensQuery.Query<HA5>().ToListAsync();
    var r6 = await lensQuery.Query<HA6>().ToListAsync();
    var r7 = await lensQuery.Query<HA7>().ToListAsync();
    var r8 = await lensQuery.Query<HA8>().ToListAsync();

    await Assert.That(r1.Count).IsEqualTo(1);
    await Assert.That(r1[0].Data.Value).IsEqualTo("v1");
    await Assert.That(r2.Count).IsEqualTo(1);
    await Assert.That(r2[0].Data.Value).IsEqualTo("v2");
    await Assert.That(r3.Count).IsEqualTo(1);
    await Assert.That(r3[0].Data.Value).IsEqualTo("v3");
    await Assert.That(r4.Count).IsEqualTo(1);
    await Assert.That(r4[0].Data.Value).IsEqualTo("v4");
    await Assert.That(r5.Count).IsEqualTo(1);
    await Assert.That(r5[0].Data.Value).IsEqualTo("v5");
    await Assert.That(r6.Count).IsEqualTo(1);
    await Assert.That(r6[0].Data.Value).IsEqualTo("v6");
    await Assert.That(r7.Count).IsEqualTo(1);
    await Assert.That(r7[0].Data.Value).IsEqualTo("v7");
    await Assert.That(r8.Count).IsEqualTo(1);
    await Assert.That(r8[0].Data.Value).IsEqualTo("v8");
  }

  [Test]
  public async Task EightGeneric_GetByIdAsync_EachTypeSlot_ReturnsSeededModelAsync() {
    await using var context = CreateInMemoryDbContext();
    var id1 = _idProvider.NewGuid();
    var id2 = _idProvider.NewGuid();
    var id3 = _idProvider.NewGuid();
    var id4 = _idProvider.NewGuid();
    var id5 = _idProvider.NewGuid();
    var id6 = _idProvider.NewGuid();
    var id7 = _idProvider.NewGuid();
    var id8 = _idProvider.NewGuid();
    await SeedAsync(context, id1, new HA1 { Value = "v1" });
    await SeedAsync(context, id2, new HA2 { Value = "v2" });
    await SeedAsync(context, id3, new HA3 { Value = "v3" });
    await SeedAsync(context, id4, new HA4 { Value = "v4" });
    await SeedAsync(context, id5, new HA5 { Value = "v5" });
    await SeedAsync(context, id6, new HA6 { Value = "v6" });
    await SeedAsync(context, id7, new HA7 { Value = "v7" });
    await SeedAsync(context, id8, new HA8 { Value = "v8" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8>(context, CreateTableNames());

    await Assert.That((await lensQuery.GetByIdAsync<HA1>(id1))!.Value).IsEqualTo("v1");
    await Assert.That((await lensQuery.GetByIdAsync<HA2>(id2))!.Value).IsEqualTo("v2");
    await Assert.That((await lensQuery.GetByIdAsync<HA3>(id3))!.Value).IsEqualTo("v3");
    await Assert.That((await lensQuery.GetByIdAsync<HA4>(id4))!.Value).IsEqualTo("v4");
    await Assert.That((await lensQuery.GetByIdAsync<HA5>(id5))!.Value).IsEqualTo("v5");
    await Assert.That((await lensQuery.GetByIdAsync<HA6>(id6))!.Value).IsEqualTo("v6");
    await Assert.That((await lensQuery.GetByIdAsync<HA7>(id7))!.Value).IsEqualTo("v7");
    await Assert.That((await lensQuery.GetByIdAsync<HA8>(id8))!.Value).IsEqualTo("v8");
  }

  [Test]
  public async Task EightGeneric_GetByIdAsync_WhenNotExists_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8>(context, CreateTableNames());

    var result = await lensQuery.GetByIdAsync<HA8>(_idProvider.NewGuid());

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task EightGeneric_Constructor_WithNullDbContext_ThrowsArgumentNullExceptionAsync() {
    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8>(null!, CreateTableNames());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("dbContext");
  }

  [Test]
  public async Task EightGeneric_Constructor_WithNullTableNames_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8>(context, null!);
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("tableNames");
  }

  [Test]
  public async Task EightGeneric_Constructor_WithNullScopeContextAccessor_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8>(context, CreateTableNames(), null!, CreateOptions());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("scopeContextAccessor");
  }

  [Test]
  public async Task EightGeneric_Dispose_CalledTwice_DisposesContextOnceAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8>(context, CreateTableNames());

    lensQuery.Dispose();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(() => lensQuery.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task EightGeneric_DisposeAsync_CalledTwice_DoesNotThrowAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8>(context, CreateTableNames());

    await lensQuery.DisposeAsync();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(async () => await lensQuery.DisposeAsync()).ThrowsNothing();
  }

  [Test]
  public async Task EightGeneric_ScopeAccessors_FilterAndOverrideCorrectlyAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-a" }, tenantId: "tenant-a");
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-b" }, tenantId: "tenant-b");
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8>(
        context, CreateTableNames(), CreateTenantAccessor("tenant-a"), CreateOptions(QueryScope.Tenant));

    var scoped = await lensQuery.Scope(QueryScope.Tenant).Query<HA1>().ToListAsync();
    var overridden = await lensQuery
        .ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride { TenantId = "tenant-b" })
        .Query<HA1>().ToListAsync();
    var byDefault = await lensQuery.DefaultScope.Query<HA1>().ToListAsync();
    var global = await lensQuery.Scope(QueryScope.Global).Query<HA1>().ToListAsync();

    await Assert.That(scoped.Count).IsEqualTo(1);
    await Assert.That(scoped[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(overridden.Count).IsEqualTo(1);
    await Assert.That(overridden[0].Data.Value).IsEqualTo("row-b");
    await Assert.That(byDefault.Count).IsEqualTo(1);
    await Assert.That(byDefault[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(global.Count).IsEqualTo(2);
  }

  #endregion

  // ===== 9-Generic EFCorePostgresLensQuery =====

  #region 9-Generic Tests

  [Test]
  public async Task NineGeneric_Query_EachTypeSlot_ReturnsSeededRowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "v1" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA2 { Value = "v2" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA3 { Value = "v3" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA4 { Value = "v4" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA5 { Value = "v5" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA6 { Value = "v6" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA7 { Value = "v7" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA8 { Value = "v8" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA9 { Value = "v9" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9>(context, CreateTableNames());

    var r1 = await lensQuery.Query<HA1>().ToListAsync();
    var r2 = await lensQuery.Query<HA2>().ToListAsync();
    var r3 = await lensQuery.Query<HA3>().ToListAsync();
    var r4 = await lensQuery.Query<HA4>().ToListAsync();
    var r5 = await lensQuery.Query<HA5>().ToListAsync();
    var r6 = await lensQuery.Query<HA6>().ToListAsync();
    var r7 = await lensQuery.Query<HA7>().ToListAsync();
    var r8 = await lensQuery.Query<HA8>().ToListAsync();
    var r9 = await lensQuery.Query<HA9>().ToListAsync();

    await Assert.That(r1.Count).IsEqualTo(1);
    await Assert.That(r1[0].Data.Value).IsEqualTo("v1");
    await Assert.That(r2.Count).IsEqualTo(1);
    await Assert.That(r2[0].Data.Value).IsEqualTo("v2");
    await Assert.That(r3.Count).IsEqualTo(1);
    await Assert.That(r3[0].Data.Value).IsEqualTo("v3");
    await Assert.That(r4.Count).IsEqualTo(1);
    await Assert.That(r4[0].Data.Value).IsEqualTo("v4");
    await Assert.That(r5.Count).IsEqualTo(1);
    await Assert.That(r5[0].Data.Value).IsEqualTo("v5");
    await Assert.That(r6.Count).IsEqualTo(1);
    await Assert.That(r6[0].Data.Value).IsEqualTo("v6");
    await Assert.That(r7.Count).IsEqualTo(1);
    await Assert.That(r7[0].Data.Value).IsEqualTo("v7");
    await Assert.That(r8.Count).IsEqualTo(1);
    await Assert.That(r8[0].Data.Value).IsEqualTo("v8");
    await Assert.That(r9.Count).IsEqualTo(1);
    await Assert.That(r9[0].Data.Value).IsEqualTo("v9");
  }

  [Test]
  public async Task NineGeneric_GetByIdAsync_EachTypeSlot_ReturnsSeededModelAsync() {
    await using var context = CreateInMemoryDbContext();
    var id1 = _idProvider.NewGuid();
    var id2 = _idProvider.NewGuid();
    var id3 = _idProvider.NewGuid();
    var id4 = _idProvider.NewGuid();
    var id5 = _idProvider.NewGuid();
    var id6 = _idProvider.NewGuid();
    var id7 = _idProvider.NewGuid();
    var id8 = _idProvider.NewGuid();
    var id9 = _idProvider.NewGuid();
    await SeedAsync(context, id1, new HA1 { Value = "v1" });
    await SeedAsync(context, id2, new HA2 { Value = "v2" });
    await SeedAsync(context, id3, new HA3 { Value = "v3" });
    await SeedAsync(context, id4, new HA4 { Value = "v4" });
    await SeedAsync(context, id5, new HA5 { Value = "v5" });
    await SeedAsync(context, id6, new HA6 { Value = "v6" });
    await SeedAsync(context, id7, new HA7 { Value = "v7" });
    await SeedAsync(context, id8, new HA8 { Value = "v8" });
    await SeedAsync(context, id9, new HA9 { Value = "v9" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9>(context, CreateTableNames());

    await Assert.That((await lensQuery.GetByIdAsync<HA1>(id1))!.Value).IsEqualTo("v1");
    await Assert.That((await lensQuery.GetByIdAsync<HA2>(id2))!.Value).IsEqualTo("v2");
    await Assert.That((await lensQuery.GetByIdAsync<HA3>(id3))!.Value).IsEqualTo("v3");
    await Assert.That((await lensQuery.GetByIdAsync<HA4>(id4))!.Value).IsEqualTo("v4");
    await Assert.That((await lensQuery.GetByIdAsync<HA5>(id5))!.Value).IsEqualTo("v5");
    await Assert.That((await lensQuery.GetByIdAsync<HA6>(id6))!.Value).IsEqualTo("v6");
    await Assert.That((await lensQuery.GetByIdAsync<HA7>(id7))!.Value).IsEqualTo("v7");
    await Assert.That((await lensQuery.GetByIdAsync<HA8>(id8))!.Value).IsEqualTo("v8");
    await Assert.That((await lensQuery.GetByIdAsync<HA9>(id9))!.Value).IsEqualTo("v9");
  }

  [Test]
  public async Task NineGeneric_GetByIdAsync_WhenNotExists_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9>(context, CreateTableNames());

    var result = await lensQuery.GetByIdAsync<HA9>(_idProvider.NewGuid());

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task NineGeneric_Constructor_WithNullDbContext_ThrowsArgumentNullExceptionAsync() {
    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9>(null!, CreateTableNames());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("dbContext");
  }

  [Test]
  public async Task NineGeneric_Constructor_WithNullTableNames_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9>(context, null!);
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("tableNames");
  }

  [Test]
  public async Task NineGeneric_Constructor_WithNullScopeContextAccessor_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9>(context, CreateTableNames(), null!, CreateOptions());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("scopeContextAccessor");
  }

  [Test]
  public async Task NineGeneric_Dispose_CalledTwice_DisposesContextOnceAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9>(context, CreateTableNames());

    lensQuery.Dispose();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(() => lensQuery.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task NineGeneric_DisposeAsync_CalledTwice_DoesNotThrowAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9>(context, CreateTableNames());

    await lensQuery.DisposeAsync();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(async () => await lensQuery.DisposeAsync()).ThrowsNothing();
  }

  [Test]
  public async Task NineGeneric_ScopeAccessors_FilterAndOverrideCorrectlyAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-a" }, tenantId: "tenant-a");
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-b" }, tenantId: "tenant-b");
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9>(
        context, CreateTableNames(), CreateTenantAccessor("tenant-a"), CreateOptions(QueryScope.Tenant));

    var scoped = await lensQuery.Scope(QueryScope.Tenant).Query<HA1>().ToListAsync();
    var overridden = await lensQuery
        .ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride { TenantId = "tenant-b" })
        .Query<HA1>().ToListAsync();
    var byDefault = await lensQuery.DefaultScope.Query<HA1>().ToListAsync();
    var global = await lensQuery.Scope(QueryScope.Global).Query<HA1>().ToListAsync();

    await Assert.That(scoped.Count).IsEqualTo(1);
    await Assert.That(scoped[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(overridden.Count).IsEqualTo(1);
    await Assert.That(overridden[0].Data.Value).IsEqualTo("row-b");
    await Assert.That(byDefault.Count).IsEqualTo(1);
    await Assert.That(byDefault[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(global.Count).IsEqualTo(2);
  }

  #endregion

  // ===== 10-Generic EFCorePostgresLensQuery =====

  #region 10-Generic Tests

  [Test]
  public async Task TenGeneric_Query_EachTypeSlot_ReturnsSeededRowsAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "v1" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA2 { Value = "v2" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA3 { Value = "v3" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA4 { Value = "v4" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA5 { Value = "v5" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA6 { Value = "v6" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA7 { Value = "v7" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA8 { Value = "v8" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA9 { Value = "v9" });
    await SeedAsync(context, _idProvider.NewGuid(), new HA10 { Value = "v10" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9, HA10>(context, CreateTableNames());

    var r1 = await lensQuery.Query<HA1>().ToListAsync();
    var r2 = await lensQuery.Query<HA2>().ToListAsync();
    var r3 = await lensQuery.Query<HA3>().ToListAsync();
    var r4 = await lensQuery.Query<HA4>().ToListAsync();
    var r5 = await lensQuery.Query<HA5>().ToListAsync();
    var r6 = await lensQuery.Query<HA6>().ToListAsync();
    var r7 = await lensQuery.Query<HA7>().ToListAsync();
    var r8 = await lensQuery.Query<HA8>().ToListAsync();
    var r9 = await lensQuery.Query<HA9>().ToListAsync();
    var r10 = await lensQuery.Query<HA10>().ToListAsync();

    await Assert.That(r1.Count).IsEqualTo(1);
    await Assert.That(r1[0].Data.Value).IsEqualTo("v1");
    await Assert.That(r2.Count).IsEqualTo(1);
    await Assert.That(r2[0].Data.Value).IsEqualTo("v2");
    await Assert.That(r3.Count).IsEqualTo(1);
    await Assert.That(r3[0].Data.Value).IsEqualTo("v3");
    await Assert.That(r4.Count).IsEqualTo(1);
    await Assert.That(r4[0].Data.Value).IsEqualTo("v4");
    await Assert.That(r5.Count).IsEqualTo(1);
    await Assert.That(r5[0].Data.Value).IsEqualTo("v5");
    await Assert.That(r6.Count).IsEqualTo(1);
    await Assert.That(r6[0].Data.Value).IsEqualTo("v6");
    await Assert.That(r7.Count).IsEqualTo(1);
    await Assert.That(r7[0].Data.Value).IsEqualTo("v7");
    await Assert.That(r8.Count).IsEqualTo(1);
    await Assert.That(r8[0].Data.Value).IsEqualTo("v8");
    await Assert.That(r9.Count).IsEqualTo(1);
    await Assert.That(r9[0].Data.Value).IsEqualTo("v9");
    await Assert.That(r10.Count).IsEqualTo(1);
    await Assert.That(r10[0].Data.Value).IsEqualTo("v10");
  }

  [Test]
  public async Task TenGeneric_GetByIdAsync_EachTypeSlot_ReturnsSeededModelAsync() {
    await using var context = CreateInMemoryDbContext();
    var id1 = _idProvider.NewGuid();
    var id2 = _idProvider.NewGuid();
    var id3 = _idProvider.NewGuid();
    var id4 = _idProvider.NewGuid();
    var id5 = _idProvider.NewGuid();
    var id6 = _idProvider.NewGuid();
    var id7 = _idProvider.NewGuid();
    var id8 = _idProvider.NewGuid();
    var id9 = _idProvider.NewGuid();
    var id10 = _idProvider.NewGuid();
    await SeedAsync(context, id1, new HA1 { Value = "v1" });
    await SeedAsync(context, id2, new HA2 { Value = "v2" });
    await SeedAsync(context, id3, new HA3 { Value = "v3" });
    await SeedAsync(context, id4, new HA4 { Value = "v4" });
    await SeedAsync(context, id5, new HA5 { Value = "v5" });
    await SeedAsync(context, id6, new HA6 { Value = "v6" });
    await SeedAsync(context, id7, new HA7 { Value = "v7" });
    await SeedAsync(context, id8, new HA8 { Value = "v8" });
    await SeedAsync(context, id9, new HA9 { Value = "v9" });
    await SeedAsync(context, id10, new HA10 { Value = "v10" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9, HA10>(context, CreateTableNames());

    await Assert.That((await lensQuery.GetByIdAsync<HA1>(id1))!.Value).IsEqualTo("v1");
    await Assert.That((await lensQuery.GetByIdAsync<HA2>(id2))!.Value).IsEqualTo("v2");
    await Assert.That((await lensQuery.GetByIdAsync<HA3>(id3))!.Value).IsEqualTo("v3");
    await Assert.That((await lensQuery.GetByIdAsync<HA4>(id4))!.Value).IsEqualTo("v4");
    await Assert.That((await lensQuery.GetByIdAsync<HA5>(id5))!.Value).IsEqualTo("v5");
    await Assert.That((await lensQuery.GetByIdAsync<HA6>(id6))!.Value).IsEqualTo("v6");
    await Assert.That((await lensQuery.GetByIdAsync<HA7>(id7))!.Value).IsEqualTo("v7");
    await Assert.That((await lensQuery.GetByIdAsync<HA8>(id8))!.Value).IsEqualTo("v8");
    await Assert.That((await lensQuery.GetByIdAsync<HA9>(id9))!.Value).IsEqualTo("v9");
    await Assert.That((await lensQuery.GetByIdAsync<HA10>(id10))!.Value).IsEqualTo("v10");
  }

  [Test]
  public async Task TenGeneric_GetByIdAsync_WhenNotExists_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9, HA10>(context, CreateTableNames());

    var result = await lensQuery.GetByIdAsync<HA10>(_idProvider.NewGuid());

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task TenGeneric_Constructor_WithNullDbContext_ThrowsArgumentNullExceptionAsync() {
    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9, HA10>(null!, CreateTableNames());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("dbContext");
  }

  [Test]
  public async Task TenGeneric_Constructor_WithNullTableNames_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9, HA10>(context, null!);
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("tableNames");
  }

  [Test]
  public async Task TenGeneric_Constructor_WithNullScopeContextAccessor_ThrowsArgumentNullExceptionAsync() {
    await using var context = CreateInMemoryDbContext();

    ArgumentNullException? exception = null;
    try {
      _ = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9, HA10>(context, CreateTableNames(), null!, CreateOptions());
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.ParamName).IsEqualTo("scopeContextAccessor");
  }

  [Test]
  public async Task TenGeneric_Dispose_CalledTwice_DisposesContextOnceAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9, HA10>(context, CreateTableNames());

    lensQuery.Dispose();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(() => lensQuery.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task TenGeneric_DisposeAsync_CalledTwice_DoesNotThrowAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9, HA10>(context, CreateTableNames());

    await lensQuery.DisposeAsync();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(async () => await lensQuery.DisposeAsync()).ThrowsNothing();
  }

  [Test]
  public async Task TenGeneric_ScopeAccessors_FilterAndOverrideCorrectlyAsync() {
    await using var context = CreateInMemoryDbContext();
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-a" }, tenantId: "tenant-a");
    await SeedAsync(context, _idProvider.NewGuid(), new HA1 { Value = "row-b" }, tenantId: "tenant-b");
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3, HA4, HA5, HA6, HA7, HA8, HA9, HA10>(
        context, CreateTableNames(), CreateTenantAccessor("tenant-a"), CreateOptions(QueryScope.Tenant));

    var scoped = await lensQuery.Scope(QueryScope.Tenant).Query<HA1>().ToListAsync();
    var overridden = await lensQuery
        .ScopeOverride(QueryScope.Tenant, new ScopeFilterOverride { TenantId = "tenant-b" })
        .Query<HA1>().ToListAsync();
    var byDefault = await lensQuery.DefaultScope.Query<HA1>().ToListAsync();
    var global = await lensQuery.Scope(QueryScope.Global).Query<HA1>().ToListAsync();

    await Assert.That(scoped.Count).IsEqualTo(1);
    await Assert.That(scoped[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(overridden.Count).IsEqualTo(1);
    await Assert.That(overridden[0].Data.Value).IsEqualTo("row-b");
    await Assert.That(byDefault.Count).IsEqualTo(1);
    await Assert.That(byDefault[0].Data.Value).IsEqualTo("row-a");
    await Assert.That(global.Count).IsEqualTo(2);
  }

  #endregion

  // ===== Lower-arity coverage gaps (arity 2/3 sync Dispose + arity 3 GetByIdAsync) =====

  #region Lower-Arity Gap Tests

  [Test]
  public async Task TwoGeneric_Dispose_CalledTwice_DisposesContextOnceAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2>(context, CreateTableNames());

    lensQuery.Dispose();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(() => lensQuery.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task ThreeGeneric_Dispose_CalledTwice_DisposesContextOnceAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3>(context, CreateTableNames());

    lensQuery.Dispose();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(() => lensQuery.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task ThreeGeneric_DisposeAsync_CalledTwice_DoesNotThrowAsync() {
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3>(context, CreateTableNames());

    await lensQuery.DisposeAsync();

    await Assert.That(() => context.Set<PerspectiveRow<HA1>>().ToList())
        .Throws<ObjectDisposedException>();
    await Assert.That(async () => await lensQuery.DisposeAsync()).ThrowsNothing();
  }

  [Test]
  public async Task ThreeGeneric_GetByIdAsync_EachTypeSlot_ReturnsSeededModelAsync() {
    await using var context = CreateInMemoryDbContext();
    var id1 = _idProvider.NewGuid();
    var id2 = _idProvider.NewGuid();
    var id3 = _idProvider.NewGuid();
    await SeedAsync(context, id1, new HA1 { Value = "v1" });
    await SeedAsync(context, id2, new HA2 { Value = "v2" });
    await SeedAsync(context, id3, new HA3 { Value = "v3" });
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3>(context, CreateTableNames());

    await Assert.That((await lensQuery.GetByIdAsync<HA1>(id1))!.Value).IsEqualTo("v1");
    await Assert.That((await lensQuery.GetByIdAsync<HA2>(id2))!.Value).IsEqualTo("v2");
    await Assert.That((await lensQuery.GetByIdAsync<HA3>(id3))!.Value).IsEqualTo("v3");
  }

  [Test]
  public async Task ThreeGeneric_GetByIdAsync_WhenNotExists_ReturnsNullAsync() {
    await using var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<HA1, HA2, HA3>(context, CreateTableNames());

    var result = await lensQuery.GetByIdAsync<HA3>(_idProvider.NewGuid());

    await Assert.That(result).IsNull();
  }

  #endregion
}
