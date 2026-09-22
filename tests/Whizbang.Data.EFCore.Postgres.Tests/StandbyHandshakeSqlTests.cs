using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 110: the standby request — a breaking migration is a planned outage, and the
/// framework's job is to convert an outage that today is silent and corrupting into one that is
/// bounded, announced and observable. The request is a single durable fleet-wide fact: who asked,
/// at what version, when. One at a time — two concurrent migrators is exactly what election
/// exists to prevent, and the request record must not quietly disagree with it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/110_StandbyHandshake.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class StandbyHandshakeSqlTests : EFCoreTestBase {

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private async Task<Guid> _joinFleetAsync(CancellationToken ct) {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    var id = (Guid)TrackedGuid.NewMedo();
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(id, "standby-svc", "standby-host", 1), ct);
    return id;
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
  public async Task RequestStandby_IsSingleAndIdempotent_SecondRequesterIsRefusedAsync(
      CancellationToken cancellationToken) {
    var migrator = await _joinFleetAsync(cancellationToken);
    var rival = await _joinFleetAsync(cancellationToken);
    await using var conn = await _openAsync(cancellationToken);
    try {
      var first = await _scalarAsync<bool>(conn,
        "SELECT request_standby(@id, '0.9.5')", ("id", migrator));
      await Assert.That(first).IsTrue();

      var again = await _scalarAsync<bool>(conn,
        "SELECT request_standby(@id, '0.9.5')", ("id", migrator));
      await Assert.That(again).IsTrue()
        .Because("re-requesting one's own active request is idempotent, not an error");

      var rivalResult = await _scalarAsync<bool>(conn,
        "SELECT request_standby(@id, '0.9.6')", ("id", rival));
      await Assert.That(rivalResult).IsFalse()
        .Because("one request at a time — two concurrent migrators is exactly what election "
               + "exists to prevent, and this record must not quietly disagree with it");
    } finally {
      await _scalarAsync<bool>(conn, "SELECT clear_standby(@id)", ("id", migrator));
    }
  }

  [Test]
  [Timeout(60000)]
  public async Task ClearStandby_OnlyTheRequesterClears_DeathClearsNothingButLivenessBoundsTheWaitAsync(
      CancellationToken cancellationToken) {
    var migrator = await _joinFleetAsync(cancellationToken);
    var other = await _joinFleetAsync(cancellationToken);
    await using var conn = await _openAsync(cancellationToken);
    try {
      await _scalarAsync<bool>(conn, "SELECT request_standby(@id, '0.9.5')", ("id", migrator));

      var strangerClear = await _scalarAsync<bool>(conn, "SELECT clear_standby(@id)", ("id", other));
      await Assert.That(strangerClear).IsFalse()
        .Because("only the requester withdraws its own request — anything else lets a confused "
               + "peer cancel a handshake mid-migration");

      var requesterClear = await _scalarAsync<bool>(conn, "SELECT clear_standby(@id)", ("id", migrator));
      await Assert.That(requesterClear).IsTrue();
    } finally {
      await _scalarAsync<bool>(conn, "SELECT clear_standby(@id)", ("id", migrator));
    }
  }

  [Test]
  [Timeout(60000)]
  public async Task EvictInstance_RecordsWhoWhenWhy_AndTheFenceHoldsAsync(CancellationToken cancellationToken) {
    var migrator = await _joinFleetAsync(cancellationToken);
    var target = await _joinFleetAsync(cancellationToken);
    await using var conn = await _openAsync(cancellationToken);

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT evict_instance(@target, @by, 'standby handshake: no acknowledgment within the wait bound')";
      cmd.Parameters.AddWithValue("target", target);
      cmd.Parameters.AddWithValue("by", migrator);
      await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT evicted_by, reason FROM wh_instance_evictions WHERE instance_id = @id";
    read.Parameters.AddWithValue("id", target);
    await using var reader = await read.ExecuteReaderAsync(cancellationToken);
    await Assert.That(await reader.ReadAsync(cancellationToken)).IsTrue();
    await Assert.That(reader.GetGuid(0)).IsEqualTo(migrator)
      .Because("an eviction forcibly stops a process — an operator finding a stopped instance "
             + "needs who issued it, when, and why, without archaeology");
    await Assert.That(reader.GetString(1)).Contains("standby handshake");
    await reader.CloseAsync();

    // The existing fence holds against the deliberate eviction too.
    var heartbeat = await _scalarAsync<bool>(conn,
      "SELECT record_heartbeat(@id, 'standby-svc', 'standby-host', 1, '{}'::jsonb)", ("id", target));
    await Assert.That(heartbeat).IsFalse();
    var capability = await _scalarAsync<bool>(conn,
      "SELECT record_capability(@id, 'migrator')", ("id", target));
    await Assert.That(capability).IsFalse();
  }
}
