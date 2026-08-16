using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 109: each instance records its own lifecycle phase and library version on its
/// instance row. Two independent requirements land on that same fact — the standby handshake
/// cannot wait for peers to reach a state nobody can observe, and a load-balanced status surface
/// can only report what instances have written down. During a mixed-version rollout, "which
/// instances are on which version" is the first question anyone asks.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/109_InstanceState.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class InstanceStateSqlTests : EFCoreTestBase {

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private async Task<Guid> _joinFleetAsync(CancellationToken ct) {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    var id = (Guid)TrackedGuid.NewMedo();
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(id, "state-svc", "state-host", 1), ct);
    return id;
  }

  [Test]
  [Timeout(60000)]
  public async Task RecordInstanceState_WritesPhaseAndVersion_WithoutTouchingLivenessAsync(
      CancellationToken cancellationToken) {
    var id = await _joinFleetAsync(cancellationToken);
    await using var conn = await _openAsync(cancellationToken);

    // Backdate the heartbeat so we can prove the state write does not refresh liveness — a
    // transition is a fact about the pipeline, not evidence the process is healthy.
    await using (var backdate = conn.CreateCommand()) {
      backdate.CommandText = "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '5 minutes' WHERE instance_id = @id";
      backdate.Parameters.AddWithValue("id", id);
      await backdate.ExecuteNonQueryAsync(cancellationToken);
    }

    await using (var record = conn.CreateCommand()) {
      record.CommandText = "SELECT record_instance_state(@id, 'StandingBy', '0.9.4-alpha.3')";
      record.Parameters.AddWithValue("id", id);
      var found = await record.ExecuteScalarAsync(cancellationToken);
      await Assert.That(found is true).IsTrue()
        .Because("the row exists — the caller learns its write landed");
    }

    await using var read = conn.CreateCommand();
    read.CommandText = @"SELECT lifecycle_phase, library_version,
                                (NOW() - last_heartbeat_at) > INTERVAL '4 minutes'
                         FROM wh_service_instances WHERE instance_id = @id";
    read.Parameters.AddWithValue("id", id);
    await using var reader = await read.ExecuteReaderAsync(cancellationToken);
    await Assert.That(await reader.ReadAsync(cancellationToken)).IsTrue();
    await Assert.That(reader.GetString(0)).IsEqualTo("StandingBy");
    await Assert.That(reader.GetString(1)).IsEqualTo("0.9.4-alpha.3");
    await Assert.That(reader.GetBoolean(2)).IsTrue()
      .Because("recording state must not refresh the heartbeat — state is not liveness, and a "
             + "standing-by zombie must still be reapable");
  }

  [Test]
  [Timeout(60000)]
  public async Task RecordInstanceState_ForAnUnknownInstance_ReportsFalseNotAnErrorAsync(
      CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);

    await using var record = conn.CreateCommand();
    record.CommandText = "SELECT record_instance_state(@id, 'Connecting', NULL)";
    record.Parameters.AddWithValue("id", (Guid)TrackedGuid.NewMedo());
    var found = await record.ExecuteScalarAsync(cancellationToken);

    await Assert.That(found is false).IsTrue()
      .Because("early startup transitions happen before the instance has heartbeated its row "
             + "into existence — that is an expected condition, not an error");
  }

  [Test]
  [Timeout(60000)]
  public async Task RecordInstanceState_NullVersion_KeepsThePreviouslyRecordedOneAsync(
      CancellationToken cancellationToken) {
    var id = await _joinFleetAsync(cancellationToken);
    await using var conn = await _openAsync(cancellationToken);

    await using (var first = conn.CreateCommand()) {
      first.CommandText = "SELECT record_instance_state(@id, 'Migrating', '0.9.4')";
      first.Parameters.AddWithValue("id", id);
      await first.ExecuteNonQueryAsync(cancellationToken);
    }
    await using (var second = conn.CreateCommand()) {
      second.CommandText = "SELECT record_instance_state(@id, 'Running', NULL)";
      second.Parameters.AddWithValue("id", id);
      await second.ExecuteNonQueryAsync(cancellationToken);
    }

    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT lifecycle_phase, library_version FROM wh_service_instances WHERE instance_id = @id";
    read.Parameters.AddWithValue("id", id);
    await using var reader = await read.ExecuteReaderAsync(cancellationToken);
    await Assert.That(await reader.ReadAsync(cancellationToken)).IsTrue();
    await Assert.That(reader.GetString(0)).IsEqualTo("Running");
    await Assert.That(reader.GetString(1)).IsEqualTo("0.9.4")
      .Because("a phase transition without a version in hand must not erase the version already "
             + "on record — during a mixed-version rollout that column is the first thing read");
  }
}
