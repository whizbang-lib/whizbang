#pragma warning disable CA1707
#pragma warning disable CA1859 // tests assert against the interface return type

using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Dapper.Postgres.Collective;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Unit tests (no database) for the Dapper collective apply components: the scope-filter compiler's
/// branch matrix, the applier's validation guards, the executor's session cast, the session accessor,
/// and the DI extensions.
/// </summary>
public class DapperCollectiveUnitTests {

  private sealed class _jobModel {
    public string Status { get; set; } = "";
    public int ViewCount { get; set; }
  }

  // ── CollectivePredicateSqlCompiler (shared) ────────────────────────────

  [Test]
  public async Task ScopeFilter_SingleEquality_CompilesToScopeJsonbShortKeyWhereAsync() {
    var tenantId = "t-A";
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Scope.TenantId == tenantId;

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);

    // PerspectiveScope.TenantId is [JsonPropertyName("t")] — the persisted jsonb key is the SHORT key, so the
    // compiler must emit scope->>'t', not scope->>'TenantId' (which matches nothing in production).
    await Assert.That(result.SqlFragment).IsEqualTo("scope->>'t' = @where_tenantid");
    await Assert.That(result.Parameters.Count).IsEqualTo(1);
    await Assert.That(result.Parameters["where_tenantid"]).IsEqualTo("t-A");
  }

  [Test]
  public async Task ScopeFilter_ConstantLiteral_IsEvaluatedAsync() {
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Scope.TenantId == "literal-t";
    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);
    await Assert.That(result.Parameters["where_tenantid"]).IsEqualTo("literal-t");
  }

  [Test]
  public async Task ScopeFilter_AndChain_ComposesBothPredicatesAsync() {
    var tenantId = "t-A";
    var customer = "c-1";
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      row => row.Scope.TenantId == tenantId && row.Scope.CustomerId == customer;

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("(scope->>'t' = @where_tenantid AND scope->>'c' = @where_customerid)");
    await Assert.That(result.Parameters.Count).IsEqualTo(2);
  }

  [Test]
  public async Task ScopeFilter_ReversedOperands_StillMatchesScopeMemberAsync() {
    var tenantId = "t-A";
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => tenantId == row.Scope.TenantId;
    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);
    await Assert.That(result.SqlFragment).IsEqualTo("scope->>'t' = @where_tenantid");
  }

  [Test]
  public async Task ScopeFilter_GreaterThan_ThrowsNotSupportedAsync() {
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.ViewCount > 5;
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task ScopeFilter_NotEqual_CompilesToInequalityAsync() {
    // a consumer cohort handlers use `!=` (e.g. r.Data.Status != "Archived"); the compiler must emit SQL `<>`.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.Status != "Archived";
    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);
    await Assert.That(result.SqlFragment).IsEqualTo("data->>'Status' <> @where_status");
    await Assert.That(result.Parameters["where_status"]).IsEqualTo("Archived");
  }

  [Test]
  public async Task ScopeFilter_NotOnAny_CompilesToNotExistsAsync() {
    // a consumer overlay-apply cohorts use `!q.Of<Sibling>().Any(...)` (NOT-in-cohort) → NOT EXISTS.
    var q = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(_statusModel)] = "wh_per_status" });
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      r => !q.Of<_statusModel>().Any(s => s.Id == r.Id && s.Data.Status == "Archived");
    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter, "where", "wh_per_job");
    await Assert.That(result.SqlFragment).IsEqualTo(
      "NOT (EXISTS (SELECT 1 FROM wh_per_status s WHERE (s.id = wh_per_job.id AND s.data->>'Status' = @where_status)))");
  }

  [Test]
  public async Task ScopeFilter_DataMember_CompilesToDataJsonbWhereAsync() {
    // A handler's per-model Where projects onto its own data columns — the compiler must translate
    // row.Data.<Prop> to data->>'Prop' (the jsonb data column), not just row.Scope.<Prop>.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.Status == "Draft";

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("data->>'Status' = @where_status");
    await Assert.That(result.Parameters["where_status"]).IsEqualTo("Draft");
  }

  [Test]
  public async Task ScopeFilter_ScopeAndDataMix_ComposesBothColumnsAsync() {
    // The Framework path AND-composes a scope-column envelope with a data-column handler Where — both
    // column kinds appear in one predicate tree and must translate side by side.
    var tenant = "t-A";
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      row => row.Scope.TenantId == tenant && row.Data.Status == "Draft";

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);

    await Assert.That(result.SqlFragment)
      .IsEqualTo("(scope->>'t' = @where_tenantid AND data->>'Status' = @where_status)");
    await Assert.That(result.Parameters.Count).IsEqualTo(2);
  }

  [Test]
  public async Task ScopeFilter_TopLevelColumn_ThrowsNotSupportedAsync() {
    // Scope/data jsonb columns and the top-level id (for correlation) are translatable; an arbitrary
    // top-level system column (version) is not.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Version == 5;
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  private sealed class _statusModel {
    public string Status { get; set; } = "";
  }

  [Test]
  public async Task ScopeFilter_CrossPerspectiveAny_CompilesToExistsSubqueryAsync() {
    // The cross-perspective cohort: q.Of<Sibling>().Any(s => s.Id == r.Id && eligible.Contains(s.Data.X))
    // compiles to a correlated EXISTS subquery — .Any -> EXISTS, Contains -> IN, sibling table resolved
    // via the query context, outer id qualified by the outer table.
    var q = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(_statusModel)] = "wh_per_status" });
    var eligible = new[] { "Draft", "Approved" };
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      r => q.Of<_statusModel>().Any(s => s.Id == r.Id && eligible.Contains(s.Data.Status));

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(
      filter, parameterPrefix: "where", outerTableName: "wh_per_job");

    await Assert.That(result.SqlFragment).IsEqualTo(
      "EXISTS (SELECT 1 FROM wh_per_status s WHERE (s.id = wh_per_job.id AND s.data->>'Status' IN (@where_status_0, @where_status_1)))");
    await Assert.That(result.Parameters["where_status_0"]).IsEqualTo("Draft");
    await Assert.That(result.Parameters["where_status_1"]).IsEqualTo("Approved");
  }

  private enum _jobStatusEnum { Draft, Approved, Published, Archived }
  private sealed class _enumStatusModel { public _jobStatusEnum Status { get; set; } }
  private static readonly _jobStatusEnum[] _eligibleEnum =
    [_jobStatusEnum.Draft, _jobStatusEnum.Approved, _jobStatusEnum.Published];

  [Test]
  public async Task ScopeFilter_EnumArrayContains_CompilesToInWithEnumNamesAsync() {
    // Mirrors the a consumer OrderTemplateCollectiveHandler: a STATIC enum-array field `.Contains(enum property)`
    // inside a cross-perspective Any. string[] Contains was covered; enum[] Contains is a different shape/value.
    var q = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(_enumStatusModel)] = "wh_per_status" });
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      r => q.Of<_enumStatusModel>().Any(s => s.Id == r.Id && _eligibleEnum.Contains(s.Data.Status));

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(
      filter, parameterPrefix: "where", outerTableName: "wh_per_job");

    await Assert.That(result.SqlFragment).IsEqualTo(
      "EXISTS (SELECT 1 FROM wh_per_status s WHERE (s.id = wh_per_job.id AND s.data->>'Status' IN (@where_status_0, @where_status_1, @where_status_2)))");
    await Assert.That(result.Parameters["where_status_0"]).IsEqualTo("Draft")
      .Because("Enum values serialize to their names, matching how the jsonb column stores the enum.");
  }

  [Test]
  public async Task ScopeFilter_ScopeAndCrossPerspectiveAny_ComposesEnvelopeAndExistsAsync() {
    var q = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(_statusModel)] = "wh_per_status" });
    var tenant = "t-A";
    var eligible = new[] { "Draft" };
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      r => r.Scope.TenantId == tenant
        && q.Of<_statusModel>().Any(s => s.Id == r.Id && eligible.Contains(s.Data.Status));

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(
      filter, parameterPrefix: "where", outerTableName: "wh_per_job");

    await Assert.That(result.SqlFragment).IsEqualTo(
      "(scope->>'t' = @where_tenantid AND EXISTS (SELECT 1 FROM wh_per_status s WHERE (s.id = wh_per_job.id AND s.data->>'Status' IN (@where_status_0))))");
  }

  [Test]
  public async Task ScopeFilter_AnyWithoutOuterTableName_ThrowsAsync() {
    // A correlated EXISTS needs the outer table to qualify the correlation; without it the compiler can't
    // disambiguate the outer id.
    var q = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(_statusModel)] = "wh_per_status" });
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      r => q.Of<_statusModel>().Any(s => s.Id == r.Id);

    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(filter, "where", outerTableName: null))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task ScopeFilter_NullFilter_ThrowsArgumentNullAsync() {
    await Assert.That(() => CollectivePredicateSqlCompiler<_jobModel>.Compile(null!))
      .Throws<ArgumentNullException>();
  }

  // ── ReferencedJsonPaths (§7 — drives expression-index creation) ─────────

  [Test]
  public async Task ReferencedJsonPaths_ScopeAndData_RecordsBothColumnsForOuterTableAsync() {
    // Every value-comparison on a scope/data jsonb column is a candidate for a btree expression index.
    // The scope->>'t' tenant filter (added on every apply after the D0 fix) is the single most important one.
    var tenant = "t-A";
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      row => row.Scope.TenantId == tenant && row.Data.Status == "Draft";

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(
      filter, parameterPrefix: "where", outerTableName: "wh_per_job");

    await Assert.That(result.ReferencedJsonPaths).Contains(
      p => p.Table == "wh_per_job" && p.ColumnExpression == "scope->>'t'")
      .Because("The tenant scope filter must be indexable — scope->>'t' equality can't use gin(scope).");
    await Assert.That(result.ReferencedJsonPaths).Contains(
      p => p.Table == "wh_per_job" && p.ColumnExpression == "data->>'Status'")
      .Because("A handler cohort filter on data->>'Status' must be indexable — gin(data) can't serve ->> equality.");
  }

  [Test]
  public async Task ReferencedJsonPaths_ContainsInClause_RecordsTheColumnAsync() {
    var eligible = new[] { "Draft", "Approved" };
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => eligible.Contains(row.Data.Status);

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(
      filter, parameterPrefix: "where", outerTableName: "wh_per_job");

    await Assert.That(result.ReferencedJsonPaths).Contains(
      p => p.Table == "wh_per_job" && p.ColumnExpression == "data->>'Status'")
      .Because("An IN (…) membership filter benefits from the same expression index as equality.");
  }

  [Test]
  public async Task ReferencedJsonPaths_CrossPerspectiveAny_RecordsSiblingTableColumnAsync() {
    // The inner EXISTS filters the SIBLING table — the index belongs on the sibling, not the outer table.
    var q = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(_statusModel)] = "wh_per_status" });
    var tenant = "t-A";
    var eligible = new[] { "Draft" };
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      r => r.Scope.TenantId == tenant
        && q.Of<_statusModel>().Any(s => s.Id == r.Id && eligible.Contains(s.Data.Status));

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(
      filter, parameterPrefix: "where", outerTableName: "wh_per_job");

    await Assert.That(result.ReferencedJsonPaths).Contains(
      p => p.Table == "wh_per_job" && p.ColumnExpression == "scope->>'t'");
    await Assert.That(result.ReferencedJsonPaths).Contains(
      p => p.Table == "wh_per_status" && p.ColumnExpression == "data->>'Status'")
      .Because("The correlated cohort filter scans the sibling table by data->>'Status' — it must be indexed there.");
  }

  [Test]
  public async Task ReferencedJsonPaths_IdCorrelationOnly_RecordsNothingIndexableAsync() {
    // A pure id-correlation (s.id = outer.id) needs no expression index — id is the primary key. And when no
    // outer table is supplied, outer columns can't be attributed to a table, so nothing is recorded.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.Status == "Draft";

    var result = CollectivePredicateSqlCompiler<_jobModel>.Compile(filter);

    await Assert.That(result.ReferencedJsonPaths.Count).IsEqualTo(0)
      .Because("Without an outer table name the compiler can't attribute a column to a table, so it records nothing (graceful — no index, statement_timeout still backstops).");
  }

  // ── DapperCollectiveEventApplier validation guards (no DB reached) ──────

  private sealed record _evtA : ICollectiveEvent { public required CollectiveScope Scope { get; init; } }
  private sealed record _evtB : ICollectiveEvent { public required CollectiveScope Scope { get; init; } }
  private sealed class _handler {
    public ICollectiveSpec<_jobModel> Apply(_evtA _) => new _spec(s => s.SetProperty(j => j.Status, "x"));
  }
  private sealed record _spec(Expression<Action<ICollectiveSetters<_jobModel>>> Setters) : ICollectiveSpec<_jobModel>;

  private static CollectiveApplyEntry _entryFor<TEvent>() => new(
    ModelType: typeof(_jobModel), EventType: typeof(TEvent), HandlerType: typeof(_handler),
    MethodName: nameof(_handler.Apply), ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq, Invoker: static (h, e, q) => ((_handler)h).Apply((_evtA)e));

  private sealed class _factory : IDbConnectionFactory {
    public Task<System.Data.IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("validation should fail before a connection is needed");
  }

  private static readonly IReadOnlyDictionary<Type, string> _noSiblings = new Dictionary<Type, string>();

  [Test]
  public async Task Applier_EventTypeMismatch_ThrowsArgumentAsync() {
    var entry = _entryFor<_evtB>(); // entry says _evtB but we pass _evtA
    await Assert.That(() => DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, new _handler(), new _evtA { Scope = new TenantCollectiveScope("t") },
        new TenantCollectiveScopeResolver(), new _factory(), "wh_per_x", _noSiblings, CollectiveApplyOptions.Default, default))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Applier_ScopeKindMismatch_ThrowsArgumentAsync() {
    var entry = _entryFor<_evtA>();
    await Assert.That(() => DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, new _handler(), new _evtA { Scope = new _otherScope() },
        new TenantCollectiveScopeResolver(), new _factory(), "wh_per_x", _noSiblings, CollectiveApplyOptions.Default, default))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Applier_NullArgs_ThrowAsync() {
    var entry = _entryFor<_evtA>();
    await Assert.That(() => DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, new _handler(), new _evtA { Scope = new TenantCollectiveScope("t") },
        new TenantCollectiveScopeResolver(), null!, "wh_per_x", _noSiblings, CollectiveApplyOptions.Default, default))
      .Throws<ArgumentNullException>();
  }

  private sealed record _otherScope : CollectiveScope {
    public override string ScopeKind => "other";
  }

  // ── DapperCollectiveEventExecutor ──────────────────────────────────────

  [Test]
  public async Task Executor_ReportsModelTypeAsync() {
    var ex = new DapperCollectiveEventExecutor<_jobModel>("wh_per_job", _noSiblings);
    await Assert.That(ex.ModelType).IsEqualTo(typeof(_jobModel));
  }

  [Test]
  public async Task Executor_NonFactorySession_ThrowsArgumentAsync() {
    var ex = new DapperCollectiveEventExecutor<_jobModel>("wh_per_job", _noSiblings);
    await Assert.That(() => ex.ApplyAsync(
        _entryFor<_evtA>(), new _handler(), new _evtA { Scope = new TenantCollectiveScope("t") },
        new TenantCollectiveScopeResolver(), dbContextOrSession: "not-a-factory", Guid.NewGuid(), default))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Executor_NullTableName_ThrowsAsync() {
    await Assert.That(() => new DapperCollectiveEventExecutor<_jobModel>("", _noSiblings))
      .Throws<ArgumentException>();
  }

  // ── DapperCollectiveSessionAccessor ────────────────────────────────────

  [Test]
  public async Task SessionAccessor_ReturnsConnectionFactoryAsync() {
    var factory = new _factory();
    var sp = new ServiceCollection().AddSingleton<IDbConnectionFactory>(factory).BuildServiceProvider();
    var session = new DapperCollectiveSessionAccessor().GetSession(sp);
    await Assert.That(session).IsSameReferenceAs(factory);
  }

  // ── DI extensions ──────────────────────────────────────────────────────

  [Test]
  public async Task AddCollectiveEventsDapper_RegistersDispatcherResolverAccessorAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IDbConnectionFactory>(new _factory());
    services.AddCollectiveEventsDapper(System.Array.Empty<CollectiveApplyEntry>());
    services.AddCollectiveExecutorDapper<_jobModel>("wh_per_job");
    var sp = services.BuildServiceProvider();

    await Assert.That(sp.GetService<ICollectiveDispatcher>()).IsNotNull();
    await Assert.That(sp.GetService<ICollectiveSessionAccessor>()).IsTypeOf<DapperCollectiveSessionAccessor>();
    await Assert.That(sp.GetServices<ICollectiveScopeResolver>().Any(r => r.ScopeKind == "tenant")).IsTrue();
    await Assert.That(sp.GetServices<ICollectiveEventExecutor>().Any(e => e.ModelType == typeof(_jobModel))).IsTrue();
  }

  [Test]
  public async Task AddCollectiveEventsDapper_NullEntries_ThrowsAsync() {
    await Assert.That(() => new ServiceCollection().AddCollectiveEventsDapper(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AddCollectiveExecutorDapper_NullTableName_ThrowsAsync() {
    await Assert.That(() => new ServiceCollection().AddCollectiveExecutorDapper<_jobModel>(""))
      .Throws<ArgumentException>();
  }
}
