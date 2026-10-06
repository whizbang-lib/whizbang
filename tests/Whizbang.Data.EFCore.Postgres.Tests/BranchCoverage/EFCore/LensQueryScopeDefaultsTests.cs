// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for the lens queries' default scope and the in-memory filter composition:
/// every arity of <c>EFCorePostgresLensQuery</c> falls back to the tenant scope when no options are
/// supplied and honors a configured default otherwise; the principal filters compose the right
/// predicate for each combination of user and principals. No database: the queries are composed
/// over in-memory rows and the DbContext is never opened.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresLensQuery.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PrincipalFilterExtensions.cs</code-under-test>
[Category("Shard2")]
public class LensQueryScopeDefaultsTests {

  private static readonly IOptions<WhizbangCoreOptions> _global =
    Options.Create(new WhizbangCoreOptions { DefaultQueryScope = QueryScope.Global });

  private static readonly Dictionary<Type, string> _tables = new() { [typeof(Doc)] = "wh_per_doc" };

  // Without options the default is the tenant scope, which needs an ambient scope context: asking
  // for it with none present must fail loudly instead of silently reading every tenant's rows.
  // With a configured global default no context is needed.
  [Test]
  public async Task SingleModel_DefaultScope_TenantWithoutOptions_ConfiguredOtherwiseAsync() {
    await using var context = _context();

    var unconfigured = new EFCorePostgresLensQuery<Doc>(context, "wh_per_doc", NullScopeContextAccessor.Instance, null!);
    var configured = new EFCorePostgresLensQuery<Doc>(context, "wh_per_doc", NullScopeContextAccessor.Instance, _global);

    await _assertTenantDefaultAsync(() => unconfigured.DefaultScope);
    await Assert.That(configured.DefaultScope).IsNotNull();
  }

