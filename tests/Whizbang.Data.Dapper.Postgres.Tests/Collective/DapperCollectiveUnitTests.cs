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

  // ── DapperCollectiveScopeFilterCompiler ────────────────────────────────

  [Test]
  public async Task ScopeFilter_SingleEquality_CompilesToScopeJsonbWhereAsync() {
    var tenantId = "t-A";
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Scope.TenantId == tenantId;

    var result = DapperCollectiveScopeFilterCompiler<_jobModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("scope->>'TenantId' = @where_tenantid");
    await Assert.That(result.Parameters.Count).IsEqualTo(1);
    await Assert.That(result.Parameters["where_tenantid"]).IsEqualTo("t-A");
  }

  [Test]
  public async Task ScopeFilter_ConstantLiteral_IsEvaluatedAsync() {
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Scope.TenantId == "literal-t";
    var result = DapperCollectiveScopeFilterCompiler<_jobModel>.Compile(filter);
    await Assert.That(result.Parameters["where_tenantid"]).IsEqualTo("literal-t");
  }

  [Test]
  public async Task ScopeFilter_AndChain_ComposesBothPredicatesAsync() {
    var tenantId = "t-A";
    var customer = "c-1";
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter =
      row => row.Scope.TenantId == tenantId && row.Scope.CustomerId == customer;

    var result = DapperCollectiveScopeFilterCompiler<_jobModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("(scope->>'TenantId' = @where_tenantid AND scope->>'CustomerId' = @where_customerid)");
    await Assert.That(result.Parameters.Count).IsEqualTo(2);
  }

  [Test]
  public async Task ScopeFilter_ReversedOperands_StillMatchesScopeMemberAsync() {
    var tenantId = "t-A";
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => tenantId == row.Scope.TenantId;
    var result = DapperCollectiveScopeFilterCompiler<_jobModel>.Compile(filter);
    await Assert.That(result.SqlFragment).IsEqualTo("scope->>'TenantId' = @where_tenantid");
  }

  [Test]
  public async Task ScopeFilter_NonEquality_ThrowsNotSupportedAsync() {
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.ViewCount > 5;
    await Assert.That(() => DapperCollectiveScopeFilterCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task ScopeFilter_DataMember_CompilesToDataJsonbWhereAsync() {
    // A handler's per-model Where projects onto its own data columns — the compiler must translate
    // row.Data.<Prop> to data->>'Prop' (the jsonb data column), not just row.Scope.<Prop>.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Data.Status == "Draft";

    var result = DapperCollectiveScopeFilterCompiler<_jobModel>.Compile(filter);

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

    var result = DapperCollectiveScopeFilterCompiler<_jobModel>.Compile(filter);

    await Assert.That(result.SqlFragment)
      .IsEqualTo("(scope->>'TenantId' = @where_tenantid AND data->>'Status' = @where_status)");
    await Assert.That(result.Parameters.Count).IsEqualTo(2);
  }

  [Test]
  public async Task ScopeFilter_TopLevelColumn_ThrowsNotSupportedAsync() {
    // Only the scope and data jsonb columns are translatable; a top-level system column (version) is not.
    Expression<Func<PerspectiveRow<_jobModel>, bool>> filter = row => row.Version == 5;
    await Assert.That(() => DapperCollectiveScopeFilterCompiler<_jobModel>.Compile(filter))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task ScopeFilter_NullFilter_ThrowsArgumentNullAsync() {
    await Assert.That(() => DapperCollectiveScopeFilterCompiler<_jobModel>.Compile(null!))
      .Throws<ArgumentNullException>();
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
    SpecKind: CollectiveSpecKind.Linq, Invoker: static (h, e) => ((_handler)h).Apply((_evtA)e));

  private sealed class _factory : IDbConnectionFactory {
    public Task<System.Data.IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("validation should fail before a connection is needed");
  }

  [Test]
  public async Task Applier_EventTypeMismatch_ThrowsArgumentAsync() {
    var entry = _entryFor<_evtB>(); // entry says _evtB but we pass _evtA
    await Assert.That(() => DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, new _handler(), new _evtA { Scope = new TenantCollectiveScope("t") },
        new TenantCollectiveScopeResolver(), new _factory(), "wh_per_x", default))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Applier_ScopeKindMismatch_ThrowsArgumentAsync() {
    var entry = _entryFor<_evtA>();
    await Assert.That(() => DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, new _handler(), new _evtA { Scope = new _otherScope() },
        new TenantCollectiveScopeResolver(), new _factory(), "wh_per_x", default))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Applier_NullArgs_ThrowAsync() {
    var entry = _entryFor<_evtA>();
    await Assert.That(() => DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
        entry, new _handler(), new _evtA { Scope = new TenantCollectiveScope("t") },
        new TenantCollectiveScopeResolver(), null!, "wh_per_x", default))
      .Throws<ArgumentNullException>();
  }

  private sealed record _otherScope : CollectiveScope {
    public override string ScopeKind => "other";
  }

  // ── DapperCollectiveEventExecutor ──────────────────────────────────────

  [Test]
  public async Task Executor_ReportsModelTypeAsync() {
    var ex = new DapperCollectiveEventExecutor<_jobModel>("wh_per_job");
    await Assert.That(ex.ModelType).IsEqualTo(typeof(_jobModel));
  }

  [Test]
  public async Task Executor_NonFactorySession_ThrowsArgumentAsync() {
    var ex = new DapperCollectiveEventExecutor<_jobModel>("wh_per_job");
    await Assert.That(() => ex.ApplyAsync(
        _entryFor<_evtA>(), new _handler(), new _evtA { Scope = new TenantCollectiveScope("t") },
        new TenantCollectiveScopeResolver(), dbContextOrSession: "not-a-factory", Guid.NewGuid(), default))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Executor_NullTableName_ThrowsAsync() {
    await Assert.That(() => new DapperCollectiveEventExecutor<_jobModel>(""))
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
