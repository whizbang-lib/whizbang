using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// The ways a developer actually writes a predicate, as opposed to the one way the SQL matrix
/// writes them.
/// </summary>
/// <remarks>
/// <para>
/// The compiled-SQL matrix crosses types and operators but always spells the predicate the same way,
/// inside <c>Where</c>, with a literal <c>==</c>. Real repositories do not. They call
/// <c>FirstOrDefaultAsync</c> with the predicate inline, they use <c>string.Equals</c>, they test a
/// list with <c>Contains</c>, they compare against a field or a method result, and they project a
/// comparison into a boolean.
/// </para>
/// <para>
/// These assert on the rewritten expression tree rather than on SQL, which is what lets a terminal
/// operator like <c>Any</c> or <c>CountAsync</c> be covered at all: those return a scalar, so there
/// is no <c>IQueryable</c> left to ask for a command. The tree is also the honest place to test the
/// decision, because that is where it is made.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class JsonbContainmentAuthoringTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=authoring;Username=u;Password=p";

  /// <summary>A value-object style identifier, which serializes as a nested object rather than a scalar.</summary>
  public readonly record struct OrderNumber(Guid Value);

  [SuppressIndexAdvisory("compiled, never run")]
  public class OrderModel {
    public string Code { get; init; } = string.Empty;
    public string Other { get; init; } = string.Empty;
    public Guid Owner { get; init; }
    public bool Active { get; init; }
    public int Count { get; init; }
    public string? Note { get; init; }
    public OrderNumber Number { get; init; }
  }

  private sealed class AuthoringDbContext(DbContextOptions<AuthoringDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<OrderModel>>(entity => {
        entity.ToTable("wh_per_authoring");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => {
          d.ToJson("data");
          d.ComplexProperty(p => p.Number);
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

      modelBuilder.UseWhizbangJsonbContainment();
    }
  }

  private static readonly DbContextOptions<AuthoringDbContext> _options =
    new DbContextOptionsBuilder<AuthoringDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static readonly AuthoringDbContext _db = new(_options);

  /// <summary>
  /// Runs the rewriter over a real query carrying the predicate, and reports whether it produced a
  /// containment call. The predicate is wrapped in <c>Where</c> rather than visited bare, because a
  /// bare lambda is not what the interceptor ever sees and the rewrite is scoped to filter position.
  /// </summary>
  private static bool _rewrites(Expression<Func<PerspectiveRow<OrderModel>, bool>> predicate) {
    var query = _db.Set<PerspectiveRow<OrderModel>>().Where(predicate).Expression;
    var rewritten = new JsonbContainmentRewriter(_db.Model).Visit(query);
    var finder = new _markerFinder();
    finder.Visit(rewritten);
    return finder.Found;
  }

  private sealed class _markerFinder : ExpressionVisitor {
    public bool Found { get; private set; }

    protected override Expression VisitMethodCall(MethodCallExpression node) {
      if (node.Method.DeclaringType == typeof(JsonbContainment)) {
        Found = true;
      }

      return base.VisitMethodCall(node);
    }
  }

  private string _instanceField = "v";
  private static string _staticField = "v";
  private static string _method() => "v";

  // ========================================
  // Spellings that reach the index
  // ========================================

  /// <summary>Every spelling below is a plain equality and every one of them is rewritten.</summary>
  [Test]
  [Arguments("literal on the right")]
  [Arguments("literal on the left")]
  [Arguments("captured local")]
  [Arguments("instance field")]
  [Arguments("static field")]
  [Arguments("method result")]
  [Arguments("ternary value")]
  [Arguments("coalesced value")]
  [Arguments("nested value object member")]
  public async Task EqualitySpellings_ReachTheIndexAsync(string spelling) {
    var local = "v";
    var flag = true;
    string? maybe = null;

    var rewritten = spelling switch {
      "literal on the right" => _rewrites(x => x.Data.Code == "v"),
      "literal on the left" => _rewrites(x => "v" == x.Data.Code),
      "captured local" => _rewrites(x => x.Data.Code == local),
      "instance field" => _rewrites(x => x.Data.Code == _instanceField),
      "static field" => _rewrites(x => x.Data.Code == _staticField),
      "method result" => _rewrites(x => x.Data.Code == _method()),
      "ternary value" => _rewrites(x => x.Data.Code == (flag ? "a" : "b")),
      "coalesced value" => _rewrites(x => x.Data.Code == (maybe ?? "d")),
      "nested value object member" => _rewrites(x => x.Data.Number.Value == _probe),
      _ => throw new InvalidOperationException(spelling),
    };

    await Assert.That(rewritten).IsTrue();
  }

  /// <summary>A predicate is rewritten wherever it sits, not only inside Where.</summary>
  [Test]
  [Arguments("where")]
  [Arguments("any")]
  [Arguments("all")]
  [Arguments("count")]
  [Arguments("first-or-default")]
  [Arguments("single-or-default")]
  [Arguments("last-or-default")]
  [Arguments("take-while")]
  [Arguments("skip-while")]
  public async Task TerminalOperators_CarryTheRewriteAsync(string op) {
    var rows = _db.Set<PerspectiveRow<OrderModel>>();
    var code = "v";

    Expression query = op switch {
      "where" => rows.Where(x => x.Data.Code == code).Expression,
      "any" => _expressionOf(() => rows.Any(x => x.Data.Code == code)),
      "all" => _expressionOf(() => rows.All(x => x.Data.Code == code)),
      "count" => _expressionOf(() => rows.Count(x => x.Data.Code == code)),
      "first-or-default" => _expressionOf(() => rows.FirstOrDefault(x => x.Data.Code == code)),
      "single-or-default" => _expressionOf(() => rows.SingleOrDefault(x => x.Data.Code == code)),
      "last-or-default" => _expressionOf(() => rows.LastOrDefault(x => x.Data.Code == code)),
      "take-while" => rows.TakeWhile(x => x.Data.Code == code).Expression,
      "skip-while" => rows.SkipWhile(x => x.Data.Code == code).Expression,
      _ => throw new InvalidOperationException(op),
    };

    var rewritten = new JsonbContainmentRewriter(_db.Model).Visit(query);
    var finder = new _markerFinder();
    finder.Visit(rewritten);

    await Assert.That(finder.Found).IsTrue();
  }

  /// <summary>Captures the call expression without executing it.</summary>
  private static Expression _expressionOf<T>(Expression<Func<T>> call) => call.Body;

  // ========================================
  // Spellings that do not, and why
  // ========================================

  /// <summary>
  /// These are equalities in spirit but not <c>ExpressionType.Equal</c> nodes, so the rewrite does
  /// not see them and the query keeps the extraction form. They are correct, just not indexed.
  /// </summary>
  [Test]
  [Arguments("instance Equals")]
  [Arguments("static string.Equals")]
  [Arguments("object.Equals")]
  [Arguments("list Contains")]
  [Arguments("array Contains")]
  [Arguments("bare boolean member")]
  [Arguments("StartsWith")]
  public async Task SpellingsThatMissTheIndex_AreRecordedAsync(string spelling) {
    var codes = new List<string> { "a", "b" };
    var array = new[] { "a", "b" };

    var rewritten = spelling switch {
      "instance Equals" => _rewrites(x => x.Data.Code.Equals("v", StringComparison.Ordinal)),
      "static string.Equals" => _rewrites(x => string.Equals(x.Data.Code, "v", StringComparison.Ordinal)),
      "object.Equals" => _rewrites(x => Equals(x.Data.Code, "v")),
      "list Contains" => _rewrites(x => codes.Contains(x.Data.Code)),
      "array Contains" => _rewrites(x => array.Contains(x.Data.Code)),
      "bare boolean member" => _rewrites(x => x.Data.Active),
      "StartsWith" => _rewrites(x => x.Data.Code.StartsWith("va", StringComparison.Ordinal)),
      _ => throw new InvalidOperationException(spelling),
    };

    await Assert.That(rewritten).IsFalse();
  }

  /// <summary>
  /// These must not be rewritten because containment would answer differently, so the assertion is
  /// about correctness rather than about performance.
  /// </summary>
  [Test]
  [Arguments("two members of the same row")]
  [Arguments("member compared with null")]
  [Arguments("negated equality")]
  [Arguments("value object compared whole")]
  [Arguments("member compared with a row column")]
  public async Task SpellingsThatMustNotBeRewritten_AreNotAsync(string spelling) {
    var number = new OrderNumber(_probe);

    var rewritten = spelling switch {
      "two members of the same row" => _rewrites(x => x.Data.Code == x.Data.Other),
      "member compared with null" => _rewrites(x => x.Data.Note == null),
      "negated equality" => _rewrites(x => !(x.Data.Code == "v")),
      "value object compared whole" => _rewrites(x => x.Data.Number == number),
      "member compared with a row column" => _rewrites(x => x.Data.Code == x.Id.ToString()),
      _ => throw new InvalidOperationException(spelling),
    };

    await Assert.That(rewritten).IsFalse();
  }

  /// <summary>
  /// A comparison projected into a boolean is left alone. In a filter the two forms agree, but a
  /// projection returns the value itself, and for a key that is absent an extraction yields null
  /// where containment yields false.
  /// </summary>
  [Test]
  public async Task ProjectedComparison_IsNotRewrittenAsync() {
    var rows = _db.Set<PerspectiveRow<OrderModel>>();
    var projection = rows.Select(x => x.Data.Code == "v").Expression;

    var rewritten = new JsonbContainmentRewriter(_db.Model).Visit(projection);
    var finder = new _markerFinder();
    finder.Visit(rewritten);

    await Assert.That(finder.Found).IsFalse();
  }

  private static readonly Guid _probe = new("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
}
