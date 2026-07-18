using System;
using System.Data;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the E2-3 destruction hold (migration 079). A PreDestruction hook's Cancel / Defer
/// records a hold via <c>HoldEphemeralDestructionAsync</c>; Task 8's reaper (and the about-to-reap query)
/// then skip any body with an active hold, so the hook's decision is honoured. Once a hold lapses the body
/// is reapable again, and its hold row is cleaned up. Verified against a real Postgres.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class EphemeralDestructionHoldSqlTests : EFCoreTestBase {
  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _execAsync(NpgsqlConnection connection, string sql, params (string, object)[] ps) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (n, v) in ps) {
      cmd.Parameters.AddWithValue(n, v);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarLongAsync(NpgsqlConnection connection, string sql, params (string, object)[] ps) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (n, v) in ps) {
      cmd.Parameters.AddWithValue(n, v);
    }
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _commitEphemeralAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType) {
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{eventType}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 8
        }]
      }
      """;
    await _execAsync(connection, "SELECT commit_handler_result(@req::jsonb)", ("req", request));
  }

  private static Task _agePastGraceAsync(NpgsqlConnection connection, Guid eventId) =>
    _execAsync(connection, "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @id", ("id", eventId));

  private static async Task _runMaintenanceAsync(NpgsqlConnection connection) {
    await using var m = connection.CreateCommand();
    m.CommandText = "SELECT * FROM perform_maintenance()";
    await using var r = await m.ExecuteReaderAsync();
    while (await r.ReadAsync()) { }
  }

  private static Task<long> _bodyCountAsync(NpgsqlConnection c, Guid id) =>
    _scalarLongAsync(c, "SELECT count(*) FROM wh_event_body WHERE event_id = @id", ("id", id));

  [Test]
  public async Task Hold_KeepsBodyFromReap_ThenReapableWhenHoldLapses_AndHoldCleanedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var eventId = Guid.NewGuid();
    await _commitEphemeralAsync(connection, eventId, Guid.NewGuid(), "Whizbang.Tests.HeldEvent");
    await _agePastGraceAsync(connection, eventId);

    // The hook decided Cancel: hold far-future. The reaper must skip it.
    await coordinator.HoldEphemeralDestructionAsync([eventId], DateTimeOffset.MaxValue);
    await _runMaintenanceAsync(connection);
    await Assert.That(await _bodyCountAsync(connection, eventId)).IsEqualTo(1L)
      .Because("An actively-held ephemeral body is not reaped — the hook's Cancel/Defer is honoured.");

    // The hold lapses (as a Defer would). Now the body is reapable again.
    await _execAsync(connection, "UPDATE wh_event_destruction_hold SET hold_until = NOW() - INTERVAL '1 minute' WHERE event_id = @id", ("id", eventId));
    await _runMaintenanceAsync(connection);
    await Assert.That(await _bodyCountAsync(connection, eventId)).IsEqualTo(0L)
      .Because("Once the hold lapses, the consumed, aged body is reaped as usual.");
    await Assert.That(await _scalarLongAsync(connection, "SELECT count(*) FROM wh_event_destruction_hold WHERE event_id = @id", ("id", eventId))).IsEqualTo(0L)
      .Because("The reaper cleans up the hold row once its body is gone, keeping the table bounded.");
  }

  [Test]
  public async Task RecordDestructionFailure_HoldsForRetry_ThenForcedDeletePastCapAsync() {
    // E2-5: a throwing PreDestruction hook records a failure — the batch is held for a backoff (retried next
    // cycle) up to the cap, then force-deleted (the reaper deletes it despite the failing hook).
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var eventId = Guid.NewGuid();
    // No association => consumed + vacuously snapshot-covered; only the destruction hold gates it.
    await _commitEphemeralAsync(connection, eventId, Guid.NewGuid(), "Whizbang.Tests.FailingEvent");
    await _agePastGraceAsync(connection, eventId);
    var future = DateTimeOffset.UtcNow.AddHours(1);

    // Attempt 1 (cap 2): held for retry — a future hold_until, so the reap skips it.
    var a1 = await coordinator.RecordDestructionFailureAsync([eventId], future, maxRetries: 2);
    await Assert.That(a1).IsEqualTo(1).Because("The first failure is attempt 1.");
    await _runMaintenanceAsync(connection);
    await Assert.That(await _bodyCountAsync(connection, eventId)).IsEqualTo(1L)
      .Because("A failed batch under the retry cap is held, so the reaper skips it (it retries next cycle).");

    // Attempt 2: still under the cap — still held.
    var a2 = await coordinator.RecordDestructionFailureAsync([eventId], future, maxRetries: 2);
    await Assert.That(a2).IsEqualTo(2).Because("The attempt count increments per failure.");
    await _runMaintenanceAsync(connection);
    await Assert.That(await _bodyCountAsync(connection, eventId)).IsEqualTo(1L);

    // Attempt 3 (> cap 2): the hold is set to '-infinity', so the reaper FORCE-deletes the batch.
    var a3 = await coordinator.RecordDestructionFailureAsync([eventId], future, maxRetries: 2);
    await Assert.That(a3).IsEqualTo(3);
    await _runMaintenanceAsync(connection);
    await Assert.That(await _bodyCountAsync(connection, eventId)).IsEqualTo(0L)
      .Because("Past the retry cap the hold lapses (hold_until '-infinity'), so the reaper force-deletes the batch — a broken hook can never leak storage.");
  }

  [Test]
  public async Task GetEphemeralBodiesAboutToReap_ExcludesHeld_IncludesAfterHoldRemovedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var eventId = Guid.NewGuid();
    await _commitEphemeralAsync(connection, eventId, Guid.NewGuid(), "Whizbang.Tests.HeldTarget");
    await _agePastGraceAsync(connection, eventId);

    await coordinator.HoldEphemeralDestructionAsync([eventId], DateTimeOffset.MaxValue);
    var whileHeld = await coordinator.GetEphemeralBodiesAboutToReapAsync();
    await Assert.That(whileHeld.Any(t => t.EventId == eventId)).IsFalse()
      .Because("A held body is not re-offered to the hook — the hook already decided its fate.");

    await _execAsync(connection, "DELETE FROM wh_event_destruction_hold WHERE event_id = @id", ("id", eventId));
    var afterRemoval = await coordinator.GetEphemeralBodiesAboutToReapAsync();
    await Assert.That(afterRemoval.Any(t => t.EventId == eventId)).IsTrue()
      .Because("With the hold gone, the consumed, aged body is a destruction target again.");
  }
}
