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
[Category("Shard2")]
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

  private MaintenanceWorker _buildWorker(IPerspectiveRowDestructionGuard? guard = null) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    if (guard is not null) {
      services.AddSingleton(guard);
    }
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
  [Timeout(60000)]
  public async Task PresenceReconcile_DropsRowsAbsentFromEveryAnnouncer_KeepsHeldAndPresentAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn);
    var orphaned = Guid.NewGuid();   // absent from the announcer — a pre-rebuild eviction resurrected
    var present = Guid.NewGuid();    // announcer still holds it — alive
    var held = Guid.NewGuid();       // absent but guard-held — untouchable
    await _seedAsync(conn, FOLLOWER_TABLE, orphaned, idleHours: 0);
    await _seedAsync(conn, FOLLOWER_TABLE, present, idleHours: 0);
    await _seedAsync(conn, FOLLOWER_TABLE, held, idleHours: 0);
    await _seedAsync(conn, LEADER_TABLE, present, idleHours: 0);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await coordinator.HoldPerspectiveRowDestructionAsync(
      [new PerspectiveRowRef(FOLLOWER_TABLE, held)], DateTimeOffset.UtcNow.AddHours(1), cancellationToken);

    var removed = await coordinator.ReconcileFollowerPresenceAsync(
      FOLLOWER_TABLE, [LEADER_TABLE], cancellationToken);

    await Assert.That(removed).IsEqualTo(1);
    await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, orphaned)).IsFalse()
      .Because("absent from every announcer = an eviction decision that predates the rebuild; presence repairs what the edge cannot re-fire");
    await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, present)).IsTrue()
      .Because("the announcer still holds the stream — the conservative all-absent rule never over-deletes");
    await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, held)).IsTrue()
      .Because("holds are honored on every destruction path, the presence reconcile included");
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

  /// <summary>A follower guard that defers until its external resource is marked clean.</summary>
  private sealed class FollowerGuard : IPerspectiveRowDestructionGuard {
    public bool Clean { get; set; }
    public List<PerspectiveRowDestructionTarget> AfterReap { get; } = [];
    public IReadOnlyCollection<Type> GuardedModels => [typeof(SgFollowerModel)];

    public ValueTask<IReadOnlyDictionary<Guid, PerspectiveRowDecision>> OnBeforeReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> targets, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult<IReadOnlyDictionary<Guid, PerspectiveRowDecision>>(
        targets.ToDictionary(t => t.RowId, _ => Clean
          ? PerspectiveRowDecision.Proceed()
          : PerspectiveRowDecision.Defer(DateTimeOffset.UtcNow.AddHours(1))));

    public ValueTask OnAfterReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> released, CancellationToken cancellationToken = default) {
      AfterReap.AddRange(released);
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task CascadedRows_PassThroughTheGuard_DeferSurvivesAndRequeuesTheSeedAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn);
    var stream = Guid.NewGuid();
    await _seedAsync(conn, LEADER_TABLE, stream, idleHours: 48);
    await _seedAsync(conn, FOLLOWER_TABLE, stream, idleHours: 0);
    PerspectiveStreamGroupRegistry.Register(typeof(SgLeaderModel), "sg-guarded", announce: true, follow: true, bridge: false);
    PerspectiveStreamGroupRegistry.Register(typeof(SgFollowerModel), "sg-guarded", announce: true, follow: true, bridge: false);
    var guard = new FollowerGuard { Clean = false };
    try {
      // Cycle 1: leader dies by TTL; the CASCADED follower row is offered to its guard, which
      // defers — the row survives, and the seed re-queues for the next cycle.
      var worker = _buildWorker(guard);
      await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);
      await Assert.That(await _survivesAsync(conn, LEADER_TABLE, stream)).IsFalse();
      await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, stream)).IsTrue()
        .Because("a cascaded row of a guarded perspective passes through the SAME guard — a "
               + "resource-referencing row cannot slip out through the cascade path");
      await Assert.That(guard.AfterReap).Count().IsEqualTo(0);

      // The resource cleans up; age the hold; the re-queued seed re-offers and converges.
      guard.Clean = true;
      await using (var age = new NpgsqlCommand(
          "UPDATE wh_perspective_row_hold SET hold_until = NOW() - INTERVAL '1 second' WHERE table_name = @t", conn)) {
        age.Parameters.AddWithValue("t", FOLLOWER_TABLE);
        await age.ExecuteNonQueryAsync(cancellationToken);
      }
      await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);
      await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, stream)).IsFalse()
        .Because("the deferred cascade re-queued its seed — convergence, not loss");
      await Assert.That(guard.AfterReap.Select(t => t.RowId)).Contains(stream)
        .Because("OnAfterReap sees the cascade-released set too");
    } finally {
      Whizbang.Testing.StreamGroupRegistryTestSeam.Clear();
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task RebuildThenSweep_ConvergesToTheIdenticalEvictedSetAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn);
    var stream = Guid.NewGuid();
    await _seedAsync(conn, LEADER_TABLE, stream, idleHours: 48);
    await _seedAsync(conn, FOLLOWER_TABLE, stream, idleHours: 0);
    PerspectiveStreamGroupRegistry.Register(typeof(SgLeaderModel), "sg-rebuild", announce: true, follow: true, bridge: false);
    PerspectiveStreamGroupRegistry.Register(typeof(SgFollowerModel), "sg-rebuild", announce: true, follow: true, bridge: false);
    try {
      var worker = _buildWorker();
      await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);
      await Assert.That(await _survivesAsync(conn, LEADER_TABLE, stream)).IsFalse();
      await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, stream)).IsFalse();

      // A rebuild resurrects both rows WITH THEIR OLD BUSINESS TIMESTAMPS (retention keys on
      // event time, never wall clock — the purity invariant). The next cycle must converge to
      // the identical evicted set: leader re-evicted by its own rule, follower re-cascaded.
      await _seedAsync(conn, LEADER_TABLE, stream, idleHours: 48);
      await _seedAsync(conn, FOLLOWER_TABLE, stream, idleHours: 0);
      await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);

      await Assert.That(await _survivesAsync(conn, LEADER_TABLE, stream)).IsFalse()
        .Because("business-time purity: the resurrected row carries its old clock and re-evicts");
      await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, stream)).IsFalse()
        .Because("the announcer's re-eviction re-fires the edge — rebuild-then-sweep converges "
               + "to the identical evicted set with zero bookkeeping");
    } finally {
      Whizbang.Testing.StreamGroupRegistryTestSeam.Clear();
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task AnnouncerRebuiltAlone_ReEvicts_AndTheCascadeNoOpsIdempotentlyAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn);
    var stream = Guid.NewGuid();
    await _seedAsync(conn, LEADER_TABLE, stream, idleHours: 48);
    await _seedAsync(conn, FOLLOWER_TABLE, stream, idleHours: 0);
    PerspectiveStreamGroupRegistry.Register(typeof(SgLeaderModel), "sg-idem", announce: true, follow: true, bridge: false);
    PerspectiveStreamGroupRegistry.Register(typeof(SgFollowerModel), "sg-idem", announce: true, follow: true, bridge: false);
    try {
      var worker = _buildWorker();
      await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);

      // Only the ANNOUNCER is rebuilt (old business timestamps); the follower stays clean.
      await _seedAsync(conn, LEADER_TABLE, stream, idleHours: 48);
      await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);

      await Assert.That(await _survivesAsync(conn, LEADER_TABLE, stream)).IsFalse()
        .Because("the announcer's own sweep is self-contained and re-evicts after its rebuild");
      await Assert.That(await _survivesAsync(conn, FOLLOWER_TABLE, stream)).IsFalse();
      await using var journal = new NpgsqlCommand("SELECT COUNT(*) FROM wh_row_eviction_journal", conn);
      var remaining = Convert.ToInt64(
        await journal.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
      await Assert.That(remaining).IsEqualTo(0L)
        .Because("the cascade onto already-absent follower rows is an idempotent no-op — nothing "
               + "re-queues, nothing errors, the journal drains clean");
    } finally {
      Whizbang.Testing.StreamGroupRegistryTestSeam.Clear();
    }
  }
}
