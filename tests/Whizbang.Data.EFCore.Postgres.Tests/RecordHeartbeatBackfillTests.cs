using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 147: the heartbeat backfills the registry row's lifecycle phase and library version
/// and takes its peer-reap threshold from the caller. The row can be created before the run
/// control path has anything to record into it (record_instance_state is UPDATE-only and returns
/// false on a missing row), which left long-lived instances with blank phase and version for their
/// whole life. And the peer reap used a 30 s constant equal to the writer's fast cadence, so a beat
/// delayed by any latency could tombstone a live peer.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/147_HeartbeatRegistryBackfill.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RecordHeartbeatBackfillTests : EFCoreTestBase {

  private async Task<bool> _beatAsync(Guid id, string? phase, string? version, int? staleSeconds, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = new NpgsqlCommand(
      "SELECT record_heartbeat(@id, 'backfill-svc', 'backfill-host', 1, '{}'::jsonb, @phase, @version, @stale)", conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.Add(new NpgsqlParameter("phase", NpgsqlDbType.Text) { Value = (object?)phase ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Text) { Value = (object?)version ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("stale", NpgsqlDbType.Integer) { Value = (object?)staleSeconds ?? DBNull.Value });
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private async Task<(string? Phase, string? Version)?> _rowAsync(Guid id, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = new NpgsqlCommand(
      "SELECT lifecycle_phase, library_version FROM wh_service_instances WHERE instance_id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct)) {
      return null;
    }
    return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
  }

  private async Task _ageAsync(Guid id, TimeSpan age, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = new NpgsqlCommand(
      "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - @age WHERE instance_id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("age", age);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private async Task<bool> _overloadCountIsOneAsync(CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = new NpgsqlCommand(
      "SELECT COUNT(*) FROM pg_proc WHERE proname = 'record_heartbeat' AND pronamespace = (SELECT oid FROM pg_namespace WHERE nspname = current_schema())", conn);
    return (long)(await cmd.ExecuteScalarAsync(ct))! == 1;
  }

  [Test]
  [Timeout(60000)]
  public async Task FirstBeat_CreatesTheRowWithPhaseAndVersionAsync(CancellationToken cancellationToken) {
    var id = Guid.CreateVersion7();

    var accepted = await _beatAsync(id, "Running", "1.2.3", 150, cancellationToken);

    await Assert.That(accepted).IsTrue();
    var row = await _rowAsync(id, cancellationToken);
    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Value.Phase).IsEqualTo("Running")
      .Because("a row created by the heartbeat must not start blank when the beat knows the phase");
    await Assert.That(row.Value.Version).IsEqualTo("1.2.3");
  }

  [Test]
  [Timeout(60000)]
  public async Task LaterBeat_BackfillsARowThatWasCreatedBlankAsync(CancellationToken cancellationToken) {
    // The failure shape: the row exists (created by a legacy-shaped beat or a claim) with no
    // phase or version, and no later transition will ever write them.
    var id = Guid.CreateVersion7();
    _ = await _beatAsync(id, null, null, null, cancellationToken);
    var blank = await _rowAsync(id, cancellationToken);
    await Assert.That(blank!.Value.Phase).IsNull().Because("precondition: the row starts blank");

    _ = await _beatAsync(id, "Running", "2.0.0", 150, cancellationToken);

    var filled = await _rowAsync(id, cancellationToken);
    await Assert.That(filled!.Value.Phase).IsEqualTo("Running")
      .Because("the beat carries the live phase and the row takes it");
    await Assert.That(filled.Value.Version).IsEqualTo("2.0.0");
  }

  [Test]
  [Timeout(60000)]
  public async Task BeatWithNulls_KeepsWhatTheRowAlreadyHoldsAsync(CancellationToken cancellationToken) {
    // A host without run control wired beats with nulls; it must not erase what a transition or an
    // earlier beat recorded.
    var id = Guid.CreateVersion7();
    _ = await _beatAsync(id, "AcceptingCommands", "3.1.0", 150, cancellationToken);

    _ = await _beatAsync(id, null, null, null, cancellationToken);

    var row = await _rowAsync(id, cancellationToken);
    await Assert.That(row!.Value.Phase).IsEqualTo("AcceptingCommands")
      .Because("null on the beat means unknown, never blank");
    await Assert.That(row.Value.Version).IsEqualTo("3.1.0");
  }

  [Test]
  [Timeout(60000)]
  public async Task BeatWithANewPhase_OverwritesTheOldOneAsync(CancellationToken cancellationToken) {
    var id = Guid.CreateVersion7();
    _ = await _beatAsync(id, "Starting", "1.0.0", 150, cancellationToken);

    _ = await _beatAsync(id, "Running", "1.0.0", 150, cancellationToken);

    var row = await _rowAsync(id, cancellationToken);
    await Assert.That(row!.Value.Phase).IsEqualTo("Running")
      .Because("the process is the authority on its own phase; a non-null value on the beat wins");
  }

  [Test]
  [Timeout(60000)]
  public async Task LegacyFiveArgumentCall_StillWorksAsync(CancellationToken cancellationToken) {
    // Callers compiled against the old shape keep working: the three new parameters default.
    var id = Guid.CreateVersion7();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(
      "SELECT record_heartbeat(@id, 'legacy-svc', 'legacy-host', 1, '{}'::jsonb)", conn);
    cmd.Parameters.AddWithValue("id", id);

    var accepted = (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;

    await Assert.That(accepted).IsTrue();
    var row = await _rowAsync(id, cancellationToken);
    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Value.Phase).IsNull();
  }

  [Test]
  [Timeout(60000)]
  public async Task ExactlyOneOverloadExistsAsync(CancellationToken cancellationToken) {
    // The migration runner force-replays duplicate overloads and the old five-parameter shape is
    // a strict prefix of the new one, so a second overload would make every call ambiguous.
    await Assert.That(await _overloadCountIsOneAsync(cancellationToken)).IsTrue()
      .Because("drop_all_overloads must have removed the pre-147 signature");
  }

  [Test]
  [Timeout(60000)]
  public async Task PeerLateByFortySeconds_SurvivesABeatCarryingTheDerivedThresholdAsync(CancellationToken cancellationToken) {
    // Before 147 the reap used 30 s: a peer whose slow beat (60 s cadence) was ten seconds late
    // was tombstoned by the next heartbeat from anyone. With the writer's derived 150 s the same
    // peer is untouched.
    var peer = Guid.CreateVersion7();
    var beater = Guid.CreateVersion7();
    _ = await _beatAsync(peer, "Running", "1.0.0", 150, cancellationToken);
    await _ageAsync(peer, TimeSpan.FromSeconds(40), cancellationToken);

    _ = await _beatAsync(beater, "Running", "1.0.0", 150, cancellationToken);

    var row = await _rowAsync(peer, cancellationToken);
    await Assert.That(row).IsNotNull()
      .Because("40 s is well inside a 150 s threshold; the peer is late, not dead");
  }

  [Test]
  [Timeout(60000)]
  public async Task PeerPastTheDerivedThreshold_IsStillReapedAsync(CancellationToken cancellationToken) {
    // The reap still works; it just uses the right number. No alive-lock is held for the peer, so
    // the lock-aware cleanup sees a silent instance past the cutoff and removes it.
    var peer = Guid.CreateVersion7();
    var beater = Guid.CreateVersion7();
    _ = await _beatAsync(peer, "Running", "1.0.0", 150, cancellationToken);
    await _ageAsync(peer, TimeSpan.FromMinutes(10), cancellationToken);

    _ = await _beatAsync(beater, "Running", "1.0.0", 150, cancellationToken);

    var row = await _rowAsync(peer, cancellationToken);
    await Assert.That(row).IsNull()
      .Because("ten minutes silent with no session lock is dead under any threshold");
    var rejoin = await _beatAsync(peer, "Running", "1.0.0", 150, cancellationToken);
    await Assert.That(rejoin).IsFalse()
      .Because("a reaped instance is tombstoned and must not rejoin silently (migration 106 fence)");
  }

  [Test]
  [Timeout(60000)]
  public async Task Coordinator_PassesPhaseVersionAndThresholdThroughAsync(CancellationToken cancellationToken) {
    // The EF driver's RecordHeartbeatAsync is the production caller of the new shape.
    var id = Guid.CreateVersion7();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    var accepted = await coordinator.RecordHeartbeatAsync(
      new HeartbeatRequest(id, "coord-svc", "coord-host", 7, LifecyclePhase: "Running", LibraryVersion: "4.5.6", StaleThresholdSeconds: 150),
      cancellationToken);

    await Assert.That(accepted).IsTrue();
    var row = await _rowAsync(id, cancellationToken);
    await Assert.That(row!.Value.Phase).IsEqualTo("Running");
    await Assert.That(row.Value.Version).IsEqualTo("4.5.6");
  }

  [Test]
  [Timeout(60000)]
  public async Task Coordinator_LegacyRequest_StillBeatsAsync(CancellationToken cancellationToken) {
    var id = Guid.CreateVersion7();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    var accepted = await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(id, "coord-svc", "coord-host", 7), cancellationToken);

    await Assert.That(accepted).IsTrue();
    await Assert.That(await _rowAsync(id, cancellationToken)).IsNotNull();
  }
}
