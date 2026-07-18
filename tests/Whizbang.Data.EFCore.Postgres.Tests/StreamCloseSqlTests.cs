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

  private static async Task<long> _archiveCountAsync(NpgsqlConnection connection, Guid streamId) {
    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT count(*) FROM wh_event_archive WHERE stream_id = @sid";
    v.Parameters.AddWithValue("sid", streamId);
    return (long)(await v.ExecuteScalarAsync())!;
  }

  [Test]
  public async Task CloseStream_Archive_MovesDetailToColdStorage_RetrievableAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    var detail1 = Guid.NewGuid();
    var detail2 = Guid.NewGuid();
    // No association => consumed vacuously; two detail events (v1-2) + a closing event (v3).
    await _commitAsync(connection, detail1, streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, detail2, streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.MonthClosed");

    var closed = await coordinator.CloseStreamAsync(streamId, throughVersion: 2, archive: true);
    await Assert.That(closed.Status).IsEqualTo("closed");
    await Assert.That(closed.EventsTruncated).IsEqualTo(2L);

    // Detail is gone from the HOT store …
    await Assert.That(await _eventCountAtOrBelowAsync(connection, streamId, 2)).IsEqualTo(0L)
      .Because("An archiving close still truncates the hot store.");
    await Assert.That(await _streamEventCountAsync(connection, streamId)).IsEqualTo(1L);

    // … but preserved in COLD storage, retrievable in version order with its body intact.
    await Assert.That(await _archiveCountAsync(connection, streamId)).IsEqualTo(2L);
    var archived = await coordinator.GetArchivedEventsAsync(streamId);
    await Assert.That(archived.Count).IsEqualTo(2)
      .Because("Both truncated detail events are retrievable from the archive.");
    await Assert.That(archived[0].Version).IsEqualTo(1L);
    await Assert.That(archived[1].Version).IsEqualTo(2L)
      .Because("Archived events are returned ordered by version.");
    await Assert.That(archived[0].EventId).IsEqualTo(detail1);
    await Assert.That(archived[0].EventDataJson).IsNotNull()
      .Because("The event body travels into cold storage — full audit / replay stays possible.");
    await Assert.That(archived[0].EventDataJson!.Contains("OrderId", StringComparison.Ordinal)).IsTrue()
      .Because("The archived body is the real payload, not a placeholder.");
  }

  [Test]
  public async Task CloseStream_Discard_LeavesArchiveEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.MonthClosed");

    var closed = await coordinator.CloseStreamAsync(streamId, throughVersion: 2, archive: false);
    await Assert.That(closed.Status).IsEqualTo("closed");
    await Assert.That(await _archiveCountAsync(connection, streamId)).IsEqualTo(0L)
      .Because("A discard close (archive: false) truncates the detail without preserving it — leanest, audit lost.");
    await Assert.That((await coordinator.GetArchivedEventsAsync(streamId)).Count).IsEqualTo(0);
  }

  [Test]
  public async Task CloseStream_Archive_NoEventLostAcrossBoundaryAsync() {
    // The archive INSERT and the truncate run in one transaction (the function body). Whatever the outcome,
    // no event is lost: archived-detail ∪ surviving-hot must equal the original full set.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    for (var i = 0; i < 4; i++) {
      await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");   // v1-4
    }
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.MonthClosed");       // v5 (origin)

    var closed = await coordinator.CloseStreamAsync(streamId, throughVersion: 4, archive: true);
    await Assert.That(closed.Status).IsEqualTo("closed");

    var archived = await _archiveCountAsync(connection, streamId);     // 4 detail
    var hot = await _streamEventCountAsync(connection, streamId);       // 1 origin
    await Assert.That(archived + hot).IsEqualTo(5L)
      .Because("Every event survives the archive+truncate boundary — 4 in cold, 1 in hot, none lost.");
  }

  [Test]
  public async Task GetConsumingPerspectiveNames_ReturnsDistinctAssociatedPerspectivesInRangeAsync() {
    // A1-6b: the close guard asks which perspectives consume the truncated range. The query joins the stream's
    // event types (≤ through) to their perspective associations and returns DISTINCT target_names.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    const string detailType = "Whizbang.Tests.LedgerEntry";
    await _seedPerspectiveAssociationAsync(connection, detailType, "TestNamespace.LedgerListPerspective");
    // Two detail events (v1-2) of the associated type + a closing event (v3) with no perspective.
    await _commitAsync(connection, Guid.NewGuid(), streamId, detailType);
    await _commitAsync(connection, Guid.NewGuid(), streamId, detailType);
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.MonthClosed");

    var names = await coordinator.GetConsumingPerspectiveNamesAsync(streamId, throughVersion: 2);
    await Assert.That(names.Count).IsEqualTo(1)
      .Because("Two events of the same associated type collapse to one DISTINCT consuming perspective name.");
    await Assert.That(names[0]).IsEqualTo("TestNamespace.LedgerListPerspective");

    // A stream with no associated perspective in range yields nothing.
    var other = Guid.NewGuid();
    await _commitAsync(connection, Guid.NewGuid(), other, "Whizbang.Tests.Unassociated");
    await Assert.That((await coordinator.GetConsumingPerspectiveNamesAsync(other, 10)).Count).IsEqualTo(0)
      .Because("No perspective association for the stream's event types => no consumers.");
  }

  private static async Task<long> _eventExistsAsync(NpgsqlConnection connection, Guid eventId) {
    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT count(*) FROM wh_event_store WHERE event_id = @id";
    v.Parameters.AddWithValue("id", eventId);
    return (long)(await v.ExecuteScalarAsync())!;
  }

  [Test]
  public async Task CloseStream_SuccessiveCloses_CoalesceToOneOriginAsync() {
    // "Closing the books" must never accumulate stale origins: a later close truncates through a point that
    // includes a PRIOR closing event, so a stream holds at most one closing/origin event at its head. This
    // coalescing falls out of the increment-1 truncate mechanics; lock it so a refactor can't regress it.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    var janClose = Guid.NewGuid();
    var febClose = Guid.NewGuid();

    // Month 1: 3 detail (v1-3) + Jan close (v4). Close through 3 => Jan close is the lone origin.
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, janClose, streamId, "Whizbang.Tests.MonthClosed");
    await Assert.That((await coordinator.CloseStreamAsync(streamId, throughVersion: 3)).Status).IsEqualTo("closed");
    await Assert.That(await _eventExistsAsync(connection, janClose)).IsEqualTo(1L);

    // Month 2: 2 more detail (v5-6) + Feb close (v7). Close through 6 => truncates the Jan close (v4) too.
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, febClose, streamId, "Whizbang.Tests.MonthClosed");

    var second = await coordinator.CloseStreamAsync(streamId, throughVersion: 6);
    await Assert.That(second.Status).IsEqualTo("closed");
    await Assert.That(await _eventExistsAsync(connection, janClose)).IsEqualTo(0L)
      .Because("The prior (Jan) closing event is truncated by the later close — origins coalesce, they don't accumulate.");
    await Assert.That(await _eventExistsAsync(connection, febClose)).IsEqualTo(1L);
    await Assert.That(await _streamEventCountAsync(connection, streamId)).IsEqualTo(1L)
      .Because("A twice-closed stream holds exactly one origin at its head.");
  }

  [Test]
  public async Task CloseStream_IdempotentReClose_IsSafeNoOpAsync() {
    // Re-closing through the same point after the detail is already gone must be a safe no-op (closed, 0),
    // not an error and not a spurious no_carry_forward — closing the books twice is harmless.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.MonthClosed");

    var first = await coordinator.CloseStreamAsync(streamId, throughVersion: 2);
    await Assert.That(first.Status).IsEqualTo("closed");
    await Assert.That(first.EventsTruncated).IsEqualTo(2L);

    var again = await coordinator.CloseStreamAsync(streamId, throughVersion: 2);
    await Assert.That(again.Status).IsEqualTo("closed")
      .Because("Re-closing through an already-truncated point is idempotent — the carry-forward still survives.");
    await Assert.That(again.EventsTruncated).IsEqualTo(0L)
      .Because("Nothing remains at/below the point, so a re-close truncates nothing.");
    await Assert.That(await _streamEventCountAsync(connection, streamId)).IsEqualTo(1L);
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
