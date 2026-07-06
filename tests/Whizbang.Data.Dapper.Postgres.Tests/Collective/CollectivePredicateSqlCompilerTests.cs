#pragma warning disable CA1707

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.Dapper.Postgres.Collective;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Unit tests (no database) for the CollectivePredicateSqlCompiler arms NOT already locked by
/// <see cref="DapperCollectiveUnitTests"/>: NOT over a plain predicate, id/int/null/enum value binding,
/// every captured-value resolution shape (property chains, method calls), each Contains overload branch
/// (instance list, empty source, custom comparer, non-column item, null source, unsupported shape), and
/// the EXISTS guard rails (nested cohorts, non-Of sources, static Of, missing table source, non-lambda
/// predicates, foreign parameter roots).
/// </summary>
public class CollectivePredicateSqlCompilerTests {

  private sealed class _jobModel {
    public string Status { get; set; } = "";
    public int ViewCount { get; set; }
  }

  private sealed class _statusModel {
    public string Status { get; set; } = "";
  }

  private enum _statusEnum { Draft, Approved }

  private sealed class _enumModel {
    public _statusEnum Status { get; set; }
  }

  // ── Compile argument validation ─────────────────────────────────────────

  [Test]
  public async Task Compile_WhitespaceParameterPrefix_ThrowsArgumentAsync() {
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.Status == "Draft";
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter, parameterPrefix: " "))
      .Throws<ArgumentException>();
  }

  // ── Predicate-shape arms ────────────────────────────────────────────────

  [Test]
  public async Task Compile_NotOverPlainEquality_WrapsPredicateInNotAsync() {
    // NOT over a non-Any predicate — the `!(pred)` arm, distinct from the `!q.Of<T>().Any(...)` arm.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => !(row.Data.Status == "Archived");
    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);
    await Assert.That(result.SqlFragment).IsEqualTo("NOT (data->>'Status' = @where_status)");
    await Assert.That(result.Parameters["where_status"]).IsEqualTo("Archived");
  }

  [Test]
  public async Task Compile_OrElseDisjunction_ThrowsNotSupportedAsync() {
    // Disjunctions are documented as unsupported — they must hit the unsupported-node throw, not
    // silently compile to AND.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      row => row.Data.Status == "Draft" || row.Data.Status == "Approved";
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  // ── Column/value binding arms ───────────────────────────────────────────

  [Test]
  public async Task Compile_OuterIdEquality_EmitsUnqualifiedIdColumnAsync() {
    var id = Guid.NewGuid();
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Id == id;

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("id = @where_id");
    await Assert.That(result.Parameters["where_id"]).IsEqualTo(id.ToString());
    // id is the primary key — never an expression-index candidate.
    await Assert.That(result.ReferencedJsonPaths.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Compile_NullComparisonValue_BindsNullParameterAsync() {
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.Status == null;
    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);
    await Assert.That(result.SqlFragment).IsEqualTo("data->>'Status' = @where_status");
    await Assert.That(result.Parameters["where_status"]).IsNull();
  }

  [Test]
  public async Task Compile_IntEquality_BindsNumberAsTextAsync() {
    // Numeric scalars round-trip through ToString() so the bound text matches data->>'ViewCount'.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.ViewCount == 42;
    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);
    await Assert.That(result.SqlFragment).IsEqualTo("data->>'ViewCount' = @where_viewcount");
    await Assert.That(result.Parameters["where_viewcount"]).IsEqualTo("42");
  }

  [Test]
  public async Task Compile_CapturedEnumEquality_BindsUnderlyingNumberAsync() {
    // Enum equality inserts Convert nodes on both sides — the compiler must strip them, and the bound
    // value must be the UNDERLYING NUMBER ("1"), not the name, to match EF's jsonb enum storage.
    var target = _statusEnum.Approved;
    Expression<Func<PerspectiveRow<_enumModel>, bool>> filter = row => row.Data.Status == target;

    var result = CollectivePredicateSqlCompiler<_enumModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("data->>'Status' = @where_status");
    await Assert.That(result.Parameters["where_status"]).IsEqualTo("1");
  }

  // ── Captured-value resolution shapes ────────────────────────────────────

  private sealed class _valueHolder {
    public string HeldStatus { get; } = "Held";
  }
  private static readonly _valueHolder _holder = new();

  [Test]
  public async Task Compile_ValueFromStaticFieldPropertyChain_ResolvesViaMemberReadAsync() {
    // Static field (null instance, FieldInfo branch) → instance property (PropertyInfo branch) — the
    // member-chain reader must resolve both without IL-compiling a lambda.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.Status == _holder.HeldStatus;
    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);
    await Assert.That(result.Parameters["where_status"]).IsEqualTo("Held");
  }

  private static string _computedStatus() => "X";

  [Test]
  public async Task Compile_MethodCallValue_ThrowsNotSupportedAsync() {
    // A method-call value source is not a constant or captured member — unsupported node kind.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.Status == _computedStatus();
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  // ── Contains overload branches ──────────────────────────────────────────

  private static readonly List<string> _statusList = ["Draft", "Approved"];

  [Test]
  public async Task Compile_ListInstanceContains_EmitsInClauseAsync() {
    // list.Contains(item) — the instance-method (Object non-null, 1 arg) Contains shape.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => _statusList.Contains(row.Data.Status);

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("data->>'Status' IN (@where_status_0, @where_status_1)");
    await Assert.That(result.Parameters["where_status_0"]).IsEqualTo("Draft");
    await Assert.That(result.Parameters["where_status_1"]).IsEqualTo("Approved");
  }

  private static readonly string[] _noStatuses = [];

  [Test]
  public async Task Compile_EmptyContainsSource_EmitsInNullAsync() {
    // Zero values → `IN (NULL)` (matches no rows) rather than the invalid `IN ()`.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => _noStatuses.Contains(row.Data.Status);
    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);
    await Assert.That(result.SqlFragment).IsEqualTo("data->>'Status' IN (NULL)");
    await Assert.That(result.Parameters.Count).IsEqualTo(0);
  }

  private static readonly string[] _someStatuses = ["Draft", "Approved"];

  [Test]
  public async Task Compile_ContainsWithCustomComparer_ThrowsNotSupportedAsync() {
    // Enumerable.Contains(source, item, comparer) — a custom comparer changes matching semantics and
    // cannot be translated to SQL IN.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      row => _someStatuses.Contains(row.Data.Status, StringComparer.OrdinalIgnoreCase);
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task Compile_ContainsItemNotAColumn_ThrowsNotSupportedAsync() {
    // The item must be row.Data.X / row.Scope.X — a literal item has no column to project.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = _ => _someStatuses.Contains("Draft");
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task Compile_NullContainsSource_ThrowsNotSupportedAsync() {
    // The source must evaluate to a captured collection; a null capture cannot yield IN values.
    string[]? missing = null;
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => missing!.Contains(row.Data.Status);
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  private sealed class _pairContainsHost {
    private readonly StringComparison _comparison = StringComparison.Ordinal;
    public bool Contains(string left, string right) => string.Equals(left, right, _comparison);
  }
  private static readonly _pairContainsHost _pairHost = new();

  [Test]
  public async Task Compile_TwoArgInstanceContains_ThrowsUnsupportedShapeAsync() {
    // An instance Contains with two arguments matches none of the recognized shapes
    // (Enumerable 2-arg, span/Enumerable 3-arg, instance 1-arg) → the shape-diagnostic throw.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      row => _pairHost.Contains(row.Data.Status, "Draft");
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  // ── EXISTS guard rails ──────────────────────────────────────────────────

  [Test]
  public async Task Compile_NestedCrossPerspectiveAny_ThrowsNotSupportedAsync() {
    var q = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(_statusModel)] = "wh_per_status" });
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      _ => q.Of<_statusModel>().Any(s => q.Of<_statusModel>().Any(t => t.Id == s.Id));
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(
        filter, parameterPrefix: "where", outerTableName: "wh_per_job"))
      .Throws<NotSupportedException>();
  }

  private static readonly List<int> _numbers = [1, 2];

  [Test]
  public async Task Compile_AnyOverNonOfSource_ThrowsNotSupportedAsync() {
    // Any over a captured collection is not a sibling cohort — the source must be query.Of<TOther>().
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = _ => _numbers.Any(n => n > 0);
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(
        filter, parameterPrefix: "where", outerTableName: "wh_per_job"))
      .Throws<NotSupportedException>();
  }

  private static class _staticOfSource {
    public static IQueryable<PerspectiveRow<TOther>> Of<TOther>() where TOther : class
      => Array.Empty<PerspectiveRow<TOther>>().AsQueryable();
  }

  [Test]
  public async Task Compile_StaticOfSource_ThrowsNotSupportedAsync() {
    // Of<TOther>() with no receiver instance — the compiler cannot resolve a query context from it.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      r => _staticOfSource.Of<_statusModel>().Any(s => s.Id == r.Id);
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(
        filter, parameterPrefix: "where", outerTableName: "wh_per_job"))
      .Throws<NotSupportedException>();
  }

  private sealed class _plainOfSource {
    private readonly string _tag = "plain";
    public IQueryable<PerspectiveRow<TOther>> Of<TOther>() where TOther : class {
      _ = _tag;
      return Array.Empty<PerspectiveRow<TOther>>().AsQueryable();
    }
  }
  private static readonly _plainOfSource _plainSource = new();

  [Test]
  public async Task Compile_OfSourceWithoutSiblingTableSource_ThrowsNotSupportedAsync() {
    // The Of receiver must implement ICollectiveSiblingTableSource to resolve the EXISTS table.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      r => _plainSource.Of<_statusModel>().Any(s => s.Id == r.Id);
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(
        filter, parameterPrefix: "where", outerTableName: "wh_per_job"))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task Compile_AnyPredicateNotInlineLambda_ThrowsNotSupportedAsync() {
    // A captured Expression variable reaches the Any node as a member access, not a quoted lambda.
    var q = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(_statusModel)] = "wh_per_status" });
    Expression<Func<PerspectiveRow<_statusModel>, bool>> inner = s => s.Data.Status == "Draft";
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = _ => q.Of<_statusModel>().Any(inner);
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(
        filter, parameterPrefix: "where", outerTableName: "wh_per_job"))
      .Throws<NotSupportedException>();
  }

  // ── Non-translatable column roots ───────────────────────────────────────

  [Test]
  public async Task Compile_SystemColumnMemberChain_ThrowsNotSupportedAsync() {
    // row.CreatedAt.Year is member-of-member on the row param, but CreatedAt is not a jsonb column
    // (only Scope/Data are) — the container lookup must reject it.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.CreatedAt.Year == 2024;
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task Compile_MemberOnForeignParameter_ThrowsNotSupportedAsync() {
    // A hand-built tree whose member access roots at a parameter that is neither the outer row nor an
    // EXISTS inner row — "comparisons not rooted at a known row parameter" per the contract.
    var rowParam = Expression.Parameter(typeof(PerspectiveRow<_jobModel>), "row");
    var foreign = Expression.Parameter(typeof(PerspectiveRow<_jobModel>), "other");
    var body = Expression.Equal(
      Expression.Property(
        Expression.Property(foreign, nameof(PerspectiveRow<_jobModel>.Data)),
        nameof(_jobModel.Status)),
      Expression.Constant("Draft"));
    var filter = Expression.Lambda<Func<PerspectiveRow<_jobModel>, bool>>(body, rowParam);

    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }
}
