#pragma warning disable CA1707

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Unit coverage for <see cref="CollectiveWhereComposer"/> — the per-model WHERE composition that lets a
/// collective-apply handler project the scope onto its own model. Pure expression composition, no database:
/// <list type="bullet">
///   <item><description><c>Framework</c> + no handler Where → resolver scope filter alone (unchanged).</description></item>
///   <item><description><c>Framework</c> + handler Where → <c>scope AND handlerWhere</c> (handler refines within the envelope).</description></item>
///   <item><description><c>Custom</c> + handler Where → handler Where alone (handler owns the full predicate).</description></item>
///   <item><description><c>Custom</c> + no handler Where → throws (misconfiguration).</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public class CollectiveWhereComposerTests {

  private sealed class _job {
    public string Status { get; set; } = "Draft";
  }

  private static PerspectiveRow<_job> _row(string status, string? tenant) => new() {
    Id = TrackedGuid.NewMedo().Value,
    Data = new _job { Status = status },
    Metadata = new PerspectiveMetadata(),
    Scope = new PerspectiveScope { TenantId = tenant },
    CreatedAt = default,
    UpdatedAt = default,
    Version = 0,
  };

  [Test]
  public async Task Framework_NoHandlerWhere_ReturnsScopeFilterUnchangedAsync() {
    Expression<Func<PerspectiveRow<_job>, bool>> scope = r => r.Scope.TenantId == "t-1";

    var composed = CollectiveWhereComposer.Compose(CollectiveScopeHandling.Framework, scope, handlerWhere: null);

    await Assert.That(composed).IsSameReferenceAs(scope)
      .Because("With no handler Where, the framework path is the resolver's scope filter verbatim — no wrapping.");
  }

  [Test]
  public async Task Framework_WithHandlerWhere_AndsScopeAndHandlerAsync() {
    Expression<Func<PerspectiveRow<_job>, bool>> scope = r => r.Scope.TenantId == "t-1";
    Expression<Func<PerspectiveRow<_job>, bool>> where = r => r.Data.Status == "Draft";

    var predicate = CollectiveWhereComposer
      .Compose(CollectiveScopeHandling.Framework, scope, where)
      .Compile();

    // In tenant AND Draft → matches; in tenant but Approved → excluded; wrong tenant but Draft → excluded.
    await Assert.That(predicate(_row("Draft", "t-1"))).IsTrue();
    await Assert.That(predicate(_row("Approved", "t-1"))).IsFalse()
      .Because("The handler Where refines within the scope envelope — Approved rows fall out.");
    await Assert.That(predicate(_row("Draft", "t-2"))).IsFalse()
      .Because("The scope envelope still binds — a different tenant is excluded even when the handler Where matches.");
  }

  [Test]
  public async Task Custom_WithHandlerWhere_UsesHandlerWhereAloneAsync() {
    // Custom: a scope filter that would EXCLUDE everything must be ignored — the handler owns the WHERE.
    Expression<Func<PerspectiveRow<_job>, bool>> excludeAll = _ => false;
    Expression<Func<PerspectiveRow<_job>, bool>> where = r => r.Data.Status == "Draft";

    var predicate = CollectiveWhereComposer
      .Compose(CollectiveScopeHandling.Custom, excludeAll, where)
      .Compile();

    await Assert.That(predicate(_row("Draft", "t-1"))).IsTrue()
      .Because("Custom ignores the resolver scope filter; only the handler's Where gates the update.");
    await Assert.That(predicate(_row("Approved", "t-1"))).IsFalse();
  }

  [Test]
  public async Task Custom_NullScopeFilter_IsAllowed_HandlerWhereWinsAsync() {
    // Custom must not require a scope filter at all — a custom-scope resolver may be unable to produce a
    // model-agnostic one (that's the reason to pick Custom). The applier passes null; the handler Where wins.
    Expression<Func<PerspectiveRow<_job>, bool>> where = r => r.Data.Status == "Draft";

    var predicate = CollectiveWhereComposer
      .Compose(CollectiveScopeHandling.Custom, scopeFilter: null, where)
      .Compile();

    await Assert.That(predicate(_row("Draft", "t-1"))).IsTrue();
    await Assert.That(predicate(_row("Approved", "t-1"))).IsFalse();
  }

  [Test]
  public async Task Custom_NoHandlerWhere_ThrowsAsync() {
    Expression<Func<PerspectiveRow<_job>, bool>> scope = r => r.Scope.TenantId == "t-1";

    await Assert.That(() => CollectiveWhereComposer.Compose(CollectiveScopeHandling.Custom, scope, handlerWhere: null))
      .Throws<InvalidOperationException>()
      .Because("Custom means the handler owns the WHERE; supplying none is a misconfiguration, not an empty predicate.");
  }

  [Test]
  public async Task NullScopeFilter_OnFramework_ThrowsArgumentNullAsync() {
    await Assert.That(() => CollectiveWhereComposer.Compose<_job>(
        CollectiveScopeHandling.Framework, scopeFilter: null!, handlerWhere: null))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task UnknownScopeHandling_ThrowsArgumentOutOfRangeAsync() {
    Expression<Func<PerspectiveRow<_job>, bool>> scope = r => r.Scope.TenantId == "t-1";

    await Assert.That(() => CollectiveWhereComposer.Compose((CollectiveScopeHandling)999, scope, handlerWhere: null))
      .Throws<ArgumentOutOfRangeException>();
  }
}
