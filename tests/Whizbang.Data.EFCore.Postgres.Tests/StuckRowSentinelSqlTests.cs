using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Locks the v0.657 slice 5a invariants around the structural stuck-row sentinel SQL
/// surface (<c>find_stuck_outbox_rows</c> / <c>find_stuck_inbox_rows</c>) and the
/// partial indexes that keep their cost negligible.
/// </summary>
/// <remarks>
/// <para>
/// Slot-3 forensic exposed a class of bug — "row gets claimed but never reaches the
/// drainer" — that bypasses every downstream defense (DLQ promotion, lifecycle
/// failure capture, drain pipeline). Slices 1-4 close the specific Empty-stream
/// instance of this class. Slice 5 adds a structural canary: any row that has been
/// claimed past <c>MaxOutboxAttempts</c> without reaching the drainer surfaces in
/// these queries and is logged as a Warning by the maintenance worker.
/// </para>
/// <para>
/// Cost design: partial indexes gated on <c>attempts &gt; 5</c> stay ~0-sized in
/// steady state (most rows publish in 1-2 attempts and clear). Query cost is an
/// index range scan on a near-empty set — O(log N) effectively free, runs once per
/// <c>perform_maintenance</c> cycle (10 min default).
/// </para>
/// </remarks>
/// <docs>operations/observability/stuck-row-sentinel</docs>
public class StuckRowSentinelSqlTests : EFCoreTestBase {

  /// <summary>
  /// The slot-3 case: a wh_outbox row with attempts &gt; MaxOutboxAttempts and
  /// no processed_at MUST appear in find_stuck_outbox_rows. This is the
  /// structural signal that "the drainer never got to it" — independent of
  /// WHY (Empty stream_id, gate saturation, transport hang, future unknown
  /// bug).
  /// </summary>
  [Test]
  public async Task FindStuckOutboxRows_RowExceedsThreshold_ReturnedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _insertOutboxRowAsync(conn, messageId, streamId, attempts: 15);

    var stuck = await _findStuckOutboxAsync(conn, maxAttempts: 10, limit: 50);

