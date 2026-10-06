// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.WorkCoordinator;

/// <summary>
/// The coordinator gives the same answer whatever concrete collection the caller hands it, and
/// short-circuits empty input without a round trip.
/// </summary>
/// <remarks>
/// <para>
/// Id lists are read as <c>ids is Guid[] arr ? arr : [.. ids]</c>: an array is bound directly,
/// anything else is copied into one. A collection expression such as <c>[id]</c> passed as
/// <see cref="IReadOnlyList{T}"/> is not an array (the compiler synthesizes a read-only list), so
/// the existing tests only ever took the copying arm, while hosts that pass an array took the
/// other. Each test here calls once with a <see cref="Guid"/> array and once with a
/// <see cref="List{T}"/>, against separate seeded rows, and asserts both reached the database.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class EFCoreWorkCoordinatorInputShapeTests : EFCoreTestBase {

  // ── Construction ───────────────────────────────────────────────────────

  [Test]
  public async Task Constructor_WithoutSerializerOptions_ThrowsArgumentNullAsync() {
    await using var dbContext = CreateDbContext();

    var thrown = Assert.Throws<ArgumentNullException>(
      () => _ = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, null!));

    await Assert.That(thrown!.ParamName).IsEqualTo("jsonOptions");
  }

  // ── Empty input short-circuits ─────────────────────────────────────────

  [Test]
  public async Task EmptyInputs_CompleteWithoutARoundTripAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);

    // An empty call must not reach the database: each of these completes synchronously.
    var releaseHolds = coordinator.ReleasePerspectiveRowHoldsAsync([]);
    var recordFailure = coordinator.RecordPerspectiveRowDestructionFailureAsync(
      [], TimeSpan.FromMinutes(1), maxRetries: 3, OnDestroyFailure.RetryThenForcedDelete);
    var markBackfill = coordinator.MarkConsumedTypeBackfillRequestedAsync([]);

    await Assert.That(releaseHolds.IsCompletedSuccessfully).IsTrue();
    await Assert.That(recordFailure.IsCompletedSuccessfully).IsTrue();
    await Assert.That(markBackfill.IsCompletedSuccessfully).IsTrue();
    await Assert.That(await recordFailure).IsEqualTo(0);
    await Assert.That(await coordinator.GetStreamEventsAsync(_newId(), [])).IsEmpty();
    await Assert.That(await coordinator.ReapExhaustedOrphanedPerspectiveRowsAsync(_newId(), [], maxAttempts: 3)).IsEqualTo(0);
    await Assert.That(await coordinator.GetPerspectiveCursorsBatchAsync([])).IsEmpty();
  }

  // ── Array and list inputs reach the database alike ─────────────────────

  [Test]
  public async Task CompleteOutboxPublished_ArrayAndListInputs_BothCompleteTheirRowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var instanceId = await _seedInstanceAsync(connection);
    var (first, _) = await _seedLeasedOutboxAsync(connection, instanceId);
    var (second, _) = await _seedLeasedOutboxAsync(connection, instanceId);

    Guid[] input1 = [first];
    await coordinator.CompleteOutboxPublishedAsync(input1, debugMode: false);
    List<Guid> input2 = [second];
    await coordinator.CompleteOutboxPublishedAsync(input2, debugMode: false);

    await Assert.That(await _countAsync(connection, "SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids)", [first, second]))
      .IsEqualTo(0L);
  }

  [Test]
  public async Task RenewLeases_ArrayAndListInputs_BothRenewTheirRowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var instanceId = await _seedInstanceAsync(connection);
    var (first, _) = await _seedLeasedOutboxAsync(connection, instanceId);
    var (second, _) = await _seedLeasedOutboxAsync(connection, instanceId);

    Guid[] input3 = [first];
    var renewedFromArray = await coordinator.RenewLeasesAsync(WorkCategory.Outbox, input3);
    List<Guid> input4 = [second];
    var renewedFromList = await coordinator.RenewLeasesAsync(WorkCategory.Outbox, input4);

    await Assert.That(renewedFromArray).IsEqualTo(1);
    await Assert.That(renewedFromList).IsEqualTo(1);
  }

  [Test]
  public async Task ReleaseUnprocessedInbox_ArrayAndListInputs_BothReleaseTheirRowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var instanceId = await _seedInstanceAsync(connection);
    var (first, _) = await _seedLeasedInboxAsync(connection, instanceId);
    var (second, _) = await _seedLeasedInboxAsync(connection, instanceId);

    Guid[] input5 = [first];
    var fromArray = await coordinator.ReleaseUnprocessedInboxAsync(instanceId, input5);
    List<Guid> input6 = [second];
    var fromList = await coordinator.ReleaseUnprocessedInboxAsync(instanceId, input6);

    await Assert.That(fromArray).IsEqualTo(1);
    await Assert.That(fromList).IsEqualTo(1);
  }

  [Test]
  public async Task ReleaseUnstartedLeases_ArrayAndListInputs_BothReleaseTheirStreamsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var instanceId = await _seedInstanceAsync(connection);
    var (_, firstStream) = await _seedLeasedInboxAsync(connection, instanceId);
    var (_, secondStream) = await _seedLeasedInboxAsync(connection, instanceId);

    Guid[] input7 = [firstStream];
    Guid[] input8 = [];
    var fromArrays = await coordinator.ReleaseUnstartedLeasesAsync(instanceId, input7, input8);
    List<Guid> input9 = [secondStream];
    List<Guid> input10 = [];
    var fromLists = await coordinator.ReleaseUnstartedLeasesAsync(instanceId, input9, input10);

    await Assert.That(fromArrays.InboxReleased).IsEqualTo(1);
    await Assert.That(fromLists.InboxReleased).IsEqualTo(1);
  }

  [Test]
  public async Task FetchOutboxBatch_ArrayAndListInputs_BothReturnTheLeasedRowAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var instanceId = await _seedInstanceAsync(connection);
    var (messageId, streamId) = await _seedLeasedOutboxAsync(connection, instanceId);

    Guid[] input11 = [streamId];
    var fromArray = await coordinator.FetchOutboxBatchAsync(input11, instanceId, maxPerStream: 100);
    List<Guid> input12 = [streamId];
    var fromList = await coordinator.FetchOutboxBatchAsync(input12, instanceId, maxPerStream: 100);

    await Assert.That(fromArray.Select(r => r.MessageId)).Contains(messageId);
    await Assert.That(fromList.Select(r => r.MessageId)).Contains(messageId);
  }

  [Test]
  public async Task FetchInboxBatch_ArrayAndListInputs_BothReturnTheLeasedRowAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var instanceId = await _seedInstanceAsync(connection);
    var (messageId, streamId) = await _seedLeasedInboxAsync(connection, instanceId);

    Guid[] input13 = [streamId];
    var fromArray = await coordinator.FetchInboxBatchAsync(input13, instanceId, maxPerStream: 100);
    List<Guid> input14 = [streamId];
    var fromList = await coordinator.FetchInboxBatchAsync(input14, instanceId, maxPerStream: 100);

    await Assert.That(fromArray.Select(r => r.MessageId)).Contains(messageId);
    await Assert.That(fromList.Select(r => r.MessageId)).Contains(messageId);
  }

  [Test]
  public async Task FetchEventsByIds_ArrayAndListInputs_BothReturnTheEventAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var (_, eventId) = await _seedEventAsync(connection, withBody: true);

    Guid[] input15 = [eventId];
    var fromArray = await coordinator.FetchEventsByIdsAsync(input15);
    List<Guid> input16 = [eventId];
    var fromList = await coordinator.FetchEventsByIdsAsync(input16);

    await Assert.That(fromArray.Select(e => e.EventId)).Contains(eventId);
    await Assert.That(fromList.Select(e => e.EventId)).Contains(eventId);
  }

  [Test]
  public async Task CompletePerspective_ArrayAndListWorkIds_BothCompleteTheirRowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var first = await _seedPerspectiveEventAsync(connection);
    var second = await _seedPerspectiveEventAsync(connection);

    Guid[] input17 = [first];
    await coordinator.CompletePerspectiveAsync(cursors: [], eventWorkIds: input17, debugMode: false);
    List<Guid> input18 = [second];
    await coordinator.CompletePerspectiveAsync(cursors: [], eventWorkIds: input18, debugMode: false);

    await Assert.That(await _countAsync(connection,
      "SELECT count(*) FROM wh_perspective_events WHERE event_work_id = ANY(@ids)", [first, second])).IsEqualTo(0L);
  }

  [Test]
  public async Task CompleteCoalesceFold_ArrayAndListFoldedIds_BothCompleteTheirSinglesAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var instanceId = await _seedInstanceAsync(connection);
    var (first, _) = await _seedLeasedOutboxAsync(connection, instanceId);
    var (second, _) = await _seedLeasedOutboxAsync(connection, instanceId);

    Guid[] input19 = [first];
    await coordinator.CompleteCoalesceFoldAsync(input19, [], partitionCount: 4);
    List<Guid> input20 = [second];
    await coordinator.CompleteCoalesceFoldAsync(input20, [], partitionCount: 4);

    await Assert.That(await _countAsync(connection,
      "SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids) AND processed_at IS NOT NULL", [first, second]))
      .IsEqualTo(2L)
      .Because("a non-empty fold marks every folded single complete, whichever collection carried the ids");
  }

  // ── Flush payload shapes ───────────────────────────────────────────────

  [Test]
  public async Task FlushCompletions_NullCursorsAndAnEmptyFailureCategory_StillCompletesTheOutboxAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var instanceId = await _seedInstanceAsync(connection);
    var (messageId, _) = await _seedLeasedOutboxAsync(connection, instanceId);

    await _coordinator(dbContext).FlushCompletionsAsync(new FlushCompletionsRequest(
      OutboxIds: [messageId],
      PerspectiveCursors: null,
      FailuresByCategory: [new CategoryFailures(WorkCategory.Outbox, [])]));

    await Assert.That(await _countAsync(connection, "SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids)", [messageId]))
      .IsEqualTo(0L);
  }

  [Test]
  public async Task FlushCompletions_EmptyCursorList_StillCompletesTheOutboxAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var instanceId = await _seedInstanceAsync(connection);
    var (messageId, _) = await _seedLeasedOutboxAsync(connection, instanceId);

    await _coordinator(dbContext).FlushCompletionsAsync(new FlushCompletionsRequest(
      OutboxIds: [messageId],
      PerspectiveCursors: []));

    await Assert.That(await _countAsync(connection, "SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids)", [messageId]))
      .IsEqualTo(0L);
  }

  /// <summary>
  /// The cursor function derives the new position from processed perspective events, not from the
  /// payload's LastEventId; with no outstanding events for the pair it marks the cursor complete
  /// (status 2). An empty payload never reaches that function, so the status change proves the
  /// non-empty list was serialized and applied.
  /// </summary>
  [Test]
  public async Task FlushCompletions_WithACursor_MarksItCompleteAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var (streamId, eventId) = await _seedEventAsync(connection, withBody: false);
    await _seedCursorAsync(connection, streamId, "P.Flush");

    await _coordinator(dbContext).FlushCompletionsAsync(new FlushCompletionsRequest(
      PerspectiveCursors: [new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = "P.Flush",
        LastEventId = eventId,
        Status = PerspectiveProcessingStatus.Completed
      }]));

    await Assert.That(await _countAsync(connection,
      "SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = ANY(@ids) AND perspective_name = 'P.Flush' AND status = 2",
      [streamId])).IsEqualTo(1L)
      .Because("a non-empty cursor list is serialized and applied, not replaced by the empty-list payload");
  }

  // ── Cursor lookups ─────────────────────────────────────────────────────

  [Test]
  public async Task GetPerspectiveCursor_MissingThenPresent_ReturnsNullThenTheCursorAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var (streamId, eventId) = await _seedEventAsync(connection, withBody: false);

    var missing = await coordinator.GetPerspectiveCursorAsync(streamId, "P.Lookup");
    await _seedCursorAsync(connection, streamId, "P.Lookup", eventId);
    var present = await coordinator.GetPerspectiveCursorAsync(streamId, "P.Lookup");

    await Assert.That(missing).IsNull();
    await Assert.That(present).IsNotNull();
    await Assert.That(present!.LastEventId).IsEqualTo(eventId);
  }

  // ── Handler commit payload ─────────────────────────────────────────────

  [Test]
  public async Task CommitHandlerResult_InDebugModeWithAnEmptyInboxList_MarksTheInboxRowProcessedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var instanceId = await _seedInstanceAsync(connection);
    var (inboxId, _) = await _seedLeasedInboxAsync(connection, instanceId);

    await _coordinator(dbContext).CommitHandlerResultAsync(new HandlerCommitRequest(
      HandlerId: _newId(),
      InstanceId: instanceId,
      ServiceName: "shape-svc",
      HostName: "shape-host",
      ProcessId: 1,
      PartitionCount: 10000,
      InboxCompletion: new HandlerInboxCompletion(inboxId, Status: 4),
      NewOutboxMessages: [],
      NewInboxMessages: [],
      DebugMode: true));

    await Assert.That(await _countAsync(connection,
      "SELECT count(*) FROM wh_inbox_state WHERE message_id = ANY(@ids) AND processed_at IS NOT NULL", [inboxId]))
      .IsEqualTo(1L)
      .Because("debug mode retains the completed row, so it is still there to be read as processed");
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static Guid _newId() => (Guid)TrackedGuid.New();

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext dbContext) =>
    new(dbContext, JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task<long> _countAsync(NpgsqlConnection connection, string sql, Guid[] ids) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(ids), ids);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<Guid> _seedInstanceAsync(NpgsqlConnection connection) {
    var instanceId = _newId();
    await using var ins = connection.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'shape-svc', 'shape-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    ins.Parameters.AddWithValue("id", instanceId);
    await ins.ExecuteNonQueryAsync();
    return instanceId;
  }

  private static async Task<(Guid MessageId, Guid StreamId)> _seedLeasedOutboxAsync(NpgsqlConnection connection, Guid instanceId) {
    var messageId = _newId();
    var streamId = _newId();
    await using var ins = connection.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry, is_event)
      VALUES (@msg, 'topic-shape', 'ShapeType', 'ShapeEnvelope', '{"p":1}', '{"h":1}', '{"t":"tenant"}',
              3, 0, NOW(), @stream, 7, @inst, NOW() + INTERVAL '5 minutes', true)
      """;
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("inst", instanceId);
    await ins.ExecuteNonQueryAsync();
    return (messageId, streamId);
  }

  private static async Task<(Guid MessageId, Guid StreamId)> _seedLeasedInboxAsync(NpgsqlConnection connection, Guid instanceId) {
    var messageId = _newId();
    var streamId = _newId();
    await using var ins = connection.CreateCommand();
    ins.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, scope, received_at, stream_id, is_event)
        VALUES (@msg, 'ShapeHandler', 'ShapeType', '{"p":1}', '{"h":1}', '{"t":"tenant"}', NOW(), @stream, true)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event,
         status, attempts, instance_id, lease_expiry, partition_number)
      SELECT message_id, stream_id, received_at, priority, is_event,
             1, 1, @inst, NOW() + INTERVAL '5 minutes', 9 FROM m
      """;
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("inst", instanceId);
    await ins.ExecuteNonQueryAsync();
    return (messageId, streamId);
  }

  private static async Task<(Guid StreamId, Guid EventId)> _seedEventAsync(NpgsqlConnection connection, bool withBody) {
    var streamId = _newId();
    var eventId = _newId();
    await using var ins = connection.CreateCommand();
    ins.CommandText = withBody
      ? """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
        VALUES (@evt, @stream, @stream, 'agg', 'Shape.Type', '{}'::jsonb, 1, NOW());
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@evt, '{"x":1}'::jsonb, '{}'::jsonb)
        """
      : """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
        VALUES (@evt, @stream, @stream, 'agg', 'Shape.Type', '{}'::jsonb, 1, NOW())
        """;
    ins.Parameters.AddWithValue("evt", eventId);
    ins.Parameters.AddWithValue("stream", streamId);
    await ins.ExecuteNonQueryAsync();
    return (streamId, eventId);
  }

  private static async Task _seedCursorAsync(NpgsqlConnection connection, Guid streamId, string perspectiveName, Guid? lastEventId = null) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at)
      VALUES (@stream, @name, @last, 1, NOW())
      """;
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("name", perspectiveName);
    ins.Parameters.AddWithValue("last", (object?)lastEventId ?? DBNull.Value);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _seedPerspectiveEventAsync(NpgsqlConnection connection) {
    var workId = _newId();
    await using var ins = connection.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
      VALUES (@work, @stream, 'Shape.Perspective', @eid, 0, 0, NOW())
      """;
    ins.Parameters.AddWithValue("work", workId);
    ins.Parameters.AddWithValue("stream", _newId());
    ins.Parameters.AddWithValue("eid", _newId());
    await ins.ExecuteNonQueryAsync();
    return workId;
  }
}
