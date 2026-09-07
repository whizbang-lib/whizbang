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
/// The scope-filtered lens read against a Split-mode perspective model.
/// </summary>
/// <remarks>
/// <para>
/// Split mode keeps some of the model's fields in real columns rather than inside the JSON
/// document, and the only thing that copies those column values back into the model is a
/// <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker"/> <c>Tracked</c> handler.
/// That handler cannot fire for an untracked entity, so the scoped read has to skip
/// <c>AsNoTracking</c> for these models — and skipping it is not an optimization detail, because
/// the failure it prevents is silent: an untracked Split-mode row materializes with every
/// split-out field at its default, which reads as real data that happens to be empty.
/// </para>
/// <para>
/// This covers the FILTERED scoped access specifically. The unfiltered (Global) path has its own
/// branch, and a lens read under a tenant scope is the ordinary case in a multi-tenant service —
/// exactly the one where an empty field looks like the tenant simply has no value set.
/// </para>
/// <para>
/// The model type here is deliberately unique to this class: whether a model is Split mode is
/// latched into a static the first time that closed generic type is touched, so sharing a model
/// with another test could latch it before the hydrator is registered and quietly test the
/// non-split branch instead.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresLensQuery.cs</code-under-test>
[Category("EFCore")]
[Category("Lenses")]
[Category("Unit")]
[Category("Shard2")]
public class EFCorePostgresLensQuerySplitModeScopeTests {

  private static readonly Lock _hydrationLock = new();
  private static int _hydrationCalls;

  private sealed record SplitScopedLensItem {
    public string Name { get; init; } = "";
  }

  private sealed class SplitScopeDbContext(DbContextOptions<SplitScopeDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<SplitScopedLensItem>>(entity => {
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

  [Test]
  public async Task ScopeTenant_WithASplitModeModel_TracksTheRowsSoTheHydratorCanRunAsync() {
    // Registered before the lens type is first touched, or the split-mode decision latches false.
    SplitModeChangeTrackerHydrator.Register(
      typeof(PerspectiveRow<SplitScopedLensItem>),
      static _ => {
        lock (_hydrationLock) {
          _hydrationCalls++;
        }
      });

    await using var context = new SplitScopeDbContext(
      new DbContextOptionsBuilder<SplitScopeDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    await _seedAsync(context, "mine", tenantId: "tenant-1");
    await _seedAsync(context, "theirs", tenantId: "tenant-2");
    context.ChangeTracker.Clear();
    lock (_hydrationLock) {
      _hydrationCalls = 0;
    }

    var accessor = new TestScopeContextAccessor {
      Current = new TestScopeContext { Scope = new PerspectiveScope { TenantId = "tenant-1" } }
    };
    var lensQuery = new EFCorePostgresLensQuery<SplitScopedLensItem>(
      context, "split_scoped_lens_items", accessor,
      Options.Create(new WhizbangCoreOptions { DefaultQueryScope = QueryScope.Tenant }));

    var results = await lensQuery.Scope(QueryScope.Tenant).Query.ToListAsync();

    // The scope filter still has to apply — the split-mode branch must not become a way to read
    // another tenant's rows.
    await Assert.That(results).Count().IsEqualTo(1)
      .Because("the tenant filter applies on the split-mode branch exactly as it does on the "
             + "no-tracking one; a branch that skipped it would leak rows across tenants");
    await Assert.That(results[0].Data.Name).IsEqualTo("mine");

    var tracked = context.ChangeTracker.Entries<PerspectiveRow<SplitScopedLensItem>>().ToList();
    await Assert.That(tracked).Count().IsEqualTo(1)
      .Because("AsNoTracking would leave nothing for the ChangeTracker hook to hydrate, and a "
             + "Split-mode row that is never hydrated comes back with its split-out fields at "
             + "their defaults — indistinguishable from a row whose values really are empty");

    int calls;
    lock (_hydrationLock) {
      calls = _hydrationCalls;
    }
    await Assert.That(calls).IsGreaterThan(0)
      .Because("tracking alone is not enough: the read also has to subscribe this context to the "
             + "Tracked event, or the hydrator sits registered and never runs");
  }

  private static async Task _seedAsync(DbContext context, string name, string tenantId) {
    context.Set<PerspectiveRow<SplitScopedLensItem>>().Add(new PerspectiveRow<SplitScopedLensItem> {
      Id = Guid.CreateVersion7(),
      Data = new SplitScopedLensItem { Name = name },
      Metadata = new PerspectiveMetadata {
        EventType = "Created",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow,
      },
      Scope = new PerspectiveScope { TenantId = tenantId },
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1,
    });
    await context.SaveChangesAsync();
  }
}
