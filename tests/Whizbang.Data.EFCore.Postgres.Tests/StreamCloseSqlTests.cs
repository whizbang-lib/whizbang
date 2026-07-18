using System;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for migration 084's <c>close_stream</c> — A1 (Archival &amp; Compaction) increment 1, the
/// gated-truncate primitive behind <c>IWorkCoordinator.CloseStreamAsync</c> ("closing the books"). A close
/// truncates a durable Sourced stream's detail at/below a version ONLY when (1) every perspective has processed
/// every event at/below that point (the consumption gate) AND (2) a carry-forward event survives above it (the
/// domain's closing event / new origin). Discard-only in this increment; skipped under debug_mode. Verified
/// against a real Postgres so the migration SQL (check_function_bodies=on) runs end-to-end.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class StreamCloseSqlTests : EFCoreTestBase {
  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _seedPerspectiveAssociationAsync(NpgsqlConnection connection, string eventType, string perspectiveName) {
    await using var assoc = connection.CreateCommand();
    assoc.CommandText = @"
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name,
         normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test-service', @t, NOW(), NOW())
      ON CONFLICT DO NOTHING";
    assoc.Parameters.AddWithValue("t", eventType);
    assoc.Parameters.AddWithValue("p", perspectiveName);
    await assoc.ExecuteNonQueryAsync();
  }

  // Commits a single Sourced event to a stream via the emit chain; separate calls to the same stream get
  // sequential versions (MAX(version)+1), so the caller controls the version ordering deterministically.
  private static async Task _commitAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType) {
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{eventType}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 0
        }]
      }
      """;
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", request);
    _ = await call.ExecuteScalarAsync();
  }

  private static async Task _processWorkItemsAsync(NpgsqlConnection connection, Guid streamId) {
    await using var c = connection.CreateCommand();
    c.CommandText = "UPDATE wh_perspective_events SET processed_at = NOW() WHERE stream_id = @sid";
    c.Parameters.AddWithValue("sid", streamId);
    await c.ExecuteNonQueryAsync();
  }

  private static async Task<long> _streamEventCountAsync(NpgsqlConnection connection, Guid streamId) {
    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT count(*) FROM wh_event_store WHERE stream_id = @sid";
    v.Parameters.AddWithValue("sid", streamId);
    return (long)(await v.ExecuteScalarAsync())!;
  }

  private static async Task<long> _eventCountAtOrBelowAsync(NpgsqlConnection connection, Guid streamId, long version) {
    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT count(*) FROM wh_event_store WHERE stream_id = @sid AND version <= @ver";
    v.Parameters.AddWithValue("sid", streamId);
    v.Parameters.AddWithValue("ver", version);
    return (long)(await v.ExecuteScalarAsync())!;
  }

  private static async Task<long> _bodyCountAsync(NpgsqlConnection connection, Guid eventId) {
    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT count(*) FROM wh_event_body WHERE event_id = @id";
    v.Parameters.AddWithValue("id", eventId);
    return (long)(await v.ExecuteScalarAsync())!;
  }

  [Test]
  public async Task CloseStream_ConsumptionGate_BlocksThenTruncatesAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    const string detailType = "Whizbang.Tests.LedgerEntry";
    await _seedPerspectiveAssociationAsync(connection, detailType, "LedgerBalance");

    // Three detail events (versions 1-3), each with an UNPROCESSED perspective work item, then the domain's
    // closing event (version 4, distinct type / no consumer).
    await _commitAsync(connection, Guid.NewGuid(), streamId, detailType);
    await _commitAsync(connection, Guid.NewGuid(), streamId, detailType);
    await _commitAsync(connection, Guid.NewGuid(), streamId, detailType);
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.MonthClosed");

    // Consumption gate: a perspective has NOT processed versions 1-3 => the close is refused, nothing truncated.
    var blocked = await coordinator.CloseStreamAsync(streamId, throughVersion: 3);
    await Assert.That(blocked.Status).IsEqualTo("blocked")
      .Because("A close must be refused while any perspective has an unprocessed event at/below the close point.");
    await Assert.That(blocked.EventsTruncated).IsEqualTo(0L);
    await Assert.That(await _streamEventCountAsync(connection, streamId)).IsEqualTo(4L)
      .Because("A blocked close truncates nothing.");

    // Every perspective processes the detail => the gate opens.
    await _processWorkItemsAsync(connection, streamId);

    var closed = await coordinator.CloseStreamAsync(streamId, throughVersion: 3);
    await Assert.That(closed.Status).IsEqualTo("closed")
      .Because("Once every perspective has consumed past the close point, the close proceeds.");
    await Assert.That(closed.EventsTruncated).IsEqualTo(3L)
      .Because("The three detail events at/below the close point are truncated.");
    await Assert.That(await _eventCountAtOrBelowAsync(connection, streamId, 3)).IsEqualTo(0L)
      .Because("Detail at/below the close point is gone.");
    await Assert.That(await _streamEventCountAsync(connection, streamId)).IsEqualTo(1L)
      .Because("The carry-forward closing event (version 4) survives as the new origin.");
  }

  [Test]
  public async Task CloseStream_NoCarryForward_RefusesTotalTruncationAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    // Three events, no consuming perspective (consumed vacuously), and NO event above the close point.
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");

    var result = await coordinator.CloseStreamAsync(streamId, throughVersion: 3);
    await Assert.That(result.Status).IsEqualTo("no_carry_forward")
      .Because("Closing the books must leave an opening balance — a total truncation with no surviving origin is refused.");
    await Assert.That(await _streamEventCountAsync(connection, streamId)).IsEqualTo(3L)
      .Because("A refused close truncates nothing.");
  }

  [Test]
  public async Task CloseStream_DebugMode_SkippedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.MonthClosed");

    await using (var dbg = connection.CreateCommand()) {
      dbg.CommandText = "UPDATE wh_settings SET setting_value = 'true' WHERE setting_key = 'debug_mode'";
      await dbg.ExecuteNonQueryAsync();
    }

    var result = await coordinator.CloseStreamAsync(streamId, throughVersion: 2);
    await Assert.That(result.Status).IsEqualTo("debug_skipped")
      .Because("Under debug_mode the close is skipped so forensic history is retained, like the reaper.");
    await Assert.That(await _streamEventCountAsync(connection, streamId)).IsEqualTo(3L)
      .Because("A debug-skipped close truncates nothing.");
  }

  [Test]
  public async Task CloseStream_TruncatesDetailBodies_KeepsClosingEventBodyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    var detail1 = Guid.NewGuid();
    var detail2 = Guid.NewGuid();
    var closing = Guid.NewGuid();
    // No association => consumed vacuously; two detail events (versions 1-2) + a closing event (version 3).
    await _commitAsync(connection, detail1, streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, detail2, streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, closing, streamId, "Whizbang.Tests.MonthClosed");

    // All three bodies live in wh_event_body (full split, 077).
    await Assert.That(await _bodyCountAsync(connection, detail1)).IsEqualTo(1L);
    await Assert.That(await _bodyCountAsync(connection, closing)).IsEqualTo(1L);

    var closed = await coordinator.CloseStreamAsync(streamId, throughVersion: 2);
    await Assert.That(closed.Status).IsEqualTo("closed");
    await Assert.That(closed.EventsTruncated).IsEqualTo(2L);

    // The detail bodies AND pointers are gone; the closing event's body + pointer survive.
    await Assert.That(await _bodyCountAsync(connection, detail1)).IsEqualTo(0L)
      .Because("A truncated event's body is deleted from wh_event_body.");
    await Assert.That(await _bodyCountAsync(connection, detail2)).IsEqualTo(0L);
    await Assert.That(await _bodyCountAsync(connection, closing)).IsEqualTo(1L)
      .Because("The carry-forward closing event's body survives — it is the new origin.");
    await Assert.That(await _streamEventCountAsync(connection, streamId)).IsEqualTo(1L);
  }
}
