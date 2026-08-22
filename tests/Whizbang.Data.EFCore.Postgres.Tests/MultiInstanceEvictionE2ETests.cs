using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Multi-instance end-to-end coverage for the eviction fence, driven through the REAL coordinator
/// layer (<see cref="EFCoreWorkCoordinator{TDbContext}"/>) rather than raw SQL — each simulated
/// instance is its own coordinator over its own DbContext, exactly as separate pods would be.
///
/// <para>The lifecycle under test is the one the fence exists for: a peer pauses (long collection,
/// partition, throttled node), a live instance's ordinary heartbeat opportunistically reaps it and
/// releases its leases, and then the paused process RESUMES — and must be refused, not silently
/// re-admitted to a fleet that has already redistributed its work.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/106_InstanceEvictionFencing.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class MultiInstanceEvictionE2ETests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinatorFor(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  private static HeartbeatRequest _requestFor(Guid instanceId, string host) =>
    new(InstanceId: instanceId, ServiceName: "e2e-svc", HostName: host, ProcessId: 1);

  [Test]
  [Timeout(120000)]
  public async Task ZombieLifecycle_PeerReapedByLiveHeartbeat_ResumedZombieIsRefusedAndItsLeasesReleasedAsync(
      CancellationToken cancellationToken) {
    // ── the fleet: a live instance and a peer that will "pause" ────────────
    await using var liveCtx = CreateDbContext();
    await using var zombieCtx = CreateDbContext();
    var live = _coordinatorFor(liveCtx);
    var zombie = _coordinatorFor(zombieCtx);

    var liveId = (Guid)TrackedGuid.NewMedo();
    var zombieId = (Guid)TrackedGuid.NewMedo();

    // Both join the fleet through the real path.
    await Assert.That(await live.RecordHeartbeatAsync(_requestFor(liveId, "host-live"), cancellationToken)).IsTrue();
    await Assert.That(await zombie.RecordHeartbeatAsync(_requestFor(zombieId, "host-zombie"), cancellationToken)).IsTrue();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    // The zombie holds a claimed outbox lease — work the fleet must recover when it dies.
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'topic', 'T', '{}', '{}', 1, 0, NOW(), @inst, NOW() + INTERVAL '5 minutes', @sid, 0)";
      cmd.Parameters.AddWithValue("msg", msgId);
      cmd.Parameters.AddWithValue("inst", zombieId);
      cmd.Parameters.AddWithValue("sid", streamId);
      await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ── the pause: the zombie goes silent past the definitive-dead cutoff ──
    // (simulated by backdating its heartbeat — the process itself is still alive)
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '10 minutes' WHERE instance_id = @id";
      cmd.Parameters.AddWithValue("id", zombieId);
      await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ── the reap: the LIVE instance's ordinary heartbeat notices and cleans up ──
    await Assert.That(await live.RecordHeartbeatAsync(_requestFor(liveId, "host-live"), cancellationToken))
      .IsTrue().Because("the live instance is not evicted; its heartbeat proceeds normally");

    await Assert.That(await _rowExistsAsync(conn, zombieId, cancellationToken)).IsFalse()
      .Because("the live heartbeat's opportunistic cleanup must have reaped the stale peer");
    await Assert.That(await _isTombstonedAsync(conn, zombieId, cancellationToken)).IsTrue()
      .Because("reaping must tombstone, or the resume below would silently rejoin");
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT instance_id IS NULL AND lease_expiry IS NULL FROM wh_outbox WHERE message_id = @msg";
      cmd.Parameters.AddWithValue("msg", msgId);
      await Assert.That((bool)(await cmd.ExecuteScalarAsync(cancellationToken))!).IsTrue()
        .Because("the zombie's lease must be released so the fleet can reclaim its work");
    }

    // ── the resume: the paused process wakes up and heartbeats again ───────
    var readmitted = await zombie.RecordHeartbeatAsync(_requestFor(zombieId, "host-zombie"), cancellationToken);

    await Assert.That(readmitted).IsFalse()
      .Because("this is the defect the fence closes: before migration 106 this call silently "
             + "re-inserted the row and the zombie rejoined a fleet that had already "
             + "redistributed its work");
    await Assert.That(await _rowExistsAsync(conn, zombieId, cancellationToken)).IsFalse()
      .Because("a refused heartbeat must leave no instance row behind");

    // ── the fleet carries on: the live instance is unaffected throughout ──
    await Assert.That(await live.RecordHeartbeatAsync(_requestFor(liveId, "host-live"), cancellationToken)).IsTrue();
  }

  [Test]
  [Timeout(120000)]
  public async Task ManyInstances_HeartbeatConcurrently_AllRegisterAndNoneInterfereAsync(
      CancellationToken cancellationToken) {
    // Five instances joining at once — the cold-start shape. Each gets its own coordinator and
    // DbContext, as separate pods would. None is stale, so nobody reaps anybody.
    const int FLEET_SIZE = 5;
    var contexts = new List<WorkCoordinationDbContext>();
    try {
      var ids = new Guid[FLEET_SIZE];
      var tasks = new Task<bool>[FLEET_SIZE];
      for (var i = 0; i < FLEET_SIZE; i++) {
        var ctx = CreateDbContext();
        contexts.Add(ctx);
        ids[i] = (Guid)TrackedGuid.NewMedo();
        tasks[i] = _coordinatorFor(ctx).RecordHeartbeatAsync(_requestFor(ids[i], $"host-{i}"), cancellationToken);
      }

      var results = await Task.WhenAll(tasks);

      await Assert.That(results.All(accepted => accepted)).IsTrue()
        .Because("no instance is evicted, so every concurrent join must be accepted");

      await using var conn = new NpgsqlConnection(ConnectionString);
      await conn.OpenAsync(cancellationToken);
      foreach (var id in ids) {
        await Assert.That(await _rowExistsAsync(conn, id, cancellationToken)).IsTrue()
          .Because("every concurrently-joining instance must end up registered");
      }
    } finally {
      foreach (var ctx in contexts) {
        await ctx.DisposeAsync();
      }
    }
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<bool> _rowExistsAsync(NpgsqlConnection conn, Guid instanceId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM wh_service_instances WHERE instance_id = @id)";
    cmd.Parameters.AddWithValue("id", instanceId);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task<bool> _isTombstonedAsync(NpgsqlConnection conn, Guid instanceId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM wh_instance_evictions WHERE instance_id = @id)";
    cmd.Parameters.AddWithValue("id", instanceId);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }
}
