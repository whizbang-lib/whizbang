using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// The shapes the tree rewriter declines, asserted on the tree rather than on the SQL.
/// </summary>
/// <remarks>
/// <para>
/// The matrix asserts where a filter lands by reading the compiled SQL, which is the right test for
/// anything a repository can write. It cannot reach these cases. Each one is a shape the rewriter has
/// to recognize and leave alone, and for most of them Entity Framework refuses the query outright
/// afterwards, so there is no SQL to read: the refusal would be the same whether the rewriter had
/// quietly planted a marker first or not.
/// </para>
/// <para>
/// So the rewriter is driven directly and the result is inspected for a planted marker. That makes the
/// assertion exact. It is also the only way to tell "declined" from "never asked", which is the
/// distinction that matters when the guard is what keeps a filter from compiling into a test that
/// matches nothing.
/// </para>
/// <para>
/// Nothing here opens a connection or needs a provider. A model is built for its metadata, which is
/// what the rewriter consults, and the trees are built with <c>AsQueryable</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <docs>contributors/perspective-query-pipeline</docs>
[Category("Shard1")]
// Serialized with every other class that touches the containment switch, which is process static.
[NotInParallel("EFCorePostgresTests")]
public class JsonbContainmentRewriterGuardTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=guards;Username=u;Password=p";

  /// <summary>A static member, whose expression chain ends at nothing rather than at a row.</summary>
  private static string Elsewhere => "x";

  /// <summary>
  /// Carries <c>Equals</c> overloads of arities the rewriter does not expect.
  /// </summary>
  /// <remarks>
  /// The rewriter matches on the method's name and return type, because <c>Equals</c> arrives from
  /// <c>string</c>, from <c>object</c> and from any type that declares its own. So a type with a
  /// three-argument instance <c>Equals</c>, or a one-argument static one, reaches the same code path as
  /// the real thing and must be declined rather than have its arguments guessed at.
  /// </remarks>
  [SuppressMessage("Design", "CA1067:Override Object.Equals(object) when implementing IEquatable<T>",
    Justification = "Neither overload is an equality contract; they exist to give the rewriter an "
      + "arity it does not expect, which is the case under test.")]
  public sealed class Oddity {
    public string Label { get; init; } = string.Empty;

    /// <summary>A three-argument instance overload, which the rewriter must decline.</summary>
    public bool Equals(int first, int second, int third) => first == second && second == third;

    /// <summary>A one-argument static overload, which the rewriter must decline.</summary>
    public static bool Equals(int only) => only == 0;
  }

  public sealed class NestedGuard {
    public string Value { get; init; } = string.Empty;
  }

  [SuppressIndexAdvisory("compile-only fixture, never run")]
  public class GuardModel {
    public string Code { get; init; } = string.Empty;

    /// <summary>Left out of the mapping, so the model has no property for this leaf.</summary>
    public string Unmapped { get; init; } = string.Empty;

    /// <summary>A collection held by the row, which cannot be a candidate set.</summary>
    public List<string> Names { get; init; } = [];

    /// <summary>Named <c>Data</c> on purpose: the chain walk has to not mistake it for the row's.</summary>
    public NestedGuard Data { get; init; } = new();

    public Oddity Odd { get; init; } = new();
  }

  /// <summary>A model whose document is one serialized value, as a polymorphic model's is.</summary>
  [SuppressIndexAdvisory("compile-only fixture, never run")]
  public class OpaqueModel {
    public string Code { get; init; } = string.Empty;
  }

  private sealed class GuardDbContext(DbContextOptions<GuardDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<GuardModel>>(entity => {
        entity.ToTable("wh_per_guard");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => {
          d.ToJson("data");
          d.ComplexProperty(p => p.Data);
          d.ComplexProperty(p => p.Odd);
          // Deliberately absent from the mapping, so FindProperty has nothing to return for it.
          d.Ignore(p => p.Unmapped);
          d.Ignore(p => p.Names);
        });
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").IsRequired();
      });

      // The mapping a polymorphic model gets: the whole document is one jsonb value rather than
      // property-by-property, so nothing inside it is a mapped property.
      modelBuilder.Entity<PerspectiveRow<OpaqueModel>>(entity => {
        entity.ToTable("wh_per_opaque");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb");
        entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").IsRequired();
      });

      modelBuilder.UseWhizbangJsonbContainment();
    }
  }

  private static readonly DbContextOptions<GuardDbContext> _options =
    new DbContextOptionsBuilder<GuardDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static GuardDbContext _newContext() => new(_options);

  [Before(Test)]
  public void SelectTheTreeRewrite() => JsonbContainmentSwitch.SetMode(ContainmentMode.ExpressionTree);

  [After(Test)]
  public void RestoreDefault() => JsonbContainmentSwitch.Reset();

  /// <summary>Finds a planted marker anywhere in a tree.</summary>
  private sealed class MarkerFinder : ExpressionVisitor {
    public bool Found { get; private set; }

    protected override Expression VisitMethodCall(MethodCallExpression node) {
      if (node.Method.DeclaringType == typeof(JsonbContainment)) {
        Found = true;
      }

      return base.VisitMethodCall(node);
    }
  }

  /// <summary>Whether the rewriter planted a marker for this predicate.</summary>
  private static bool _plantsAMarkerFor<TModel>(Expression<Func<PerspectiveRow<TModel>, bool>> predicate)
      where TModel : class {
    using var db = _newContext();

    var query = Enumerable.Empty<PerspectiveRow<TModel>>().AsQueryable().Where(predicate);
    var rewritten = new JsonbContainmentRewriter(db.Model).Visit(query.Expression);

    var finder = new MarkerFinder();
    finder.Visit(rewritten);
    return finder.Found;
  }

  /// <summary>Whether the rewriter planted a marker for a predicate over a projected model.</summary>
  private static bool _plantsAMarkerForProjection<TModel, TProjected>(
      Expression<Func<PerspectiveRow<TModel>, TProjected>> projection,
      Expression<Func<TProjected, bool>> predicate)
      where TModel : class {
    using var db = _newContext();

    var query = Enumerable.Empty<PerspectiveRow<TModel>>().AsQueryable()
      .Select(projection)
      .Where(predicate);
    var rewritten = new JsonbContainmentRewriter(db.Model).Visit(query.Expression);

    var finder = new MarkerFinder();
    finder.Visit(rewritten);
    return finder.Found;
  }

  /// <summary>
  /// An asynchronous filtering operator is a filtering operator.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The operator names are matched after an <c>Async</c> suffix is stripped, because the same
  /// predicates arrive from <c>Queryable</c>, from <c>Enumerable</c> and from Entity Framework's
  /// asynchronous extensions. A repository writes <c>AnyAsync</c> far more often than <c>Any</c>, so
  /// this is the common path rather than an edge of one: without the strip, the name would not match
  /// and every filter written asynchronously would quietly keep the extraction form.
  /// </para>
  /// <para>
  /// The call is built rather than written, because <c>AnyAsync</c> executes against a provider and
  /// what is needed here is only its tree. The method comes from a delegate over a statically
  /// referenced method, so nothing is looked up by name.
  /// </para>
  /// </remarks>
  [Test]
  public async Task AnAsynchronousOperatorIsStillAPredicateAsync() {
    await using var db = _newContext();

    var source = Enumerable.Empty<PerspectiveRow<GuardModel>>().AsQueryable();
    Expression<Func<PerspectiveRow<GuardModel>, bool>> predicate = r => r.Data.Code == "v";

    var anyAsync = ((Func<IQueryable<PerspectiveRow<GuardModel>>,
                          Expression<Func<PerspectiveRow<GuardModel>, bool>>,
                          CancellationToken,
                          Task<bool>>)EntityFrameworkQueryableExtensions.AnyAsync).Method;

    var call = Expression.Call(
      anyAsync,
      source.Expression,
      Expression.Quote(predicate),
      Expression.Constant(CancellationToken.None));

    var rewritten = new JsonbContainmentRewriter(db.Model).Visit(call);

    var finder = new MarkerFinder();
    finder.Visit(rewritten);

    await Assert.That(finder.Found).IsTrue()
      .Because("AnyAsync is Any, and a filter written asynchronously must reach the index the "
        + "synchronous spelling reaches");
  }

  /// <summary>
  /// An ordinary filter is rewritten, so every decline below is a decline and not a dead harness.
  /// </summary>
  /// <remarks>
  /// The control case, and it earns its place: a harness that never planted anything would report
  /// every one of the assertions below as passing.
  /// </remarks>
  [Test]
  public async Task TheHarnessDoesPlantAMarkerForAnOrdinaryFilterAsync() =>
    await Assert.That(_plantsAMarkerFor<GuardModel>(r => r.Data.Code == "v")).IsTrue()
      .Because("without this the declines below would pass on a rewriter that never ran at all");

  /// <summary>
  /// An <c>Equals</c> whose arity is not one of the two known shapes is declined.
  /// </summary>
  /// <remarks>
  /// The rewriter recognizes <c>Equals</c> by name, because the same name arrives from <c>string</c>,
  /// from <c>object</c> and from any model type that declares its own. A declared overload of another
  /// arity therefore reaches the same code, and its arguments are not a member and a value.
  /// </remarks>
  [Test]
  [Arguments("three-argument instance")]
  [Arguments("one-argument static")]
  public async Task AnEqualsOfAnUnexpectedArityIsDeclinedAsync(string shape) {
    var plants = shape switch {
      "three-argument instance" => _plantsAMarkerFor<GuardModel>(r => r.Data.Odd.Equals(1, 1, 1)),
      "one-argument static" => _plantsAMarkerFor<GuardModel>(_ => Oddity.Equals(0)),
      _ => throw new InvalidOperationException(shape),
    };

    await Assert.That(plants).IsFalse()
      .Because("the name is all the rewriter matched on, so an overload it does not know the shape of "
        + "must be left alone rather than have its first two arguments treated as a comparison");
  }

  /// <summary>
  /// A <c>Contains</c> whose shape is not a collection and a member is declined.
  /// </summary>
  /// <remarks>
  /// Substring matching is spelled the same way as set membership. <c>Contains</c> on a string with a
  /// comparison argument is two arguments on an instance, which is neither of the membership shapes,
  /// and compiling it as one would ask whether a document holds a substring as a value.
  /// </remarks>
  [Test]
  public async Task ASubstringContainsIsNotTreatedAsSetMembershipAsync() =>
    await Assert.That(_plantsAMarkerFor<GuardModel>(
        r => r.Data.Code.Contains("value", StringComparison.Ordinal))).IsFalse()
      .Because("substring matching and set membership share a name and mean different things");

  /// <summary>
  /// Candidates read out of the row being filtered are declined, because they are not a value.
  /// </summary>
  /// <remarks>
  /// The membership helper builds one document per candidate while extracting parameters, which it can
  /// only do for a collection whose contents are known then. A collection read from the row varies per
  /// row, so there is nothing to build the documents from.
  /// </remarks>
  [Test]
  public async Task CandidatesReadFromTheRowAreDeclinedAsync() =>
    await Assert.That(_plantsAMarkerFor<GuardModel>(r => r.Data.Names.Contains(r.Data.Code))).IsFalse()
      .Because("the candidate set has to be known while parameters are extracted, and a collection "
        + "held by the row is not");

  /// <summary>
  /// A member of something other than a perspective row is declined.
  /// </summary>
  /// <remarks>
  /// A static member's chain ends at nothing rather than at a row or a range variable, which is the
  /// walk's other terminating case and the one with no document behind it.
  /// </remarks>
  [Test]
  public async Task AStaticMemberIsDeclinedAsync() =>
    await Assert.That(_plantsAMarkerFor<GuardModel>(_ => Elsewhere == "x")).IsFalse()
      .Because("nothing about a static read is a path into a document");

  /// <summary>
  /// A model member named <c>Data</c> is not mistaken for the row's own.
  /// </summary>
  /// <remarks>
  /// The walk recognizes the document by finding a member called <c>Data</c> whose owner is a
  /// perspective row. A model that happens to have its own <c>Data</c> property would be matched by a
  /// name test alone, so the owner is checked too, and this is what proves it.
  /// </remarks>
  [Test]
  public async Task AModelsOwnDataMemberIsStillReachedThroughTheRowsAsync() =>
    await Assert.That(_plantsAMarkerFor<GuardModel>(r => r.Data.Data.Value == "v")).IsTrue()
      .Because("the inner Data belongs to the model, so the walk has to keep going and find the row's");

  /// <summary>
  /// A projection to something that is not a perspective model is declined.
  /// </summary>
  /// <remarks>
  /// A predicate over a projected model is a path into the same document, which is why the parameter's
  /// type is asked about at all. A projection to a scalar or to a string is not, and treating either as
  /// one would read an ordinary property as a document key.
  /// </remarks>
  [Test]
  [Arguments("a value type")]
  [Arguments("a string")]
  public async Task AProjectionToSomethingThatIsNotAModelIsDeclinedAsync(string projected) {
    var plants = projected switch {
      "a value type" => _plantsAMarkerForProjection<GuardModel, int>(
        r => r.Data.Code.Length, n => n == 3),
      "a string" => _plantsAMarkerForProjection<GuardModel, string>(
        r => r.Data.Code, s => s.Length == 3),
      _ => throw new InvalidOperationException(projected),
    };

    await Assert.That(plants).IsFalse()
      .Because("only a type a perspective is stored for makes a bare member of it a document path");
  }

  /// <summary>
  /// A member the model does not map is declined, because nothing says how it is stored.
  /// </summary>
  /// <remarks>
  /// Standing down is the safe answer for an unknown. A property absent from the mapping has no type
  /// mapping and therefore no way to know whether a converter changed its stored form, and building a
  /// containment document on that assumption is how a filter starts matching nothing.
  /// </remarks>
  [Test]
  public async Task AnUnmappedMemberIsDeclinedAsync() =>
    await Assert.That(_plantsAMarkerFor<GuardModel>(r => r.Data.Unmapped == "v")).IsFalse()
      .Because("with no mapped property there is nothing that says how the value is stored, and "
        + "assuming is what compiles a filter that matches nothing");

  /// <summary>
  /// A model stored as one serialized value is declined, because nothing inside it is mapped.
  /// </summary>
  /// <remarks>
  /// This is the storage mode a polymorphic model gets, where the document is a single jsonb value
  /// rather than property-by-property. There is no mapped property to read a converter from and no
  /// extraction the rewrite could replace, which is the same reason WHIZ304 reports an index declared
  /// over such a model.
  /// </remarks>
  [Test]
  public async Task AnOpaquelyStoredModelIsDeclinedAsync() =>
    await Assert.That(_plantsAMarkerFor<OpaqueModel>(r => r.Data.Code == "v")).IsFalse()
      .Because("the document is one value, so there is no mapped property behind this member at all");
}
