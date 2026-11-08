// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.RunControl;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The beat of a starting instance through the Dapper driver (#2126), as through the EF Core driver: from registration
/// until its schema is ready, the heartbeat refreshes the instance's row through <c>wh_record_startup_heartbeat</c>,
/// which keeps it live by the one liveness rule, records its connection mode and window, and never inserts a row nor
/// writes the stage or the phase.
/// </summary>
/// <remarks>Ages are on the database's clock (<c>NOW() - age</c>), never the host's.</remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/362_TheHeartbeatStartsAtRegistration.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
public class DapperStartupHeartbeatTests : PostgresTestBase {
  [Test]
  public async Task AStartupBeat_KeepsAStartingInstanceLive_PastThirtySeconds_AndAPeersReapLeavesItAsync() {
    var coordinator = _build();
    var starting = (Guid)TrackedGuid.New();
    var peer = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _insertMigratingAsync(conn, starting, TimeSpan.FromSeconds(200));

    var accepted = await coordinator.RecordStartupHeartbeatAsync(_beat(starting));
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(peer, "peer-svc", "peer-host", 2, StaleThresholdSeconds: 150));

    await Assert.That(accepted).IsTrue();
    await Assert.That(await conn.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT 1 FROM wh_service_instances WHERE instance_id = @starting)", new { starting })).IsTrue()
      .Because("the startup beat refreshed the row, so the peer's reap finds it live");
    await Assert.That(await conn.ExecuteScalarAsync<bool>(
      "SELECT EXISTS (SELECT 1 FROM wh_instance_evictions WHERE instance_id = @starting)", new { starting })).IsFalse();
  }

  [Test]
  public async Task AStartupBeat_NeverMovesTheStageOrThePhase_AndRecordsTheModeAndTheWindowAsync() {
    var coordinator = _build();
    var starting = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _insertMigratingAsync(conn, starting, TimeSpan.Zero);

    _ = await coordinator.RecordStartupHeartbeatAsync(_beat(starting));

    await Assert.That(await conn.ExecuteScalarAsync<string>(
        """
        SELECT startup_stage || ' ' || lifecycle_phase || ' ' || connection_mode || ' ' || liveness_window_seconds
        FROM wh_service_instances WHERE instance_id = @starting
        """, new { starting }))
      .IsEqualTo("30 Migrating direct 150")
      .Because("the beat announced Running; only the stage record writes the stage and the phase");
  }

  [Test]
  public async Task AStartupBeat_OfAnEvictedInstance_IsRefusedAsync() {
    var coordinator = _build();
    var evicted = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await conn.ExecuteAsync(
      "INSERT INTO wh_instance_evictions (instance_id, evicted_at, reason) VALUES (@evicted, NOW(), 'test')", new { evicted });

    await Assert.That(await coordinator.RecordStartupHeartbeatAsync(_beat(evicted))).IsFalse()
      .Because("an evicted instance learns it from a startup beat as from any beat");
  }

  [Test]
  public async Task AStartupBeat_OfAnInstanceWithNoRow_InsertsNothingAsync() {
    var coordinator = _build();
    var unregistered = (Guid)TrackedGuid.New();

    var accepted = await coordinator.RecordStartupHeartbeatAsync(_beat(unregistered));

    await using var conn = await _openAsync();
    await Assert.That(accepted).IsTrue();
    await Assert.That(await conn.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_service_instances WHERE instance_id = @unregistered", new { unregistered })).IsEqualTo(0L)
      .Because("this driver registers at the end of its pass; a row without a phase would read as eligible for work");
  }

  /// <summary>
  /// This driver applies no bootstrap closure: on a database its pass has not reached yet (fresh, or an earlier release),
  /// the startup beat's function does not exist. There is no row of this instance to keep then, so the beat is accepted
  /// and writes nothing, rather than failing on every tick of the migration.
  /// </summary>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AStartupBeat_BeforeThePassCreatedItsFunction_IsAcceptedAndWritesNothingAsync(bool withALogger) {
    var coordinator = _build(withALogger);
    var starting = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await conn.ExecuteAsync("DROP FUNCTION IF EXISTS wh_record_startup_heartbeat(uuid, text, integer)");

    var accepted = await coordinator.RecordStartupHeartbeatAsync(_beat(starting));

    await Assert.That(accepted).IsTrue();
    await Assert.That(await conn.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_service_instances WHERE instance_id = @starting", new { starting })).IsEqualTo(0L);
  }

  /// <summary>Any other failure of the beat is the heartbeat's to log and retry, so it surfaces.</summary>
  [Test]
  public async Task AStartupBeat_ThatFailsForAnyOtherReason_ThrowsAsync() {
    var coordinator = _build();
    await using var conn = await _openAsync();
    await conn.ExecuteAsync("ALTER TABLE wh_instance_evictions RENAME TO wh_instance_evictions_moved");

    await Assert.That(async () => await coordinator.RecordStartupHeartbeatAsync(_beat((Guid)TrackedGuid.New())))
      .Throws<PostgresException>();
  }

  private static HeartbeatRequest _beat(Guid id) =>
    new(id, "starting-svc", "starting-host", 1, LifecyclePhase: nameof(LifecyclePhase.Running), StaleThresholdSeconds: 150,
      ConnectionMode: InstanceConnectionMode.Direct);

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>A starting instance's row as its registration and stage record leave it in Migrate, aged on the database clock.</summary>
  private static Task<int> _insertMigratingAsync(NpgsqlConnection conn, Guid id, TimeSpan age) =>
    conn.ExecuteAsync(
      """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, lifecycle_phase, startup_stage)
      VALUES (@id, 'starting-svc', 'starting-host', 1, NOW() - @age, NOW(), 'Migrating', 30)
      """, new { id, age });

  /// <summary>The Dapper coordinator as the heartbeat worker resolves it: through the interface.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance",
    Justification = "Calls go through the interface on purpose: that is how the heartbeat worker reaches it.")]
  private IWorkCoordinator _build(bool withALogger = true) =>
    new DapperWorkCoordinator(ConnectionString, new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default },
      withALogger ? NullLogger<DapperWorkCoordinator>.Instance : null);
}
