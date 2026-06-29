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
      Invoker: static (h, e) => ((_jobPerspective)h).Archive((_archiveEvent)e));

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      entry,
      handler,
      new _archiveEvent { Scope = new TenantCollectiveScope("t-A") },
      new TenantCollectiveScopeResolver(),
      ConnectionFactory,
      TABLE,
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
  public async Task ApplyAsync_CustomHandlerWhere_IgnoresResolverScopeAsync() {
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
      default);

    await Assert.That(affected).IsEqualTo(2)
      .Because("Custom drops the tenant envelope: every Draft row matches the handler's sole predicate, regardless of tenant.");
    await Assert.That(await _statusAsync(draftA)).IsEqualTo("Archived");
    await Assert.That(await _statusAsync(draftB)).IsEqualTo("Archived")
      .Because("Tenant-B Draft row is mutated even though the event scope names tenant A — Custom ignores the resolver scope filter.");
    await Assert.That(await _statusAsync(approvedB)).IsEqualTo("Approved")
      .Because("Handler Where (Status=='Draft') still excludes the Approved row.");
  }

  private static CollectiveApplyEntry _draftsEntry(CollectiveScopeHandling handling) => new(
    ModelType: typeof(_jobModel),
    EventType: typeof(_archiveEvent),
    HandlerType: typeof(_draftPerspective),
    MethodName: nameof(_draftPerspective.ArchiveDrafts),
    ScopeHandling: handling,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (h, e) => ((_draftPerspective)h).ArchiveDrafts((_archiveEvent)e));

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