    await Assert.That(stuck).Contains(messageId)
      .Because("A row at attempts=15 with no processed_at and no DLQ promotion is the canonical stuck-row signal. The sentinel surfaces it so an operator/AI can investigate.");
  }

  /// <summary>
  /// Healthy rows (attempts ≤ threshold) MUST NOT appear — the sentinel must
  /// not create false positives during normal operation.
  /// </summary>
  [Test]
  public async Task FindStuckOutboxRows_HealthyRow_NotReturnedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _insertOutboxRowAsync(conn, messageId, (Guid)TrackedGuid.NewMedo(), attempts: 1);

    var stuck = await _findStuckOutboxAsync(conn, maxAttempts: 10, limit: 50);

    await Assert.That(stuck).DoesNotContain(messageId)
      .Because("attempts=1 is healthy — first claim attempt. Surfacing healthy traffic would flood operator dashboards.");
  }

  /// <summary>
  /// Processed rows (processed_at IS NOT NULL) MUST NOT appear — even if
  /// they had high attempts at the time of completion. The sentinel only
  /// surfaces actively stuck rows.
  /// </summary>
  [Test]
  public async Task FindStuckOutboxRows_ProcessedRow_NotReturnedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _insertOutboxRowAsync(conn, messageId, (Guid)TrackedGuid.NewMedo(), attempts: 50, processed: true);

    var stuck = await _findStuckOutboxAsync(conn, maxAttempts: 10, limit: 50);

    await Assert.That(stuck).DoesNotContain(messageId)
      .Because("A row that eventually succeeded (processed_at IS NOT NULL) is not stuck — the sentinel only cares about rows that are STILL claimed past the threshold.");
  }

  /// <summary>
  /// Limit parameter MUST be honored — under widespread stuck-row scenarios
  /// the sentinel must not return unbounded rows (would flood the log).
  /// </summary>
  [Test]
  public async Task FindStuckOutboxRows_HonorsLimitAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    for (var i = 0; i < 5; i++) {
      await _insertOutboxRowAsync(conn, (Guid)TrackedGuid.NewMedo(), (Guid)TrackedGuid.NewMedo(), attempts: 15);
    }

    var stuck = await _findStuckOutboxAsync(conn, maxAttempts: 10, limit: 2);

    await Assert.That(stuck.Count).IsLessThanOrEqualTo(2)
      .Because("Under saturation the maintenance worker must not flood the log; the limit bounds the per-cycle Warning count.");
  }

  /// <summary>
  /// Mirror invariant for inbox.
  /// </summary>
  [Test]
  public async Task FindStuckInboxRows_RowExceedsThreshold_ReturnedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 15);

    var stuck = await _findStuckInboxAsync(conn, maxAttempts: 10, limit: 50);

    await Assert.That(stuck).Contains(messageId)
      .Because("Inbox stuck-row pattern is the same as outbox — high attempts + no processed_at = silent stuck.");
  }

  /// <summary>
  /// Verify the partial-index predicate covers <c>attempts &gt; 5</c>. We assert
  /// against the index's EXISTS-check rather than EXPLAIN output (EXPLAIN format
  /// can change between PG versions). The presence of the named partial index
  /// is the structural invariant — its absence means the maintenance query
  /// would full-scan wh_outbox.
  /// </summary>
  [Test]
  public async Task PartialIndex_ExistsOnOutboxAndInboxAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);

    var outboxIdx = await _indexExistsAsync(conn, "idx_outbox_stuck_sentinel");
    var inboxIdx = await _indexExistsAsync(conn, "idx_inbox_stuck_sentinel");

    await Assert.That(outboxIdx).IsTrue()
      .Because("Without idx_outbox_stuck_sentinel, find_stuck_outbox_rows would full-scan wh_outbox on every 10-min maintenance tick — at JDX scale (millions of historical rows), the sentinel itself becomes a problem.");
    await Assert.That(inboxIdx).IsTrue()
      .Because("Same for wh_inbox — the index is the cost-control mechanism.");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _insertOutboxRowAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, int attempts, bool processed = false) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = processed
      ? @"INSERT INTO wh_outbox
            (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
             created_at, processed_at, stream_id, partition_number)
          VALUES (@msg, 'topic', 'Stuck.TestEvent', 'Stuck.TestEnvelope', '{}', '{}', 1, @attempts, NOW(), NOW(), @stream, 0)"
      : @"INSERT INTO wh_outbox
            (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
             created_at, stream_id, partition_number)
          VALUES (@msg, 'topic', 'Stuck.TestEvent', 'Stuck.TestEnvelope', '{}', '{}', 1, @attempts, NOW(), @stream, 0)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("attempts", attempts);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertInboxRowAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts,
         received_at, stream_id, partition_number)
      VALUES (@msg, 'TestHandler', 'Stuck.TestEvent', '{}', '{}', 1, @attempts, NOW(), @stream, 0)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("attempts", attempts);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task<List<Guid>> _findStuckOutboxAsync(NpgsqlConnection conn, int maxAttempts, int limit) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT message_id FROM find_stuck_outbox_rows(@max, @lim)";
    cmd.Parameters.AddWithValue("max", maxAttempts);
    cmd.Parameters.AddWithValue("lim", limit);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<List<Guid>> _findStuckInboxAsync(NpgsqlConnection conn, int maxAttempts, int limit) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT message_id FROM find_stuck_inbox_rows(@max, @lim)";
    cmd.Parameters.AddWithValue("max", maxAttempts);
    cmd.Parameters.AddWithValue("lim", limit);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<bool> _indexExistsAsync(NpgsqlConnection conn, string indexName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT 1 FROM pg_indexes WHERE indexname = @name";
    cmd.Parameters.AddWithValue("name", indexName);
    return await cmd.ExecuteScalarAsync() is not null;
  }
}