  [Test]
  public async Task MultiModel_EveryArity_DefaultScope_TenantWithoutOptions_ConfiguredOtherwiseAsync() {
    using (var a = new EFCorePostgresLensQuery<Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, null!))
    using (var b = new EFCorePostgresLensQuery<Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, _global)) {
      await _assertTenantDefaultAsync(() => a.DefaultScope);
      await Assert.That(b.DefaultScope).IsNotNull();
    }
    using (var a = new EFCorePostgresLensQuery<Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, null!))
    using (var b = new EFCorePostgresLensQuery<Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, _global)) {
      await _assertTenantDefaultAsync(() => a.DefaultScope);
      await Assert.That(b.DefaultScope).IsNotNull();
    }
    using (var a = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, null!))
    using (var b = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, _global)) {
      await _assertTenantDefaultAsync(() => a.DefaultScope);
      await Assert.That(b.DefaultScope).IsNotNull();
    }
    using (var a = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, null!))
    using (var b = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, _global)) {
      await _assertTenantDefaultAsync(() => a.DefaultScope);
      await Assert.That(b.DefaultScope).IsNotNull();
    }
    using (var a = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, null!))
    using (var b = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, _global)) {
      await _assertTenantDefaultAsync(() => a.DefaultScope);
      await Assert.That(b.DefaultScope).IsNotNull();
    }
    using (var a = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, null!))
    using (var b = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, _global)) {
      await _assertTenantDefaultAsync(() => a.DefaultScope);
      await Assert.That(b.DefaultScope).IsNotNull();
    }
    using (var a = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, null!))
    using (var b = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, _global)) {
      await _assertTenantDefaultAsync(() => a.DefaultScope);
      await Assert.That(b.DefaultScope).IsNotNull();
    }
    using (var a = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, null!))
    using (var b = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, _global)) {
      await _assertTenantDefaultAsync(() => a.DefaultScope);
      await Assert.That(b.DefaultScope).IsNotNull();
    }
    using (var a = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, null!))
    using (var b = new EFCorePostgresLensQuery<Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc, Doc>(_context(), _tables, NullScopeContextAccessor.Instance, _global)) {
      await _assertTenantDefaultAsync(() => a.DefaultScope);
      await Assert.That(b.DefaultScope).IsNotNull();
    }
  }

  // A principal filter narrows the rows only when the caller actually holds principals; a scope
  // with the principal flag and no principals, or without the flag, leaves the principal clause out.
  [Test]
  public async Task ApplyFilterInfo_PrincipalClause_OnlyWithFlagAndPrincipalsAsync() {
    var rows = _rows();

    var tenantOnly = ScopedAccessHelper.ApplyFilterInfo(rows, new ScopeFilterInfo {
      Filters = ScopeFilters.Tenant,
      TenantId = "tenant-a",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
    }).ToList();
    var flagNoPrincipals = ScopedAccessHelper.ApplyFilterInfo(rows, new ScopeFilterInfo {
      Filters = ScopeFilters.Principal,
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
    }).ToList();
    var flagWithPrincipals = ScopedAccessHelper.ApplyFilterInfo(rows, new ScopeFilterInfo {
      Filters = ScopeFilters.Principal,
      SecurityPrincipals = new HashSet<SecurityPrincipalId> { new("group:p1") },
    }).ToList();

    await Assert.That(tenantOnly.Count).IsEqualTo(2)
      .Because("only the tenant clause applies");
    await Assert.That(flagNoPrincipals.Count).IsEqualTo(3)
      .Because("with no principals held, the principal clause is not composed at all");
    await Assert.That(flagWithPrincipals.Select(r => r.Scope.UserId)).IsEquivalentTo(["user-1"])
      .Because("only the row that allows group:p1 is visible to a caller holding it");
  }

  [Test]
  public async Task FilterByUserOrPrincipals_EachCombination_ComposesTheMatchingPredicateAsync() {
    var rows = _rows();
    var p1 = new HashSet<SecurityPrincipalId> { new("group:p1") };

    var both = rows.FilterByUserOrPrincipals("user-2", p1).ToList();
    var userWithNullSet = rows.FilterByUserOrPrincipals("user-2", null!).ToList();
    var principalsOnly = rows.FilterByUserOrPrincipals(null, p1).ToList();
    var neither = rows.FilterByUserOrPrincipals(null, new HashSet<SecurityPrincipalId>()).ToList();

    await Assert.That(both.Select(r => r.Scope.UserId)).IsEquivalentTo(["user-1", "user-2"])
      .Because("user-2's own row OR the row allowing group:p1");
    await Assert.That(userWithNullSet.Select(r => r.Scope.UserId)).IsEquivalentTo(["user-2"])
      .Because("a null principal set reads as no principals, leaving the user clause alone");
    await Assert.That(principalsOnly.Select(r => r.Scope.UserId)).IsEquivalentTo(["user-1"]);
    await Assert.That(neither).IsEmpty()
      .Because("no user and no principals grants no access");
  }

  // ===== Helpers =====

  private static async Task _assertTenantDefaultAsync(Func<object> defaultScope) {
    await Assert.That(defaultScope).Throws<InvalidOperationException>()
      .WithMessageContaining("Tenant");
  }

  private static BareContext _context() =>
    new(new DbContextOptionsBuilder<BareContext>().UseNpgsql("Host=localhost;Database=never_opened").Options);

  private static IQueryable<PerspectiveRow<Doc>> _rows() => new[] {
    _row("tenant-a", "user-1", "group:p1"),
    _row("tenant-a", "user-2", "group:p2"),
    _row("tenant-b", "user-3", "group:p3"),
  }.AsQueryable();

  private static PerspectiveRow<Doc> _row(string tenant, string user, string principal) => new() {
    Id = Guid.CreateVersion7(),
    Data = new Doc(),
    Metadata = new PerspectiveMetadata { EventType = "DocCreated", EventId = "1", Timestamp = DateTime.UtcNow },
    Scope = new PerspectiveScope { TenantId = tenant, UserId = user, AllowedPrincipals = [principal] },
    CreatedAt = DateTime.UtcNow,
    UpdatedAt = DateTime.UtcNow,
    Version = 1,
  };

  /// <summary>A perspective model with no state; only its type matters here.</summary>
  public sealed class Doc {
    /// <summary>An identifier, so the model is not an empty type.</summary>
    public Guid Id { get; init; }
  }

  /// <summary>A DbContext that maps nothing and is never opened.</summary>
  public sealed class BareContext(DbContextOptions<BareContext> options) : DbContext(options);
}
