#pragma warning disable CA1707

using System.Collections.Generic;
using System.Linq.Expressions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Locks the behavior of the first baked-in <see cref="ICollectiveScopeResolver"/>
/// (Slice 4 of the collective-events feature). The resolver answers for
/// <see cref="TenantCollectiveScope"/> events: it advertises
/// <c>ScopeKind = "tenant"</c>, accepts every perspective (all rows have
/// a <see cref="PerspectiveScope"/>), and emits a filter expression that
/// matches <c>row.Scope.TenantId == scope.TenantId</c>. <c>EnterContext</c>
/// installs an ambient <see cref="ScopeContextAccessor"/> for the
/// duration of a <c>BulkApply</c> call and restores the prior context on
/// dispose.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public class TenantCollectiveScopeResolverTests {

  // ── TenantCollectiveScope ──────────────────────────────────────────────

  [Test]
  public async Task TenantCollectiveScope_ScopeKind_IsTenantAsync() {
    // Type as ICollectiveScope explicitly to assert the interface property
    // is what's returned, not the concrete record member. CA1859 false
    // positive here — the test is the discriminator-via-interface contract.
#pragma warning disable CA1859
    ICollectiveScope scope = new TenantCollectiveScope("00000000-0000-0000-0000-000000000001");
#pragma warning restore CA1859

    await Assert.That(scope.ScopeKind).IsEqualTo("tenant")
      .Because("The string is the routing key the resolver registry uses to dispatch incoming collective events to the right resolver. 'tenant' must be stable across versions.");
  }

  [Test]
  public async Task TenantCollectiveScope_CarriesTenantIdAsync() {
    var scope = new TenantCollectiveScope("tenant-abc");

    await Assert.That(scope.TenantId).IsEqualTo("tenant-abc");
  }

  // ── TenantCollectiveScopeResolver: identity + routing ──────────────────

  [Test]
  public async Task Resolver_ScopeKind_MatchesScopeAsync() {
    var resolver = new TenantCollectiveScopeResolver();

    await Assert.That(resolver.ScopeKind).IsEqualTo("tenant")
      .Because("Resolver discriminator must equal the scope kind it handles so DI registration can dispatch unambiguously.");
  }

  [Test]
  public async Task Resolver_AcceptsPerspective_AllowsAnyModelAsync() {
    var resolver = new TenantCollectiveScopeResolver();

    // The PerspectiveRow<TModel> shape always carries a Scope column, so
    // tenant filtering applies regardless of TModel. Per-model opt-out is
    // a future concern; the first cut accepts all.
    await Assert.That(resolver.AcceptsPerspective<_jobModel>()).IsTrue();
    await Assert.That(resolver.AcceptsPerspective<_userModel>()).IsTrue();
  }

  // ── ScopeFilter ────────────────────────────────────────────────────────

  [Test]
  public async Task ScopeFilter_ProducesNonNullExpressionForTenantScopeAsync() {
    var resolver = new TenantCollectiveScopeResolver();
    var scope = new TenantCollectiveScope("tenant-xyz");

    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = resolver.ScopeFilter<_jobModel>(scope);

    await Assert.That(filter).IsNotNull();
  }

  [Test]
  public async Task ScopeFilter_CompiledExpression_MatchesRowsByTenantIdAsync() {
    var resolver = new TenantCollectiveScopeResolver();
    var scope = new TenantCollectiveScope("tenant-xyz");
    var filter = resolver.ScopeFilter<_jobModel>(scope).Compile();

    var matching = _row(tenantId: "tenant-xyz");
    var nonMatching = _row(tenantId: "tenant-other");
    var nullScope = _row(tenantId: null);

    await Assert.That(filter(matching)).IsTrue()
      .Because("Row whose Scope.TenantId equals the captured scope tenant MUST be in the filter's truth set.");
    await Assert.That(filter(nonMatching)).IsFalse()
      .Because("Different tenant must be excluded so a tenant admin can't accidentally mutate another tenant's rows.");
    await Assert.That(filter(nullScope)).IsFalse()
      .Because("Tenantless (null) rows are excluded from tenant-scoped collective mutations.");
  }

  [Test]
  public async Task ScopeFilter_WithWrongScopeKind_ThrowsArgumentExceptionAsync() {
    var resolver = new TenantCollectiveScopeResolver();
    ICollectiveScope wrongScope = new _customScope();

    await Assert.That(() => resolver.ScopeFilter<_jobModel>(wrongScope))
      .ThrowsExactly<ArgumentException>()
      .Because("Defensive guard: a tenant resolver should NEVER be handed a non-tenant scope. If it is, the registration is misconfigured and silently returning a 'match all' filter would over-mutate.");
  }

  // ── EnterContext ──────────────────────────────────────────────────────

  [Test]
  public async Task EnterContext_SetsAmbientScopeContextWithTenantIdAsync() {
    var resolver = new TenantCollectiveScopeResolver();
    var scope = new TenantCollectiveScope("tenant-A");
    var prior = ScopeContextAccessor.CurrentContext;

    try {
      using (resolver.EnterContext(scope)) {
        await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull();
        await Assert.That(ScopeContextAccessor.CurrentContext!.Scope.TenantId).IsEqualTo("tenant-A")
          .Because("Perspectives in [CollectiveApplyFor(ScopeHandling = Custom)] mode read ScopeContextAccessor.CurrentContext to construct their own WHERE clause — the tenant id must be visible there.");
      }
    } finally {
      ScopeContextAccessor.CurrentContext = prior;
    }
  }

  [Test]
  public async Task EnterContext_DisposeRestoresPriorContextAsync() {
    var resolver = new TenantCollectiveScopeResolver();
    var scope = new TenantCollectiveScope("tenant-B");
    var prior = ScopeContextAccessor.CurrentContext;

    var inside = resolver.EnterContext(scope);
    var insideContext = ScopeContextAccessor.CurrentContext;
    inside.Dispose();
    var afterDispose = ScopeContextAccessor.CurrentContext;

    try {
      await Assert.That(insideContext).IsNotNull();
      await Assert.That(afterDispose).IsEqualTo(prior)
        .Because("Restoring the prior context on Dispose keeps the resolver re-entrant — nested BulkApply or upstream code that set a context must not have its context clobbered.");
    } finally {
      ScopeContextAccessor.CurrentContext = prior;
    }
  }

  [Test]
  public async Task EnterContext_WithWrongScopeKind_ThrowsArgumentExceptionAsync() {
    var resolver = new TenantCollectiveScopeResolver();
    ICollectiveScope wrongScope = new _customScope();

    await Assert.That(() => resolver.EnterContext(wrongScope))
      .ThrowsExactly<ArgumentException>()
      .Because("Same defensive guard as ScopeFilter — a tenant resolver handed a non-tenant scope is a registration bug.");
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  private sealed class _jobModel {
    public string Title { get; set; } = string.Empty;
  }

  private sealed class _userModel {
    public string Name { get; set; } = string.Empty;
  }

  private sealed record _customScope : ICollectiveScope {
    public string ScopeKind => "custom-other";
  }

  private static PerspectiveRow<_jobModel> _row(string? tenantId) =>
    new() {
      Id = Guid.NewGuid(),
      Data = new _jobModel { Title = "t" },
      Metadata = new PerspectiveMetadata(),
      Scope = new PerspectiveScope { TenantId = tenantId },
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1,
    };
}
