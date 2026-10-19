// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
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
[Category("Shard1")]
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

  // Commits an event with explicit flags (16 = EventFlags.Compacted — a permanent StateBased origin).
  private static async Task _commitFlaggedAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType, int flags) {
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{eventType}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": {{flags}}
        }]
      }
      """;
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", request);
    _ = await call.ExecuteScalarAsync();
  }

  [Test]
  public async Task CloseStream_Coalesces_PriorCompactedOrigin_IsTruncatedByLaterCloseAsync() {
    // E3-4 coalescing: the fold appends a new Compacted origin then closes through the prior fold point — which
    // includes the PRIOR Compacted (flags 16). close_stream truncates it (superseded), leaving one origin at
    // head. Truncating a permanent Compacted origin here is correct: this is the EXPLICIT re-compaction path,
    // not the automatic reaper (which never touches flags&16).
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    var priorOrigin = Guid.NewGuid();
    var newOrigin = Guid.NewGuid();
    // v1 = prior Compacted origin, v2 = later ephemeral detail, v3 = new Compacted origin.
    await _commitFlaggedAsync(connection, priorOrigin, streamId, "Whizbang.Core.Perspectives.Compacted", flags: 16);
    await _commitAsync(connection, Guid.NewGuid(), streamId, "Whizbang.Tests.PresencePing");
    await _commitFlaggedAsync(connection, newOrigin, streamId, "Whizbang.Core.Perspectives.Compacted", flags: 16);

    var result = await coordinator.CloseStreamAsync(streamId, throughVersion: 2, archive: false);
    await Assert.That(result.Status).IsEqualTo("closed");

    await Assert.That(await _eventExistsAsync(connection, priorOrigin)).IsEqualTo(0L)
      .Because("The prior Compacted origin (v1) is superseded and truncated by the re-compaction — origins coalesce.");
    await Assert.That(await _eventExistsAsync(connection, newOrigin)).IsEqualTo(1L)
      .Because("The new Compacted origin (v3) survives as the lone head.");
    await Assert.That(await _streamEventCountAsync(connection, streamId)).IsEqualTo(1L)
      .Because("A re-compacted stream holds exactly one origin at its head.");
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

  [Test]
  public async Task GetEventVersion_ReturnsPerStreamVersion_NullForUnknownAsync() {
    // E3: a Tier-2 compaction closes through the version of the snapshot's anchor event — this maps the anchor
    // event id to its per-stream version.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    await _commitAsync(connection, first, streamId, "Whizbang.Tests.LedgerEntry");   // v1
    await _commitAsync(connection, second, streamId, "Whizbang.Tests.LedgerEntry");  // v2

    await Assert.That(await coordinator.GetEventVersionAsync(first)).IsEqualTo(1L);
    await Assert.That(await coordinator.GetEventVersionAsync(second)).IsEqualTo(2L)
      .Because("Per-stream versions increment in commit order.");
    await Assert.That(await coordinator.GetEventVersionAsync(Guid.NewGuid())).IsNull()
      .Because("An unknown event id has no version.");
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
  // ── #1486: a close re-anchors the cursors and removes the snapshots the truncate strands ──────────────

  private static async Task _cursorAsync(NpgsqlConnection connection, Guid streamId, string perspectiveName, Guid? lastEventId) {
    await using var c = connection.CreateCommand();
    c.CommandText = "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) "
      + "VALUES (@sid, @p, @e, 0, NOW())";
    c.Parameters.AddWithValue("sid", streamId);
    c.Parameters.AddWithValue("p", perspectiveName);
    c.Parameters.AddWithValue("e", (object?)lastEventId ?? DBNull.Value);
    await c.ExecuteNonQueryAsync();
  }

  private static async Task<Guid?> _cursorEventAsync(NpgsqlConnection connection, Guid streamId, string perspectiveName) {
    await using var c = connection.CreateCommand();
    c.CommandText = "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @sid AND perspective_name = @p";
    c.Parameters.AddWithValue("sid", streamId);
    c.Parameters.AddWithValue("p", perspectiveName);
    var raw = await c.ExecuteScalarAsync();
    return raw is Guid g ? g : null;
  }

  private static async Task _snapshotAsync(NpgsqlConnection connection, Guid streamId, string perspectiveName, Guid eventId, int? version) {
    await using var c = connection.CreateCommand();
    c.CommandText = "INSERT INTO wh_perspective_snapshots "
      + "(stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) "
      + "VALUES (@sid, @p, @e, '{}'::jsonb, 1, @v)";
    c.Parameters.AddWithValue("sid", streamId);
    c.Parameters.AddWithValue("p", perspectiveName);
    c.Parameters.AddWithValue("e", eventId);
    c.Parameters.AddWithValue("v", (object?)version ?? DBNull.Value);
    await c.ExecuteNonQueryAsync();
  }

  // The snapshot event ids that survive for one perspective, in event-id order.
  private static async Task<List<Guid>> _snapshotEventsAsync(NpgsqlConnection connection, Guid streamId, string perspectiveName) {
    await using var c = connection.CreateCommand();
    c.CommandText = "SELECT snapshot_event_id FROM wh_perspective_snapshots WHERE stream_id = @sid AND perspective_name = @p ORDER BY snapshot_event_id";
    c.Parameters.AddWithValue("sid", streamId);
    c.Parameters.AddWithValue("p", perspectiveName);
    var ids = new List<Guid>();
    await using var reader = await c.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<long> _countAsync(NpgsqlConnection connection, string sql, Guid id) {
    await using var c = connection.CreateCommand();
    c.CommandText = sql;
    c.Parameters.AddWithValue("id", id);
    return (long)(await c.ExecuteScalarAsync())!;
  }

  private static async Task _executeAsync(NpgsqlConnection connection, string sql, Guid id) {
    await using var c = connection.CreateCommand();
    c.CommandText = sql;
    c.Parameters.AddWithValue("id", id);
    await c.ExecuteNonQueryAsync();
  }

  // Sequential v7 ids, so event-id order is stream order and the snapshot-order assertions mean something.
  private static Guid[] _orderedIds(int count) {
    var ids = new Guid[count];
    for (var i = 0; i < count; i++) {
      ids[i] = Guid.CreateVersion7();
    }
    Array.Sort(ids);
    return ids;
  }

  [Test]
  public async Task CloseStream_CursorOfAPerspectiveWithNothingLeftToConsume_IsReanchoredOntoTheBoundaryEventAsync() {
    // fk_perspective_cursors_event is ON DELETE RESTRICT. A perspective that does not subscribe to the closing
    // event keeps its cursor on the last detail event it applied, inside the truncated range, so the close used
    // to fail with a foreign-key violation. Nothing below the boundary is unread (the consumption gate proved
    // it) and the perspective owes nothing on the boundary event, so the cursor moves onto the boundary: the
    // next read (event_id > cursor) still returns every event the perspective has not seen.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    const string detailType = "Whizbang.Tests.LedgerEntry";
    const string detailOnly = "Tests.DetailOnlyPerspective";
    const string caughtUp = "Tests.CaughtUpPerspective";
    const string neverRan = "Tests.NeverRanPerspective";
    await _seedPerspectiveAssociationAsync(connection, detailType, detailOnly);

    var ids = _orderedIds(4);
    await _commitAsync(connection, ids[0], streamId, detailType);                   // v1
    await _commitAsync(connection, ids[1], streamId, detailType);                   // v2
    await _commitAsync(connection, ids[2], streamId, detailType);                   // v3
    await _commitAsync(connection, ids[3], streamId, "Whizbang.Tests.MonthClosed"); // v4, nothing subscribes
    await _processWorkItemsAsync(connection, streamId);

    await _cursorAsync(connection, streamId, detailOnly, ids[2]);   // inside the range
    await _cursorAsync(connection, streamId, caughtUp, ids[3]);     // already on the boundary
    await _cursorAsync(connection, streamId, neverRan, null);       // has applied nothing

    var closed = await coordinator.CloseStreamAsync(streamId, throughVersion: 3);

    await Assert.That(closed.Status).IsEqualTo("closed")
      .Because("a cursor inside the range is re-anchored, not a reason for the truncate to fail");
    await Assert.That(closed.EventsTruncated).IsEqualTo(3L);
    await Assert.That(await _cursorEventAsync(connection, streamId, detailOnly)).IsEqualTo(ids[3])
      .Because("the cursor moves onto the first surviving event, which the perspective does not consume");
    await Assert.That(await _cursorEventAsync(connection, streamId, caughtUp)).IsEqualTo(ids[3])
      .Because("a cursor at or past the boundary is not touched");
    await Assert.That(await _cursorEventAsync(connection, streamId, neverRan)).IsNull()
      .Because("a cursor that has applied nothing points at nothing the truncate removes");
  }

  [Test]
  public async Task CloseStream_CursorBehindABoundaryEventItsPerspectiveStillOwes_IsRefusedAsync() {
    // The cursor is the read position: the next apply reads events with an id above it. Moving it onto a
    // boundary event the perspective has not applied yet would skip that event forever, so the close is
    // refused until the perspective consumes it, the same way the consumption gate refuses unread detail.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    const string detailType = "Whizbang.Tests.LedgerEntry";
    const string closingType = "Whizbang.Tests.MonthClosed";
    const string ledger = "Tests.LedgerPerspective";
    await _seedPerspectiveAssociationAsync(connection, detailType, ledger);
    await _seedPerspectiveAssociationAsync(connection, closingType, ledger);

    var ids = _orderedIds(4);
    await _commitAsync(connection, ids[0], streamId, detailType);
    await _commitAsync(connection, ids[1], streamId, detailType);
    await _commitAsync(connection, ids[2], streamId, detailType);
    await _processWorkItemsAsync(connection, streamId);
    await _cursorAsync(connection, streamId, ledger, ids[2]);
    await _commitAsync(connection, ids[3], streamId, closingType);  // v4: work the perspective still owes

    var refused = await coordinator.CloseStreamAsync(streamId, throughVersion: 3);

    await Assert.That(refused.Status).IsEqualTo("cursor_behind")
      .Because("re-anchoring onto an event the perspective has not applied would skip it");
    await Assert.That(refused.EventsTruncated).IsEqualTo(0L);
    await Assert.That(await _streamEventCountAsync(connection, streamId)).IsEqualTo(4L)
      .Because("a refused close truncates nothing");
    await Assert.That(await _cursorEventAsync(connection, streamId, ledger)).IsEqualTo(ids[2])
      .Because("a refused close moves no cursor");

    // Once the boundary event's work is done, nothing is owed and the close goes through.
    await _processWorkItemsAsync(connection, streamId);
    var closed = await coordinator.CloseStreamAsync(streamId, throughVersion: 3);
    await Assert.That(closed.Status).IsEqualTo("closed");
    await Assert.That(await _cursorEventAsync(connection, streamId, ledger)).IsEqualTo(ids[3]);
  }

  [Test]
  public async Task CloseStream_RemovesEverySnapshotThatNeedsATruncatedEvent_KeepsTheExactFloorsAsync() {
    // A rewind restores a snapshot and replays the events after it. After a close, a snapshot replays
    // correctly only if the perspective applied nothing between it and the close point: everything there is
    // gone. Two snapshots qualify: one at the close point itself, and one at the event a perspective's cursor
    // still sits on inside the range (that perspective applied nothing after it). Every other snapshot at or
    // below the close point is removed, and a versionless one on a truncated event (which a rewind can never
    // place again) goes with them.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    const string parked = "Tests.ParkedPerspective";     // cursor inside the range, at v2
    const string current = "Tests.CurrentPerspective";   // no cursor in range

    var ids = _orderedIds(5);
    await _commitAsync(connection, ids[0], streamId, "Whizbang.Tests.LedgerEntry");  // v1
    await _commitAsync(connection, ids[1], streamId, "Whizbang.Tests.LedgerEntry");  // v2
    await _commitAsync(connection, ids[2], streamId, "Whizbang.Tests.LedgerEntry");  // v3 (close point)
    await _commitAsync(connection, ids[3], streamId, "Whizbang.Tests.MonthClosed");  // v4
    await _commitAsync(connection, ids[4], streamId, "Whizbang.Tests.LedgerEntry");  // v5

    await _cursorAsync(connection, streamId, parked, ids[1]);
    await _snapshotAsync(connection, streamId, parked, ids[0], version: 1);   // needs v2: removed
    await _snapshotAsync(connection, streamId, parked, ids[1], version: 2);   // the parked state: kept

    await _snapshotAsync(connection, streamId, current, ids[0], version: 1);  // needs v2..v3: removed
    await _snapshotAsync(connection, streamId, current, ids[1], version: null); // unplaceable, truncated: removed
    await _snapshotAsync(connection, streamId, current, ids[2], version: 3);  // at the close point: kept
    await _snapshotAsync(connection, streamId, current, ids[3], version: 4);  // above the range: kept
    await _snapshotAsync(connection, streamId, current, ids[4], version: null); // unplaceable, not truncated: kept

    var closed = await coordinator.CloseStreamAsync(streamId, throughVersion: 3);
    await Assert.That(closed.Status).IsEqualTo("closed");

    var parkedLeft = await _snapshotEventsAsync(connection, streamId, parked);
    await Assert.That(parkedLeft.Count).IsEqualTo(1);
    await Assert.That(parkedLeft[0]).IsEqualTo(ids[1])
      .Because("the snapshot at the parked cursor is the perspective's current state; replaying from it needs no truncated event");

    var currentLeft = await _snapshotEventsAsync(connection, streamId, current);
    await Assert.That(currentLeft.Count).IsEqualTo(3)
      .Because("only the snapshots at and above the close point survive for a perspective with no cursor in the range");
    await Assert.That(currentLeft[0]).IsEqualTo(ids[2]);
    await Assert.That(currentLeft[1]).IsEqualTo(ids[3]);
    await Assert.That(currentLeft[2]).IsEqualTo(ids[4])
      .Because("a versionless snapshot on a surviving event is not the close's to judge");

    // What a later rewind selects: never a snapshot whose replay needs a truncated event.
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    Whizbang.Core.Perspectives.IPerspectiveSnapshotStore store = new EFCorePerspectiveSnapshotStore(dataSource);
    var parkedFloor = await store.GetLatestSnapshotBeforeEventInStreamOrderAsync(streamId, parked, ids[4], CancellationToken.None);
    await Assert.That(parkedFloor.HasValue).IsTrue();
    await Assert.That(parkedFloor!.Value.SnapshotEventId).IsEqualTo(ids[1]);
    var currentFloor = await store.GetLatestSnapshotBeforeEventInStreamOrderAsync(streamId, current, ids[3], CancellationToken.None);
    await Assert.That(currentFloor.HasValue).IsTrue();
    await Assert.That(currentFloor!.Value.SnapshotEventId).IsEqualTo(ids[2])
      .Because("a rewind to the boundary event restores the close-point snapshot, not the removed v1 one");
  }

  [Test]
  public async Task CloseStream_ActiveDestructionHoldInsideTheRange_IsRefused_ALapsedOneIsRemovedAsync() {
    // A destruction hold records a PreDestruction hook's decision to keep an event's data (Cancel) or keep it
    // until a time (Defer). A close would destroy that event, so an active hold refuses it. A lapsed hold is
    // only a record about an event that is about to go, and goes with it.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    var ids = _orderedIds(3);
    await _commitAsync(connection, ids[0], streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, ids[1], streamId, "Whizbang.Tests.LedgerEntry");
    await _commitAsync(connection, ids[2], streamId, "Whizbang.Tests.MonthClosed");
    await _executeAsync(connection,
      "INSERT INTO wh_event_destruction_hold (event_id, hold_until) VALUES (@id, NOW() + INTERVAL '1 day')", ids[0]);

    var refused = await coordinator.CloseStreamAsync(streamId, throughVersion: 2);
    await Assert.That(refused.Status).IsEqualTo("held")
      .Because("an active hold is a recorded decision to keep that event");
    await Assert.That(refused.EventsTruncated).IsEqualTo(0L);
    await Assert.That(await _streamEventCountAsync(connection, streamId)).IsEqualTo(3L);

    // On the database clock, so the hold has lapsed by construction rather than by waiting.
    await _executeAsync(connection,
      "UPDATE wh_event_destruction_hold SET hold_until = NOW() - INTERVAL '1 second' WHERE event_id = @id", ids[0]);

    var closed = await coordinator.CloseStreamAsync(streamId, throughVersion: 2);
    await Assert.That(closed.Status).IsEqualTo("closed");
    await Assert.That(await _countAsync(connection,
        "SELECT count(*) FROM wh_event_destruction_hold WHERE event_id = @id", ids[0])).IsEqualTo(0L)
      .Because("a lapsed hold on a truncated event is removed with the event");
  }

  [Test]
  public async Task CloseStream_RemovesProcessedWorkRowsOfTruncatedEvents_KeepsTheirDeduplicationEntriesAsync() {
    // Processed perspective work rows for the truncated events describe work that is finished on events that
    // no longer exist; they go. Deduplication entries are kept: they are what turns a redelivered copy of a
    // truncated event away instead of storing it again below the close point, and they age out under their
    // own retention.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamId = Guid.NewGuid();
    const string detailType = "Whizbang.Tests.LedgerEntry";
    await _seedPerspectiveAssociationAsync(connection, detailType, "Tests.WorkRowPerspective");

    var ids = _orderedIds(3);
    await _commitAsync(connection, ids[0], streamId, detailType);
    await _commitAsync(connection, ids[1], streamId, detailType);
    await _commitAsync(connection, ids[2], streamId, "Whizbang.Tests.MonthClosed");
    await _processWorkItemsAsync(connection, streamId);
    await _executeAsync(connection,
      "INSERT INTO wh_message_deduplication (message_id) VALUES (@id) ON CONFLICT DO NOTHING", ids[0]);
    await Assert.That(await _countAsync(connection,
      "SELECT count(*) FROM wh_perspective_events WHERE stream_id = @id", streamId)).IsEqualTo(2L)
      .Because("precondition: both detail events left a processed work row");

    var closed = await coordinator.CloseStreamAsync(streamId, throughVersion: 2);
    await Assert.That(closed.Status).IsEqualTo("closed");

    await Assert.That(await _countAsync(connection,
      "SELECT count(*) FROM wh_perspective_events WHERE stream_id = @id", streamId)).IsEqualTo(0L)
      .Because("processed work rows of truncated events are removed with the events");
    await Assert.That(await _countAsync(connection,
      "SELECT count(*) FROM wh_message_deduplication WHERE message_id = @id", ids[0])).IsEqualTo(1L)
      .Because("the deduplication entry still turns a redelivered copy of the truncated event away");
  }
}
