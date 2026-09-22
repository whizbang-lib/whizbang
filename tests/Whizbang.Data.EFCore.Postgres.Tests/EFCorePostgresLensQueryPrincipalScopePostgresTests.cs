using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Postgres-backed tests for <see cref="EFCorePostgresLensQuery{TModel}"/> Principal and
/// UserOrPrincipal scope filtering. These live here (not in the InMemory scope test file)
/// because the AllowedPrincipals containment filter requires the Npgsql JSONB translation —
/// the EF InMemory provider cannot translate it.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresLensQuery.cs</tests>
[Category("Shard3")]
public class EFCorePostgresLensQueryPrincipalScopePostgresTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new();

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

  private static TestScopeContextAccessor CreateScopeAccessor(
      string? tenantId = null,
      string? userId = null,
      HashSet<SecurityPrincipalId>? principals = null) {
    return new TestScopeContextAccessor {
      Current = new TestScopeContext {
        Scope = new PerspectiveScope {
          TenantId = tenantId,
          UserId = userId
        },
        SecurityPrincipals = principals ?? []
      }
    };
  }

  private async Task<Guid> SeedOrderAsync(
      WorkCoordinationDbContext context,
      string name,
      string tenantId,
      string? userId = null,
      List<string>? allowedPrincipals = null) {
    var id = _idProvider.NewGuid();
    var row = new PerspectiveRow<Order> {
      Id = id,
      Data = new Order { OrderId = TestOrderId.From(id), Amount = 42m, Status = name },
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
    return id;
  }

  private static EFCorePostgresLensQuery<Order> CreateLensQuery(
      WorkCoordinationDbContext context,
      IScopeContextAccessor accessor) {
    return new EFCorePostgresLensQuery<Order>(
      context, "wh_per_order", accessor,
      Options.Create(new WhizbangCoreOptions { DefaultQueryScope = QueryScope.Tenant }));
  }

  [Test]
  public async Task Scope_Principal_ReturnsOnlyRowsSharedWithCallerPrincipalsAsync() {
    await using var context = CreateDbContext();
    await SeedOrderAsync(context, "shared", tenantId: "tenant-1", allowedPrincipals: ["group:sales-team"]);
    await SeedOrderAsync(context, "hidden", tenantId: "tenant-1", allowedPrincipals: ["group:engineering"]);
    var principals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.Group("sales-team") };
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(tenantId: "tenant-1", principals: principals));

    var results = await lensQuery.Scope(QueryScope.Principal).Query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Status).IsEqualTo("shared");
  }

  [Test]
  public async Task Scope_UserOrPrincipal_ReturnsOwnedOrSharedRowsAsync() {
    await using var context = CreateDbContext();
    var ownedId = await SeedOrderAsync(context, "owned", tenantId: "tenant-1", userId: "user-alice");
    var sharedId = await SeedOrderAsync(context, "shared", tenantId: "tenant-1", userId: "user-bob",
        allowedPrincipals: ["group:sales-team"]);
    var hiddenId = await SeedOrderAsync(context, "hidden", tenantId: "tenant-1", userId: "user-charlie",
        allowedPrincipals: ["group:engineering"]);
    var principals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("sales-team")
    };
    var lensQuery = CreateLensQuery(context, CreateScopeAccessor(
        tenantId: "tenant-1", userId: "user-alice", principals: principals));

    var results = await lensQuery.Scope(QueryScope.UserOrPrincipal).Query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(2);
    var ids = results.Select(r => r.Id).ToHashSet();
    await Assert.That(ids.Contains(ownedId)).IsTrue();
    await Assert.That(ids.Contains(sharedId)).IsTrue();
    await Assert.That(ids.Contains(hiddenId)).IsFalse();
  }
}
