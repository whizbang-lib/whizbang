#pragma warning disable CA1707

using System.Linq.Expressions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Coverage for the <see cref="CollectiveSettersRewriter"/> corners
/// <see cref="CollectiveSettersRewriterTests"/> doesn't reach: the visitor's "not a SetProperty
/// call" early return, the widening-conversion strip on a computed comparison's compared property,
/// and the selector-argument shape guard. All three are pure expression-tree walks — no database
/// involved anywhere in this file.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
[Category("Shard1")]
public class CollectiveSettersRewriterCoverageTests {

  // A setters rewriter changes how state is written; a call it silently mistakes for a setter
  // corrupts data with no exception at the point of damage. This proves the opposite: a call the
  // visitor doesn't recognize is left alone rather than treated as one, so the spec fails the same
  // loud way an empty spec does (zero SetProperty calls) instead of writing a wrong or partial UPDATE.
  [Test]
  public async Task CollectAssignments_SpecBodyCallsAnUnrelatedMethod_ProducesZeroAssignmentsAsync() {
    // ToString() is callable on any interface-typed reference (forwarded from object) but is not a
    // SetProperty call the visitor's method-shape guard recognizes.
    Expression<Action<ICollectiveSetters<_probeModel>>> source = s => s.ToString();

    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments(source))
      .ThrowsExactly<InvalidOperationException>()
      .Because("a call the rewriter doesn't recognize as SetProperty must never be silently treated "
             + "as one; the visitor leaves it untouched, so the spec ends up with the same "
             + "zero-assignment failure as an empty spec rather than a wrong or partial mutation");
  }

  // A computed comparison's compared-property lookup pattern-matches a MemberExpression directly
  // against the lambda parameter. Comparing an int property to a long forces the compiler to wrap
  // the property access in Convert(j.ViewCount, Int64) first — without stripping that wrapper, a
  // completely legal spec would fail to resolve which property it compares against.
  [Test]
  public async Task CollectAssignments_ComputedComparisonAgainstNarrowerProperty_StripsTheWideningConversionAsync() {
    Expression<Action<ICollectiveSetters<_probeModel>>> source =
      s => s.SetProperty(j => j.IsActive, j => j.ViewCount == 5L);

    var assignments = CollectiveSettersRewriter.CollectAssignments(source);

    await Assert.That(assignments.Count).IsEqualTo(1)
      .Because("one computed SetProperty call produces one assignment");
    await Assert.That(assignments[0].PathName).IsEqualTo("IsActive");
    await Assert.That(assignments[0].Comparison).IsNotNull()
      .Because("stripping the implicit int-to-long conversion is what lets the compared-property "
             + "pattern match succeed at all");
    await Assert.That(assignments[0].Comparison!.ComparedProperty).IsEqualTo("ViewCount")
      .Because("the property being compared is ViewCount, not some artifact of the conversion node");
    await Assert.That(assignments[0].Comparison!.SqlOperator).IsEqualTo("=");
    await Assert.That(assignments[0].JsonValue).IsEqualTo("5")
      .Because("the RHS constant survives the widening as the number it was");
  }

  // The selector argument is only ever a lambda (quoted or direct) when a spec is written normally.
  // A hand-built expression tree that substitutes something else into that slot — the kind of bug a
  // generator or adapter could introduce — has to fail with a message naming the problem, rather
  // than a NullReferenceException deep inside a pattern match or a silently wrong property.
  [Test]
  public async Task CollectAssignments_SelectorArgumentIsNotActuallyALambda_NamesTheProblemAsync() {
    var sParam = Expression.Parameter(typeof(ICollectiveSetters<_probeModel>), "s");
    var setProperty = typeof(ICollectiveSetters<_probeModel>)
      .GetMethods()
      .Single(m => m.Name == "SetProperty" && m.GetParameters()[1].ParameterType.IsGenericParameter)
      .MakeGenericMethod(typeof(int));
    // A ConstantExpression whose declared Type exactly matches the selector parameter's Expression<>
    // type — so it passes Expression.Call's argument-type check without being wrapped — but whose
    // node shape is not a lambda at all.
    var notActuallyALambda = Expression.Constant(null, typeof(Expression<Func<_probeModel, int>>));
    var call = Expression.Call(sParam, setProperty, notActuallyALambda, Expression.Constant(0));
    var source = Expression.Lambda<Action<ICollectiveSetters<_probeModel>>>(call, sParam);

    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments(source))
      .ThrowsExactly<InvalidOperationException>()
      .Because("a selector argument that isn't a lambda leaves no property selector to fall back "
             + "to, so the failure has to say so explicitly instead of matching nothing silently");
  }

  private sealed class _probeModel {
    public int ViewCount { get; set; }
    public bool IsActive { get; set; }
  }
}
