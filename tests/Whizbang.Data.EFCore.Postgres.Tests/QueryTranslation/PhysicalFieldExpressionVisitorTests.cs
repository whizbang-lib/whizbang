using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Unit coverage for <see cref="PhysicalFieldExpressionVisitor"/>'s rewrite decision, driven
/// through hand-built expression trees rather than a query.
/// <para>
/// The end-to-end query tests prove the happy path — <c>r.Data.Price</c> becomes
/// <c>EF.Property&lt;decimal&gt;(r, "price")</c> — but they can only produce the shapes LINQ
/// happens to build. The decisions that matter here are the ones about what NOT to rewrite: a
/// rewrite that fires on the wrong shape produces an EF translation error far from its cause,
/// and one that fails to fire silently queries the JSONB document instead of the indexed column.
/// </para>
/// </summary>
/// <remarks>
/// Registers only test-local model types, so it never disturbs another class's mappings. It still
/// joins the "EFCorePostgresTests" group with the other registry-touching classes, whose
/// <c>Clear()</c> calls would otherwise wipe these registrations mid-test.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/QueryTranslation/PhysicalFieldExpressionVisitor.cs</code-under-test>
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PhysicalFieldExpressionVisitorTests {

  // ── Models and row shapes ───────────────────────────────────────────────

  public class CatalogModel {
    public decimal Price { get; init; }
    public float[]? Embedding { get; init; }
    public string Name { get; init; } = string.Empty;
  }

  /// <summary>A row type one level below <see cref="PerspectiveRow{TModel}"/>.</summary>
  public class DerivedRow<TModel> : PerspectiveRow<TModel> where TModel : class;

  /// <summary>Two levels below, so the base-type walk has to actually take a step.</summary>
  public class GrandchildRow<TModel> : DerivedRow<TModel> where TModel : class;

  /// <summary>Carries a <c>Data</c> property but is NOT a perspective row — a near miss.</summary>
  public class LookalikeRow {
    public CatalogModel Data { get; set; } = new();
  }

  /// <summary>Exposes <c>Data</c> statically, so the access has no instance expression.</summary>
  public static class StaticDataHolder {
    public static CatalogModel Data { get; } = new();
  }

  /// <summary>A field, not a property — the visitor's first guard.</summary>
  public sealed class FieldHolder {
#pragma warning disable CA1051 // Do not declare visible instance fields — a field is the point
    public int Counter;
#pragma warning restore CA1051
  }

  [Before(Test)]
  public void RegisterMappings() {
    PhysicalFieldRegistry.Register<CatalogModel>(nameof(CatalogModel.Price), "price");
    PhysicalFieldRegistry.Register<CatalogModel>(nameof(CatalogModel.Embedding), "embeddings", isVector: true);
  }

  // ── Rewrites ────────────────────────────────────────────────────────────

  [Test]
  public async Task VisitMember_PhysicalFieldOnADerivedRowType_StillRewritesAsync() {
    // A row type is recognized by what it derives from, not by being PerspectiveRow<T> exactly.
    // Miss this and every consumer that subclasses the row silently queries the JSONB document
    // instead of the indexed column — same results, no error, and the index never used.
    Expression<Func<DerivedRow<CatalogModel>, decimal>> query = r => r.Data.Price;

    var rewritten = new PhysicalFieldExpressionVisitor().Visit(query.Body);

    await Assert.That(rewritten).IsTypeOf<MethodCallExpression>()
      .Because("a registered physical field becomes an EF.Property call, not a member access");
    var call = (MethodCallExpression)rewritten;
    await Assert.That(call.Method.Name).IsEqualTo("Property");
    await Assert.That(((ConstantExpression)call.Arguments[1]).Value).IsEqualTo("price");
    await Assert.That(call.Arguments[0]).IsSameReferenceAs(query.Parameters[0])
      .Because("the rewrite targets the ROW, so the shadow property resolves on the entity");
  }

  [Test]
  public async Task VisitMember_PhysicalFieldTwoLevelsBelowPerspectiveRow_StillRewritesAsync() {
    // The base-type walk has to keep climbing, not just check the immediate base.
    Expression<Func<GrandchildRow<CatalogModel>, decimal>> query = r => r.Data.Price;

    var rewritten = new PhysicalFieldExpressionVisitor().Visit(query.Body);

    await Assert.That(rewritten).IsTypeOf<MethodCallExpression>()
      .Because("depth in the row hierarchy is not a reason to stop treating it as a row");
  }

  // ── Shapes that must be left alone ──────────────────────────────────────

  [Test]
  public async Task VisitMember_VectorPhysicalField_IsLeftAsAMemberAccessAsync() {
    // Vector fields are registered like any other physical field, but the shadow property is
    // Vector? while the model property is float[]? — EF cannot coerce between them in an
    // expression tree, so rewriting throws at translation time. Materialization goes through
    // change-tracker hydration instead.
    Expression<Func<PerspectiveRow<CatalogModel>, float[]?>> query = r => r.Data.Embedding;

    var rewritten = new PhysicalFieldExpressionVisitor().Visit(query.Body);

    await Assert.That(rewritten).IsSameReferenceAs(query.Body)
      .Because("a rewritten vector field fails EF translation with a coercion error far from here");
  }

  [Test]
  public async Task VisitMember_FieldRatherThanProperty_IsLeftAloneAsync() {
    Expression<Func<FieldHolder, int>> query = h => h.Counter;

    var rewritten = new PhysicalFieldExpressionVisitor().Visit(query.Body);

    await Assert.That(rewritten).IsSameReferenceAs(query.Body)
      .Because("physical fields map model PROPERTIES; a field access is not a candidate at all");
  }

  [Test]
  public async Task VisitMember_DataPropertyOnANonRowType_IsLeftAloneAsync() {
    // The guard is not "is it called Data" — a type that merely has a Data property owns its own
    // storage, and rewriting its member access to EF.Property would name a column that does not
    // exist on it.
    Expression<Func<LookalikeRow, decimal>> query = r => r.Data.Price;

    var rewritten = new PhysicalFieldExpressionVisitor().Visit(query.Body);

    await Assert.That(rewritten).IsSameReferenceAs(query.Body)
      .Because("the type has to BE a perspective row, not just resemble one");
  }

  [Test]
  public async Task VisitMember_StaticDataAccess_IsLeftAloneAsync() {
    // A static Data has no instance expression, so there is no entity to rewrite the access
    // against. Hand-built because LINQ will not produce this shape from a query.
    var staticData = Expression.Property(null, typeof(StaticDataHolder), nameof(StaticDataHolder.Data));
    var access = Expression.Property(staticData, nameof(CatalogModel.Price));

    var rewritten = new PhysicalFieldExpressionVisitor().Visit(access);

    await Assert.That(rewritten).IsSameReferenceAs(access)
      .Because("there is no row instance here to resolve a shadow property on");
  }

  [Test]
  public async Task VisitMember_UnregisteredPropertyOnARow_IsLeftAloneAsync() {
    // Name is stored in the JSONB document, not promoted to a column — rewriting it would name
    // a shadow property the model never declared.
    Expression<Func<PerspectiveRow<CatalogModel>, string>> query = r => r.Data.Name;

    var rewritten = new PhysicalFieldExpressionVisitor().Visit(query.Body);

    await Assert.That(rewritten).IsSameReferenceAs(query.Body)
      .Because("only REGISTERED physical fields have a column to redirect to");
  }
}
