#pragma warning disable CA1707
#pragma warning disable CA1859 // tests assert against the interface return type

using System.Linq.Expressions;
using Dapper;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// End-to-end integration coverage for the Dapper collective apply path against a real Postgres
/// testcontainer — the Dapper counterpart of <c>CollectiveDispatcherEFCoreIntegrationTests</c>. Proves
/// the resolver's scope filter is the SOLE predicate: a tenant-A event archives every tenant-A row and
/// leaves tenant-B rows untouched, via a single <c>UPDATE … SET data = jsonb_set(...) WHERE scope-&gt;&gt;…</c>.
/// </summary>
[NotInParallel("PostgreSQL")]
public class DapperCollectiveApplierIntegrationTests : PostgresTestBase {

  private const string TABLE = "wh_per_collective_dapper";
  private const string STATUS_TABLE = "wh_per_collective_dapper_status";

  private static readonly IReadOnlyDictionary<Type, string> _noSiblings = new Dictionary<Type, string>();

  private async Task _createTableAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync($@"
      CREATE TABLE IF NOT EXISTS {TABLE} (
        id uuid PRIMARY KEY,
        data jsonb NOT NULL,
        metadata jsonb,
        scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(),
        updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1);
      TRUNCATE {TABLE};");
  }

  private async Task _seedAsync(Guid id, string tenantId, string status) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync(
      $"INSERT INTO {TABLE} (id, data, scope) VALUES (@id, @data::jsonb, @scope::jsonb)",
      new { id, data = $"{{\"Status\": \"{status}\"}}", scope = $"{{\"TenantId\": \"{tenantId}\"}}" });
  }

  private async Task<string?> _statusAsync(Guid id) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    return await conn.ExecuteScalarAsync<string?>(
      $"SELECT data->>'Status' FROM {TABLE} WHERE id = @id", new { id });
  }

  private async Task _createStatusTableAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync($@"
      CREATE TABLE IF NOT EXISTS {STATUS_TABLE} (
        id uuid PRIMARY KEY,
        data jsonb NOT NULL,
        metadata jsonb,
        scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(),
        updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1);
      TRUNCATE {STATUS_TABLE};");
  }

  private async Task _seedStatusAsync(Guid id, string status) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync(
      $"INSERT INTO {STATUS_TABLE} (id, data, scope) VALUES (@id, @data::jsonb, '{{}}'::jsonb)",
      new { id, data = $"{{\"Status\": \"{status}\"}}" });
  }

  [Test]
  public async Task ApplyAsync_CrossPerspectiveCohort_ScopesBySiblingTableAsync() {
    // The cohort is defined by a status that lives on a SIBLING table (same id). The handler's Where uses
    // q.Of<_statusModel>().Any(...), which the Dapper compiler turns into a correlated EXISTS over the
    // sibling table — proving cross-perspective projection end-to-end on the Dapper driver.
    await _createTableAsync();
    await _createStatusTableAsync();

    var eligible = Guid.NewGuid();   // sibling status Draft → in cohort
    var ineligible = Guid.NewGuid(); // sibling status Published → out
    var noSibling = Guid.NewGuid();  // no sibling row → out

    await _seedAsync(eligible, "t-A", "Active");
    await _seedAsync(ineligible, "t-A", "Active");
    await _seedAsync(noSibling, "t-A", "Active");
    await _seedStatusAsync(eligible, "Draft");
    await _seedStatusAsync(ineligible, "Published");

    var siblings = new Dictionary<Type, string> { [typeof(_statusModel)] = STATUS_TABLE };

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      _crossEntry(),
      new _crossPerspective(),
      new _archiveEvent { Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(),
      ConnectionFactory,
      TABLE,
      siblings,
      default);

    await Assert.That(affected).IsEqualTo(1)
      .Because("Only the job whose sibling status is Draft is in the cohort (correlated EXISTS over the status table).");
    await Assert.That(await _statusAsync(eligible)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(ineligible)).IsEqualTo("Active")
      .Because("Sibling status Published is not in the eligible set.");
    await Assert.That(await _statusAsync(noSibling)).IsEqualTo("Active")
      .Because("No sibling row → the EXISTS correlation finds nothing.");
  }

  private static CollectiveApplyEntry _crossEntry() => new(
    ModelType: typeof(_jobModel),
    EventType: typeof(_archiveEvent),
    HandlerType: typeof(_crossPerspective),
    MethodName: nameof(_crossPerspective.Archive),
    ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (h, e, q) => ((_crossPerspective)h).Archive((_archiveEvent)e, q));

  private sealed class _statusModel {
    public string Status { get; set; } = "";
  }

  // Cross-perspective handler: scopes the mutated job table by a status on the SIBLING status table.
  private sealed class _crossPerspective {
    private static readonly string[] _eligible = ["Draft"];

    public ICollectiveSpec<_jobModel> Archive(_archiveEvent e, ICollectiveQuery q) =>
      new _whereSpec(
        s => s.SetProperty(j => j.Status, "Archived"),
        r => q.Of<_statusModel>().Any(st => st.Id == r.Id && _eligible.Contains(st.Data.Status)));

    private sealed record _whereSpec(
        Expression<Action<ICollectiveSetters<_jobModel>>> Setters,
        Expression<Func<PerspectiveRow<_jobModel>, bool>>? Where) : ICollectiveSpec<_jobModel>;
  }

  [Test]
  public async Task ApplyAsync_TenantScoped_UpdatesOnlyInScopeRowsAsync() {
    await _createTableAsync();
    var a1 = Guid.NewGuid();
    var a2 = Guid.NewGuid();
    var b1 = Guid.NewGuid();
    await _seedAsync(a1, "t-A", "Active");
    await _seedAsync(a2, "t-A", "Active");
    await _seedAsync(b1, "t-B", "Active");

    var handler = new _jobPerspective();
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(_jobModel),
      EventType: typeof(_archiveEvent),
      HandlerType: typeof(_jobPerspective),
      MethodName: nameof(_jobPerspective.Archive),
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (h, e, q) => ((_jobPerspective)h).Archive((_archiveEvent)e));

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      entry,
      handler,
      new _archiveEvent { Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(),
      ConnectionFactory,
      TABLE,
      _noSiblings,
      default);

    await Assert.That(affected).IsEqualTo(2)
      .Because("Exactly the two tenant-A rows are in scope.");
    await Assert.That(await _statusAsync(a1)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(a2)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(b1)).IsEqualTo("Active")
      .Because("The resolver scope filter is the SOLE WHERE — tenant-B rows are entirely untouched.");
  }

  [Test]
  public async Task ApplyAsync_FrameworkWithHandlerWhere_RefinesWithinScopeAsync() {
    await _createTableAsync();
    var draftA = Guid.NewGuid();
    var approvedA = Guid.NewGuid();
    var draftB = Guid.NewGuid();
    await _seedAsync(draftA, "t-A", "Draft");
    await _seedAsync(approvedA, "t-A", "Approved");
    await _seedAsync(draftB, "t-B", "Draft");

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      _draftsEntry(CollectiveScopeHandling.Framework),
      new _draftPerspective(),
      new _archiveEvent { Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(),
      ConnectionFactory,
      TABLE,
      _noSiblings,
      default);

    await Assert.That(affected).IsEqualTo(1)
      .Because("Framework AND-composes the tenant envelope (scope->>'TenantId') with the handler's data->>'Status'='Draft' — only tenant-A's Draft row.");
    await Assert.That(await _statusAsync(draftA)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(approvedA)).IsEqualTo("Approved")
      .Because("Handler Where refines within scope — the Approved row in the same tenant falls out.");
    await Assert.That(await _statusAsync(draftB)).IsEqualTo("Draft")
      .Because("Scope envelope still binds — a Draft row in tenant B is excluded.");
  }

  [Test]
  public async Task ApplyAsync_CustomHandlerWhere_StillHonorsTenantScopeAsync() {
    // D0 safety fix: under Custom the handler owns the cohort predicate, but the framework STILL ANDs the
    // tenant envelope on shared multi-tenant tables — so a tenant-A event never touches tenant-B rows.
    await _createTableAsync();
    var draftA = Guid.NewGuid();
    var draftB = Guid.NewGuid();
    var approvedB = Guid.NewGuid();
    await _seedAsync(draftA, "t-A", "Draft");
    await _seedAsync(draftB, "t-B", "Draft");
    await _seedAsync(approvedB, "t-B", "Approved");

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      _draftsEntry(CollectiveScopeHandling.Custom),
      new _draftPerspective(),
      new _archiveEvent { Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(),
      ConnectionFactory,
      TABLE,
      _noSiblings,
      default);

    await Assert.That(affected).IsEqualTo(1)
      .Because("Custom refines the cohort (Status=='Draft') but the framework still ANDs the tenant envelope — only tenant-A's single Draft row qualifies.");
    await Assert.That(await _statusAsync(draftA)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(draftB)).IsEqualTo("Draft")
      .Because("D0 FIX: tenant-B Draft row is UNTOUCHED even under Custom — the scope envelope always binds.");
    await Assert.That(await _statusAsync(approvedB)).IsEqualTo("Approved")
      .Because("Handler Where (Status=='Draft') excludes the Approved row anyway.");
  }

  private static CollectiveApplyEntry _draftsEntry(CollectiveScopeHandling handling) => new(
    ModelType: typeof(_jobModel),
    EventType: typeof(_archiveEvent),
    HandlerType: typeof(_draftPerspective),
    MethodName: nameof(_draftPerspective.ArchiveDrafts),
    ScopeHandling: handling,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (h, e, q) => ((_draftPerspective)h).ArchiveDrafts((_archiveEvent)e));

  private sealed class _jobModel {
    public string Status { get; set; } = "";
  }

  // Projects the cohort onto its own data column via spec.Where — the per-perspective projection capability.
  private sealed class _draftPerspective {
    public ICollectiveSpec<_jobModel> ArchiveDrafts(_archiveEvent e) =>
      new _whereSpec(
        s => s.SetProperty(j => j.Status, "Archived"),
        r => r.Data.Status == "Draft");

    private sealed record _whereSpec(
        Expression<Action<ICollectiveSetters<_jobModel>>> Setters,
        Expression<Func<PerspectiveRow<_jobModel>, bool>>? Where) : ICollectiveSpec<_jobModel>;
  }

  private sealed class _jobPerspective {
    public ICollectiveSpec<_jobModel> Archive(_archiveEvent e) =>
      new _spec(s => s.SetProperty(j => j.Status, "Archived"));

    private sealed record _spec(Expression<Action<ICollectiveSetters<_jobModel>>> Setters)
      : ICollectiveSpec<_jobModel>;
  }

  private sealed record _archiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }
}
