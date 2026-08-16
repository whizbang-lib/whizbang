#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The stream-group cascade on a real database: the sweeps JOURNAL their victims in the same
/// statement, the journal drain is an atomic claim, the cascade delete honors holds, and — the
/// convergence lock — one real maintenance cycle evicts a leader row by TTL and its group sibling
/// leaves in the SAME cycle through the closure. No more half-dead streams.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/112_StreamGroupCascadeAndApplyPathFold.sql</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class StreamGroupCascadeSqlTests : EFCoreTestBase {
  private const string LEADER_TABLE = "wh_per_sg_leader";
  private const string FOLLOWER_TABLE = "wh_per_sg_follower";

  private sealed class SgLeaderModel;
  private sealed class SgFollowerModel;

  private IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task _arrangeAsync(NpgsqlConnection conn) {
    await using var ddl = new NpgsqlCommand($@"
      DROP TABLE IF EXISTS {LEADER_TABLE}; DROP TABLE IF EXISTS {FOLLOWER_TABLE};
      CREATE TABLE {LEADER_TABLE} (
        id UUID NOT NULL PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ, expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
      CREATE TABLE {FOLLOWER_TABLE} (LIKE {LEADER_TABLE} INCLUDING ALL);
      DELETE FROM wh_row_eviction_journal;
      DELETE FROM wh_perspective_row_hold WHERE table_name IN ('{LEADER_TABLE}', '{FOLLOWER_TABLE}');
      DELETE FROM wh_perspective_registry WHERE table_name IN ('{LEADER_TABLE}', '{FOLLOWER_TABLE}');
      INSERT INTO wh_perspective_registry
        (clr_type_name, table_name, schema_json, schema_hash, service_name,
         row_retention_enrolled, retention_enforcement_acknowledged, row_ttl_seconds)
      VALUES
        ('{typeof(SgLeaderModel).FullName}', '{LEADER_TABLE}', '{{}}'::jsonb, 'h', 'svc', TRUE, TRUE, 3600),
        ('{typeof(SgFollowerModel).FullName}', '{FOLLOWER_TABLE}', '{{}}'::jsonb, 'h', 'svc', FALSE, FALSE, NULL);", conn);
    await ddl.ExecuteNonQueryAsync();
  }

  private static async Task _seedAsync(NpgsqlConnection conn, string table, Guid id, int idleHours) {
    await using var cmd = new NpgsqlCommand($@"
      INSERT INTO {table} (id, data, metadata, scope, created_at, updated_at, version)
      VALUES (@id, '{{}}'::jsonb, '{{}}'::jsonb, '{{}}'::jsonb,
              NOW() - make_interval(hours => @h), NOW() - make_interval(hours => @h), 1)", conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("h", idleHours);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<bool> _survivesAsync(NpgsqlConnection conn, string table, Guid id) {
    await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {table} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    return Convert.ToInt64(
      await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
  }

  private MaintenanceWorker _buildWorker() {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  [Test]
  [Timeout(60000)]
  public async Task Sweep_JournalsItsVictims_InTheSameStatementAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn);
    var victim = Guid.NewGuid();
    await _seedAsync(conn, LEADER_TABLE, victim, idleHours: 48);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    await coordinator.ReapEnrolledPerspectiveRowsAsync(cancellationToken: cancellationToken);

    var drained = await coordinator.DrainRowEvictionJournalAsync(cancellationToken: cancellationToken);
    await Assert.That(drained).Contains(new PerspectiveRowRef(LEADER_TABLE, victim))
      .Because("the cascade must see exactly what died — journaled in the DELETE's own statement");

    var second = await coordinator.DrainRowEvictionJournalAsync(cancellationToken: cancellationToken);
    await Assert.That(second).Count().IsEqualTo(0)
      .Because("DELETE ... RETURNING is an atomic claim — a second drain (another replica) gets nothing");
  }

  [Test]
  [Timeout(60000)]
  public async Task CascadeDelete_HonorsHolds_AndRequeueIsIdempotentAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn);
    var held = Guid.NewGuid();
    var free = Guid.NewGuid();
    await _seedAsync(conn, FOLLOWER_TABLE, held, idleHours: 1);
    await _seedAsync(conn, FOLLOWER_TABLE, free, idleHours: 1);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await coordinator.HoldPerspectiveRowDestructionAsync(
      [new PerspectiveRowRef(FOLLOWER_TABLE, held)], DateTimeOffset.UtcNow.AddHours(1), cancellationToken);

    var deleted = await coordinator.CascadeDeletePerspectiveRowsAsync(
      FOLLOWER_TABLE, [held, free], cancellationToken);

    await Assert.That(deleted).IsEqualTo(1);
    await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, held)).IsTrue()
      .Because("a guard's Defer survives the cascade — the hold is honored on every eviction path");
    await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, free)).IsFalse();

    await coordinator.RequeueRowEvictionsAsync(
      [new PerspectiveRowRef(LEADER_TABLE, held), new PerspectiveRowRef(LEADER_TABLE, held)], cancellationToken);
    var requeued = await coordinator.DrainRowEvictionJournalAsync(cancellationToken: cancellationToken);
    await Assert.That(requeued).Count().IsEqualTo(1)
      .Because("requeue is idempotent — a deferred cascade re-seeds once, never duplicates");
  }

  [Test]
  [Timeout(120000)]
  public async Task LeaderTtlEviction_CascadesToTheFollower_InTheSameCycleAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn);
    var stream = Guid.NewGuid();
    var liveStream = Guid.NewGuid();
    await _seedAsync(conn, LEADER_TABLE, stream, idleHours: 48);      // expired — the origin
    await _seedAsync(conn, FOLLOWER_TABLE, stream, idleHours: 0);     // fresh, no own evictor
    await _seedAsync(conn, LEADER_TABLE, liveStream, idleHours: 0);   // alive — untouched
    await _seedAsync(conn, FOLLOWER_TABLE, liveStream, idleHours: 0);

    PerspectiveStreamGroupRegistry.Register(typeof(SgLeaderModel), "sg-e2e", announce: true, follow: true, bridge: false);
    PerspectiveStreamGroupRegistry.Register(typeof(SgFollowerModel), "sg-e2e", announce: true, follow: true, bridge: false);
    try {
      await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(_buildWorker(), cancellationToken);

      await Assert.That(await _survivesAsync(conn, LEADER_TABLE, stream)).IsFalse()
        .Because("the leader's own TTL evicted it — the origin");
      await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, stream)).IsFalse()
        .Because("the follower has NO evictor of its own — only the group closure can have taken it, "
               + "in the same cycle: no more half-dead streams");
      await Assert.That(await _survivesAsync(conn, LEADER_TABLE, liveStream)).IsTrue();
      await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, liveStream)).IsTrue()
        .Because("the cascade is per-stream — a live stream's rows are untouched in both members");
    } finally {
      Whizbang.Testing.StreamGroupRegistryTestSeam.Clear();
    }
  }
}
