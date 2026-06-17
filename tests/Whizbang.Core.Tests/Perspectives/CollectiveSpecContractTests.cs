#pragma warning disable CA1707

using System.Collections.Generic;
using System.Linq.Expressions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Locks the collective-events perspective surface: <see cref="ICollectiveSpec{TModel}"/>,
/// <see cref="ICollectiveSetters{TModel}"/>, <see cref="ICollectiveApplyFor{TModel, TEvent}"/>,
/// <see cref="ICollectiveScopeResolver"/>, and <see cref="CollectiveApplyForAttribute"/>.
/// These contracts have no runtime behavior in Slice 1; the tests pin the
/// shape so the source generator (Slice 5), the EFCore adapter (Slice 6),
/// and the Dapper adapter (Slice 9) all bind against a stable surface.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public class CollectiveSpecContractTests {

  [Test]
  public async Task ICollectiveSpec_Setters_IsLinqExpressionTreeAsync() {
    // The spec's only payload is an Expression<Action<ICollectiveSetters<TModel>>>.
    // Driver adapters (EF, Dapper) walk this expression with ExpressionVisitor;
    // emitting a delegate instead of an expression would break the adapter
    // contract and trip AOT trim warnings on reflection-based fallbacks.
    Expression<Action<ICollectiveSetters<_jobModel>>> setters =
      s => s.SetProperty(j => j.Status, "Archived");
    ICollectiveSpec<_jobModel> spec = new _archiveSpec(setters);

    await Assert.That(spec.Setters).IsNotNull();
    await Assert.That(spec.Setters).IsSameReferenceAs(setters)
      .Because("The spec holds the expression as-passed; adapters depend on the tree being inspectable, not pre-compiled.");
  }

  [Test]
  public async Task ICollectiveSetters_ConstantSetProperty_OverloadCompilesAsync() {
    // Overload (a): SetProperty(selector, value). The constant overload
    // is the most common shape — "set field X to value V."
    Expression<Action<ICollectiveSetters<_jobModel>>> expr =
      s => s.SetProperty(j => j.Status, "Archived");

    // The expression compiled — the SetProperty(selector, value) overload
    // exists with the expected signature. (Runtime expression body
    // inspection would be a Slice 6 adapter test.)
    await Assert.That(expr).IsNotNull();
  }

  [Test]
  public async Task ICollectiveSetters_ComputedSetProperty_OverloadCompilesAsync() {
    // Overload (b): SetProperty(selector, computed). The computed overload
    // accepts an Expression<Func<TModel, TProp>> for the new value, which
    // lets the spec reference the row's CURRENT property values — needed
    // for increment, append, conditional-rewrite patterns.
    Expression<Action<ICollectiveSetters<_jobModel>>> expr =
      s => s.SetProperty(j => j.ViewCount, j => j.ViewCount + 1);

    await Assert.That(expr).IsNotNull();
  }

  [Test]
  public async Task ICollectiveApplyFor_TEventConstraint_IsICollectiveEventAsync() {
    // The generic constraint `where TEvent : ICollectiveEvent` is what
    // distinguishes the collective Apply path from the existing per-row
    // IPerspectiveFor.Apply path. Source generators (Slice 5) discover
    // these handlers by matching on this constraint.
    ICollectiveApplyFor<_jobModel, _archiveCollectiveEvent> apply = new _archiveApply();

    // Compile-time proof: the type system accepts a TEvent that implements
    // ICollectiveEvent. A non-collective TEvent would not compile.
    await Assert.That(apply).IsNotNull();
  }

  [Test]
  public async Task ICollectiveScopeResolver_HasScopeKindDiscriminatorAsync() {
    // The resolver carries the same ScopeKind discriminator as the scope
    // payload so DI registration can key on a stable string. Multiple
    // resolvers for the same kind would be an error at registration time
    // (deferred to Slice 4); the contract just requires the property.
    ICollectiveScopeResolver resolver = new _tenantResolverStub();

    await Assert.That(resolver.ScopeKind).IsEqualTo("tenant");
  }

  [Test]
  public async Task CollectiveApplyForAttribute_CarriesScopeHandlingDefaultAsync() {
    // Default ScopeHandling is Framework — meaning the framework composes
    // the resolver's ScopeFilter around the perspective's mutation spec.
    // The opt-out Custom mode is what lets the perspective consult ambient
    // ScopeContext and write its own WHERE.
    var attr = new CollectiveApplyForAttribute();

    await Assert.That(attr.ScopeHandling).IsEqualTo(CollectiveScopeHandling.Framework)
      .Because("Safe default: framework composes the scope filter so perspectives can't accidentally over-mutate. Custom is the explicit opt-out.");
    await Assert.That(attr.SpecKind).IsEqualTo(CollectiveSpecKind.Linq)
      .Because("Default spec kind is LINQ. RawSql is the escape hatch when the LINQ surface can't express the mutation.");
  }

  [Test]
  public async Task CollectiveApplyForAttribute_AcceptsExplicitScopeHandlingCustomAsync() {
    var attr = new CollectiveApplyForAttribute {
      ScopeHandling = CollectiveScopeHandling.Custom,
    };

    await Assert.That(attr.ScopeHandling).IsEqualTo(CollectiveScopeHandling.Custom);
  }

  [Test]
  public async Task CollectiveApplyForAttribute_AcceptsExplicitSpecKindRawSqlAsync() {
    var attr = new CollectiveApplyForAttribute {
      SpecKind = CollectiveSpecKind.RawSql,
    };

    await Assert.That(attr.SpecKind).IsEqualTo(CollectiveSpecKind.RawSql);
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed class _jobModel {
    public string Status { get; set; } = "Draft";
    public int ViewCount { get; set; }
  }

  private sealed record _archiveCollectiveEvent(
    ICollectiveScope Scope,
    IReadOnlyList<Guid> MatchedStreamIds) : ICollectiveEvent;

  private sealed record _archiveSpec(Expression<Action<ICollectiveSetters<_jobModel>>> Setters)
    : ICollectiveSpec<_jobModel>;

  private sealed class _archiveApply : ICollectiveApplyFor<_jobModel, _archiveCollectiveEvent> {
    public ICollectiveSpec<_jobModel> Apply(_archiveCollectiveEvent evt) =>
      new _archiveSpec(s => s.SetProperty(j => j.Status, "Archived"));
  }

  private sealed class _tenantResolverStub : ICollectiveScopeResolver {
    public string ScopeKind => "tenant";

    public bool AcceptsPerspective<TModel>() where TModel : class => true;

    public Expression<Func<PerspectiveRow<TModel>, bool>> ScopeFilter<TModel>(ICollectiveScope scope)
      where TModel : class => _ => true;

    public IDisposable EnterContext(ICollectiveScope scope) => _noopDisposable.Instance;
  }

  private sealed class _noopDisposable : IDisposable {
    public static readonly _noopDisposable Instance = new();
    public void Dispose() { }
  }
}
