using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 108: the recorded capability holdings — <c>wh_instance_capabilities</c> keyed by
/// (instance, capability), with <c>acquired_at</c> answering "how long has this instance been the
/// migrator". The rule the design turns on: <b>the lock decides, the row reports</b> — these
/// functions record and release derived state, and <c>record_capability</c> is also where the
/// eviction fence reaches exclusive work: an evicted instance is refused at acquisition.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/108_InstanceCapabilities.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class InstanceCapabilitiesSqlTests : EFCoreTestBase {

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private static async Task _heartbeatAsync(WorkCoordinationDbContext ctx, Guid instanceId, CancellationToken ct) {
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "cap-svc", "cap-host", 1), ct);
  }

  private static async Task<T?> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var result = await cmd.ExecuteScalarAsync();
    return result is T t ? t : default;
  }

  [Test]
  [Timeout(60000)]
  public async Task RecordCapability_ForALiveInstance_RecordsTheHoldingWithAcquiredAtAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _heartbeatAsync(ctx, instanceId, cancellationToken);
    await using var conn = await _openAsync(cancellationToken);

    var recorded = await _scalarAsync<bool>(conn,
      "SELECT record_capability(@id, 'migrator')", ("id", instanceId));

    await Assert.That(recorded).IsTrue();
    var acquiredAt = await _scalarAsync<DateTime>(conn,
      "SELECT acquired_at::timestamp FROM wh_instance_capabilities WHERE instance_id = @id AND capability = 'migrator'",
      ("id", instanceId));
    await Assert.That(acquiredAt).IsNotEqualTo(default(DateTime))
      .Because("acquired_at is the field that answers 'how long has this instance been the migrator'");

    // Re-recording is idempotent and does NOT touch acquired_at — tenure is measured from the
    // original acquisition, not the latest heartbeat of it.
    var again = await _scalarAsync<bool>(conn,
      "SELECT record_capability(@id, 'migrator')", ("id", instanceId));
    await Assert.That(again).IsTrue();
    var acquiredAtAfter = await _scalarAsync<DateTime>(conn,
      "SELECT acquired_at::timestamp FROM wh_instance_capabilities WHERE instance_id = @id AND capability = 'migrator'",
      ("id", instanceId));
    await Assert.That(acquiredAtAfter).IsEqualTo(acquiredAt);
  }

  [Test]
  [Timeout(60000)]
  public async Task RecordCapability_ForAnEvictedInstance_IsRefused_TheFenceReachesExclusiveWorkAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _heartbeatAsync(ctx, instanceId, cancellationToken);
    await using var conn = await _openAsync(cancellationToken);

    // Evict it — the tombstone cleanup_stale_instances writes.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "INSERT INTO wh_instance_evictions (instance_id, reason) VALUES (@id, 'test-eviction')";
      cmd.Parameters.AddWithValue("id", instanceId);
      await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    var recorded = await _scalarAsync<bool>(conn,
      "SELECT record_capability(@id, 'migrator')", ("id", instanceId));

    await Assert.That(recorded).IsFalse()
      .Because("delivering the fence through the heartbeat bounds the notice window; refusing an "
             + "evicted instance at capability acquisition closes it for exclusive work");
    var held = await _scalarAsync<long>(conn,
      "SELECT COUNT(*) FROM wh_instance_capabilities WHERE instance_id = @id", ("id", instanceId));
    await Assert.That(held).IsEqualTo(0);
  }

  [Test]
  [Timeout(60000)]
  public async Task ReleaseCapability_RemovesTheHoldingAsync(CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _heartbeatAsync(ctx, instanceId, cancellationToken);
    await using var conn = await _openAsync(cancellationToken);

    await _scalarAsync<bool>(conn, "SELECT record_capability(@id, 'maintainer')", ("id", instanceId));
    await _scalarAsync<object>(conn, "SELECT release_capability(@id, 'maintainer')", ("id", instanceId));

    var held = await _scalarAsync<long>(conn,
      "SELECT COUNT(*) FROM wh_instance_capabilities WHERE instance_id = @id AND capability = 'maintainer'",
      ("id", instanceId));
    await Assert.That(held).IsEqualTo(0);
  }

  [Test]
  [Timeout(60000)]
  public async Task ReapingAnInstance_CascadesItsHoldings_NoSeparateReaperNeededAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _heartbeatAsync(ctx, instanceId, cancellationToken);
    await using var conn = await _openAsync(cancellationToken);
    await _scalarAsync<bool>(conn, "SELECT record_capability(@id, 'migrator')", ("id", instanceId));

    // The real reap path: backdate the heartbeat past the cutoff and run the cleanup.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '1 hour' WHERE instance_id = @id";
      cmd.Parameters.AddWithValue("id", instanceId);
      await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT COUNT(*) FROM cleanup_stale_instances(NOW() - INTERVAL '30 minutes', NOW() - INTERVAL '30 minutes')";
      _ = await cmd.ExecuteScalarAsync(cancellationToken);
    }

    var held = await _scalarAsync<long>(conn,
      "SELECT COUNT(*) FROM wh_instance_capabilities WHERE instance_id = @id", ("id", instanceId));
    await Assert.That(held).IsEqualTo(0)
      .Because("holdings ride the same rails as liveness: stale instances are genuinely DELETEd "
             + "and the foreign key cascades — reaping stays free");
  }
}
