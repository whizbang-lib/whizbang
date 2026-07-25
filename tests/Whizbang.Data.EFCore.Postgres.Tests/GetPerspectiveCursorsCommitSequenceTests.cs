using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 26.13 — locks the JOIN that hydrates <see cref="PerspectiveCursorInfo.LastCommitSequence"/>
/// from <c>wh_event_store.commit_sequence</c> via <c>wh_perspective_cursors.last_event_id</c>.
/// Without this, <c>PerspectiveWorker._prefetchMissingDrainModeCursorsAsync</c> can't warm the
/// commit-sequence half of <see cref="Whizbang.Core.Workers.PerspectiveCursorCache"/> on cold
/// starts or post-rewind, and the inversion detector falls through to the event_id path —
/// re-introducing the UUIDv7 lex-vs-commit-order false positives that slice 26 was meant to
/// eliminate (observed in a production run as a burst of false-positive inversions).
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public class GetPerspectiveCursorsCommitSequenceTests : EFCoreTestBase {

  [Test]
  public async Task GetPerspectiveCursorsBatchAsync_LastEventHasCommitSequence_ReturnsItAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    const long expectedSeq = 4242L;

    await _insertEventStoreRowAsync(conn, eventId, streamId, "T", expectedSeq);
    await _insertCursorAsync(conn, streamId, "Some.Perspective", eventId);

    var coord = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var results = await coord.GetPerspectiveCursorsBatchAsync([streamId]);

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].StreamId).IsEqualTo(streamId);
    await Assert.That(results[0].LastEventId).IsEqualTo(eventId);
    await Assert.That(results[0].LastCommitSequence).IsEqualTo(expectedSeq);
  }

  [Test]
  public async Task GetPerspectiveCursorsBatchAsync_LastEventUnstamped_ReturnsNullCommitSequenceAsync() {
    // Unstamped row (commit_sequence IS NULL) — the JOIN must yield NULL, not throw, not skip the row.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();

    await _insertEventStoreRowAsync(conn, eventId, streamId, "T", commitSequence: null);
    await _insertCursorAsync(conn, streamId, "Some.Perspective", eventId);

    var coord = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var results = await coord.GetPerspectiveCursorsBatchAsync([streamId]);

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].LastEventId).IsEqualTo(eventId);
    await Assert.That(results[0].LastCommitSequence).IsNull();
  }

  [Test]
  public async Task GetPerspectiveCursorsBatchAsync_NoLastEvent_ReturnsNullCommitSequenceAsync() {
    // last_event_id IS NULL — cursor row exists (perspective leased but never advanced).
    // LEFT JOIN must keep the row and yield NULL for commit_sequence.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var streamId = (Guid)TrackedGuid.NewMedo();
    await _insertCursorAsync(conn, streamId, "Some.Perspective", lastEventId: null);

    var coord = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var results = await coord.GetPerspectiveCursorsBatchAsync([streamId]);

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].LastEventId).IsNull();
    await Assert.That(results[0].LastCommitSequence).IsNull();
  }

  [Test]
  public async Task GetPerspectiveCursorAsync_SingleStream_ReturnsCommitSequenceAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    const long expectedSeq = 9001L;

    await _insertEventStoreRowAsync(conn, eventId, streamId, "T", expectedSeq);
    await _insertCursorAsync(conn, streamId, "Some.Perspective", eventId);

    var coord = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var result = await coord.GetPerspectiveCursorAsync(streamId, "Some.Perspective");

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.LastEventId).IsEqualTo(eventId);
    await Assert.That(result.LastCommitSequence).IsEqualTo(expectedSeq);
  }

  private static async Task _insertEventStoreRowAsync(
      NpgsqlConnection conn, Guid eventId, Guid streamId, string eventType, long? commitSequence) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at, commit_sequence)
      VALUES (@evt, @stream, @stream, 'agg', @type, '{}'::jsonb, 1, NOW(), @seq)";
    ins.Parameters.AddWithValue("evt", eventId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("type", eventType);
    if (commitSequence.HasValue) {
      ins.Parameters.AddWithValue("seq", commitSequence.Value);
    } else {
      ins.Parameters.AddWithValue("seq", DBNull.Value);
    }
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertCursorAsync(
      NpgsqlConnection conn, Guid streamId, string perspectiveName, Guid? lastEventId) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_cursors
        (stream_id, perspective_name, last_event_id, status, processed_at)
      VALUES (@stream, @name, @last_event, 1, NOW())";
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("name", perspectiveName);
    if (lastEventId.HasValue) {
      ins.Parameters.AddWithValue("last_event", lastEventId.Value);
    } else {
      ins.Parameters.AddWithValue("last_event", DBNull.Value);
    }
    await ins.ExecuteNonQueryAsync();
  }
}
