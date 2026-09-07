#pragma warning disable CA1707
#pragma warning disable CA1859 // tests assert against the interface return type

using System.Data;
using System.Linq.Expressions;
using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Dapper.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Targeted coverage for a handful of <see cref="DapperCollectiveEventApplier{TModel}"/> branches that
/// the broader integration suite (<see cref="DapperCollectiveApplierIntegrationTests"/>) never exercises
/// because every one of its applies (a) supplies no <c>onBatchApplied</c> callback, (b) always goes
/// through the eager-opening <see cref="PostgresConnectionFactory"/>, (c) never sets
/// <see cref="CollectiveApplyOptions.StatementTimeoutSeconds"/>, and (d) always matches at least one row.
/// Each generated statement here (jsonb_set, the scope->>'…' predicate) is genuinely Postgres SQL, so
/// these branches can only be reached against a real server — there is no fakes-only route to them.
/// </summary>
[NotInParallel("PostgreSQL")]
public class DapperCollectiveEventApplierCoverageTests : PostgresTestBase {

  private const string TABLE = "wh_per_collective_dapper_coverage";

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
      new { id, data = $"{{\"Status\": \"{status}\"}}", scope = $"{{\"t\": \"{tenantId}\"}}" });
  }

  private async Task<string?> _statusAsync(Guid id) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    return await conn.ExecuteScalarAsync<string?>(
      $"SELECT data->>'Status' FROM {TABLE} WHERE id = @id", new { id });
  }

  private static CollectiveApplyEntry _entry() => new(
    ModelType: typeof(_jobModel),
    EventType: typeof(_archiveEvent),
    HandlerType: typeof(_jobPerspective),
    MethodName: nameof(_jobPerspective.Archive),
    ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (h, e, q) => ((_jobPerspective)h).Archive((_archiveEvent)e));

  private sealed class _jobModel {
    public string Status { get; set; } = "";
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

  // ── onBatchApplied callback (lines 157-158) ──────────────────────────────

  [Test]
  public async Task ApplyAsync_OnBatchAppliedCallback_FiresOnceAfterTheOnlyBatchAsync() {
    // If this regresses, a caller riding a lease-renewal heartbeat on the per-batch progress callback
    // never gets a signal during the single batch an ordinary small apply takes, so the lease can expire
    // mid-apply and another worker picks up (and reprocesses) the same cohort.
    await _createTableAsync();
    var job = Guid.NewGuid();
    await _seedAsync(job, "t-cov-batch", "Active");
    var batchCallCount = 0;

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      _entry(), new _jobPerspective(), new _archiveEvent { Scope = new TenantCollectiveScope("t-cov-batch") },
      new TenantCollectiveScopeResolver(), ConnectionFactory, TABLE, _noSiblings, CollectiveApplyOptions.Default,
      logger: null, hookRegistry: null, onBatchApplied: _ => { batchCallCount++; return ValueTask.CompletedTask; });

    await Assert.That(affected).IsEqualTo(1)
      .Because("The single matching row must be applied exactly once.");
    await Assert.That(await _statusAsync(job)).IsEqualTo("Archived")
      .Because("The spec's setter must actually have run against the matched row, not just report a count.");
    await Assert.That(batchCallCount).IsEqualTo(1)
      .Because("Exactly one keyset batch ran for this cohort, so the progress callback must fire exactly once — not zero, not more.");
  }

  // ── Connection returned closed by the factory (lines 181-182) ───────────

  private sealed class _unopenedConnectionFactory(string connectionString) : IDbConnectionFactory {
    public Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IDbConnection>(new NpgsqlConnection(connectionString));
  }

  [Test]
  public async Task ApplyAsync_FactoryReturnsAnUnopenedConnection_OpensItBeforeRunningTheBatchAsync() {
    // IDbConnectionFactory's own contract says a returned connection "is not opened automatically -
    // caller must open it." If this regresses, any factory honoring that contract (unlike the
    // eager-opening PostgresConnectionFactory every other test in this file's sibling suite uses) would
    // make the very first command of the batch fail against a closed connection instead of the applier
    // transparently opening it.
    await _createTableAsync();
    var job = Guid.NewGuid();
    await _seedAsync(job, "t-cov-open", "Active");
    var factory = new _unopenedConnectionFactory(ConnectionString);

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      _entry(), new _jobPerspective(), new _archiveEvent { Scope = new TenantCollectiveScope("t-cov-open") },
      new TenantCollectiveScopeResolver(), factory, TABLE, _noSiblings, CollectiveApplyOptions.Default, default);

    await Assert.That(affected).IsEqualTo(1)
      .Because("The applier must open the closed connection itself before issuing any command on it.");
    await Assert.That(await _statusAsync(job)).IsEqualTo("Archived");
  }

  // ── Positive StatementTimeoutSeconds — the SET LOCAL branch (lines 189-190, 194, 196-197) ─────────

  [Test]
  public async Task ApplyAsync_WithPositiveStatementTimeout_StillAppliesAndCommitsAsync() {
    // SET LOCAL statement_timeout is the only form that survives PgBouncer transaction pooling, and it
    // is the sole guard against a runaway batch holding locks indefinitely. If this regresses, opting a
    // handler into a statement timeout would either fail to issue the SET LOCAL command or poison the
    // batch transaction, silently disabling that guard for every apply that configures one.
    await _createTableAsync();
    var job = Guid.NewGuid();
    await _seedAsync(job, "t-cov-timeout", "Active");
    var options = CollectiveApplyOptions.Default with { StatementTimeoutSeconds = 5 };

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      _entry(), new _jobPerspective(), new _archiveEvent { Scope = new TenantCollectiveScope("t-cov-timeout") },
      new TenantCollectiveScopeResolver(), ConnectionFactory, TABLE, _noSiblings, options, default);

    await Assert.That(affected).IsEqualTo(1)
      .Because("An ordinary, fast batch must still complete and commit once a positive statement_timeout is configured.");
    await Assert.That(await _statusAsync(job)).IsEqualTo("Archived")
      .Because("The row must actually be updated — a poisoned transaction from a broken SET LOCAL would leave it unchanged.");
  }

  // ── Zero matching rows — the skip-without-error branch (lines 222-223) ──

  [Test]
  public async Task ApplyAsync_NoRowsMatchTheScope_ReturnsZeroWithoutErrorAsync() {
    // A collective event legitimately misses most perspectives most of the time (an empty or
    // not-yet-populated cohort is the common case, not an error). If this regresses, an apply whose
    // batch SELECT finds no ids would throw or hang instead of quietly returning zero, which would take
    // down the whole apply loop rather than leaving it a routine no-op.
    await _createTableAsync();
    var unrelated = Guid.NewGuid();
    await _seedAsync(unrelated, "t-cov-other", "Active");

    var affected = await DapperCollectiveEventApplier<_jobModel>.ApplyAsync(
      _entry(), new _jobPerspective(), new _archiveEvent { Scope = new TenantCollectiveScope("t-cov-empty") },
      new TenantCollectiveScopeResolver(), ConnectionFactory, TABLE, _noSiblings, CollectiveApplyOptions.Default, default);

    await Assert.That(affected).IsEqualTo(0)
      .Because("No row's scope matches t-cov-empty, so the batch's SELECT returns no ids and nothing is updated.");
    await Assert.That(await _statusAsync(unrelated)).IsEqualTo("Active")
      .Because("An unrelated tenant's row must be left untouched by the no-op apply.");
  }
}
