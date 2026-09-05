using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for the tag-bound coalescing persistence chain (migration 115): the
/// <c>wh_outbox.coalesce_group</c> column flows through <c>store_outbox_messages</c>, the
/// eligible-scan partial index excludes coalesce-pending rows BY MEMBERSHIP (not per-row
/// filtering), <c>claim_work</c> / <c>claim_orphaned_outbox</c> never claim or lease them —
/// even matured ones, whose degrade is an explicit release — and a released row
/// (group and floor cleared) ships through the normal pump unchanged.
/// </summary>
/// <docs>fundamentals/messages/message-tags#coalescing</docs>
[Category("Shard1")]
public class TagBoundCoalescingSqlTests : EFCoreTestBase {
  /// <summary>
  /// A CoalesceGroup value in the store_outbox_messages payload must persist to the
  /// wh_outbox.coalesce_group column (and the floor to scheduled_for).
  /// </summary>
  [Test]
  public async Task StoreOutboxMessages_CoalesceGroupInPayload_PersistsToColumnAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var messageId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await using (var store = connection.CreateCommand()) {
      store.CommandText = @"
        SELECT * FROM store_outbox_messages(
          @messages::jsonb, NULL::uuid, NULL::timestamptz, NOW(), 10000)";
      store.Parameters.AddWithValue("messages", $$"""
        [{
          "MessageId": "{{messageId}}",
          "Destination": "test-topic",
          "MessageType": "TestEvent",
          "EnvelopeType": "TestEnvelope",
          "Envelope": {},
          "Metadata": {},
          "StreamId": "{{streamId}}",
          "IsEvent": false,
          "ScheduledFor": "2026-08-18T12:02:00Z",
          "CoalesceGroup": "sys-audit"
        }]
        """);
      await store.ExecuteNonQueryAsync();
    }

    await using var read = connection.CreateCommand();
    read.CommandText = "SELECT coalesce_group, scheduled_for FROM wh_outbox WHERE message_id = @id";
    read.Parameters.AddWithValue("id", messageId);
    await using var reader = await read.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();

    await Assert.That(reader.GetString(0)).IsEqualTo("sys-audit");
    await Assert.That(reader.IsDBNull(1)).IsFalse()
      .Because("the max-delay floor rides scheduled_for so an unfolded single ships at the deadline");
  }

  /// <summary>
  /// An absent CoalesceGroup key (every pre-coalescing producer) stores NULL — the normal
  /// immediately-claimable row shape.
  /// </summary>
  [Test]
  public async Task StoreOutboxMessages_NoCoalesceGroup_StoresNullAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var messageId = Guid.NewGuid();
    await using (var store = connection.CreateCommand()) {
      store.CommandText = @"
        SELECT * FROM store_outbox_messages(
          @messages::jsonb, NULL::uuid, NULL::timestamptz, NOW(), 10000)";
      store.Parameters.AddWithValue("messages", $$"""
        [{
          "MessageId": "{{messageId}}",
          "Destination": "test-topic",
          "MessageType": "TestEvent",
          "EnvelopeType": "TestEnvelope",
          "Envelope": {},
          "Metadata": {},
          "StreamId": "{{Guid.NewGuid()}}",
          "IsEvent": false
        }]
        """);
      await store.ExecuteNonQueryAsync();
    }

    await using var read = connection.CreateCommand();
    read.CommandText = "SELECT coalesce_group IS NULL FROM wh_outbox WHERE message_id = @id";
    read.Parameters.AddWithValue("id", messageId);

    var isNull = (bool)(await read.ExecuteScalarAsync())!;

    await Assert.That(isNull).IsTrue();
  }

  /// <summary>
  /// The hot-path lock: from a mixed batch, claim_work returns ZERO coalesce-pending rows —
  /// including MATURED ones (scheduled_for already elapsed). Deadline-degrade is an explicit
  /// release by the coalesce worker, never an implicit query union; before migration 115 a
  /// matured floor row would have shipped through the pump on its own.
  /// </summary>
  [Test]
  public async Task ClaimWork_MixedBatch_ReturnsZeroCoalescePendingRowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    await _heartbeatAsync(connection, instanceId);

    var normalIds = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      normalIds.Add(await _seedOutboxRowAsync(connection, coalesceGroup: null, scheduledFor: null));
    }
    var pendingIds = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      // Matured on purpose: scheduled_for in the past AND still grouped — the sharpest case.
      pendingIds.Add(await _seedOutboxRowAsync(connection, coalesceGroup: "sys-audit", scheduledFor: "NOW() - INTERVAL '1 second'"));
    }

    var claimed = await _claimAllAsync(connection, instanceId);

    foreach (var normalId in normalIds) {
      await Assert.That(claimed).Contains(normalId)
        .Because("normal rows must keep shipping exactly as before");
    }
    foreach (var pendingId in pendingIds) {
      await Assert.That(claimed).DoesNotContain(pendingId)
        .Because("coalesce-pending rows are invisible to the claim pump until folded or explicitly released");
    }
  }

  /// <summary>
  /// Coalesce-pending rows must not even be LEASED by the orphan claim — lease churn on
  /// thousands of pending singles per poll is exactly the hot-path tax the column exists
  /// to remove.
  /// </summary>
  [Test]
  public async Task ClaimWork_CoalescePendingRow_IsNotLeasedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    await _heartbeatAsync(connection, instanceId);
    var pendingId = await _seedOutboxRowAsync(connection, coalesceGroup: "sys-audit", scheduledFor: "NOW() - INTERVAL '1 second'");

    await _claimAllAsync(connection, instanceId);

    await using var read = connection.CreateCommand();
    read.CommandText = "SELECT instance_id IS NULL FROM wh_outbox WHERE message_id = @id";
    read.Parameters.AddWithValue("id", pendingId);
    var unleased = (bool)(await read.ExecuteScalarAsync())!;
    await Assert.That(unleased).IsTrue();
  }

  /// <summary>
  /// The release transition: clearing coalesce_group + scheduled_for moves a row into the
  /// eligible index, and the very next claim ships it — the deadline-degrade path is a
  /// visible state change, not a special query.
  /// </summary>
  [Test]
  public async Task ClaimWork_ReleasedRow_IsClaimedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    await _heartbeatAsync(connection, instanceId);
    var rowId = await _seedOutboxRowAsync(connection, coalesceGroup: "sys-audit", scheduledFor: "NOW() - INTERVAL '1 second'");

    await using (var release = connection.CreateCommand()) {
      release.CommandText = @"
        UPDATE wh_outbox SET coalesce_group = NULL, scheduled_for = NULL
        WHERE message_id = @id";
      release.Parameters.AddWithValue("id", rowId);
      await release.ExecuteNonQueryAsync();
    }

    var claimed = await _claimAllAsync(connection, instanceId);

    await Assert.That(claimed).Contains(rowId);
  }

  /// <summary>
  /// Locks the index shapes migration 115 re-creates: the eligible-scan partial index
  /// excludes coalesce-pending rows in its PREDICATE (membership exclusion — zero per-row
  /// filtering for normal traffic), and the coalesce worker gets its own tiny partial index.
  /// </summary>
  [Test]
  public async Task OutboxIndexes_CoalescePredicatesAreInPlaceAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var cmd = ((NpgsqlConnection)connection).CreateCommand();
    cmd.CommandText = @"
      SELECT indexname, COALESCE(pg_get_expr(i.indpred, i.indrelid), '')
      FROM pg_indexes x
      JOIN pg_class c ON c.relname = x.indexname
      JOIN pg_index i ON i.indexrelid = c.oid
      WHERE x.tablename = 'wh_outbox'
        AND x.indexname IN ('idx_outbox_unprocessed_claiming', 'idx_outbox_coalesce_pending')";

    var predicates = new Dictionary<string, string>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        predicates[reader.GetString(0)] = reader.GetString(1);
      }
    }

    await Assert.That(predicates.ContainsKey("idx_outbox_unprocessed_claiming")).IsTrue();
    await Assert.That(predicates["idx_outbox_unprocessed_claiming"]).Contains("coalesce_group IS NULL")
      .Because("coalesce-pending singles must never ENTER the index the claim path scans");
    await Assert.That(predicates.ContainsKey("idx_outbox_coalesce_pending")).IsTrue();
    await Assert.That(predicates["idx_outbox_coalesce_pending"]).Contains("coalesce_group IS NOT NULL");
    await Assert.That(predicates["idx_outbox_coalesce_pending"]).Contains("processed_at IS NULL");
  }

  #region Helpers

  private static async Task _heartbeatAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var hb = connection.CreateCommand();
    hb.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
    hb.Parameters.AddWithValue("id", instanceId);
    await hb.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _seedOutboxRowAsync(
      NpgsqlConnection connection,
      string? coalesceGroup,
      string? scheduledFor) {
    var messageId = Guid.NewGuid();
    await using var ins = connection.CreateCommand();
    ins.CommandText = $@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, coalesce_group, scheduled_for)
      VALUES (@msg, 'test-topic', 'TestEvent', '{{}}', '{{}}', 0, 0,
         NOW(), @stream, 0, @grp, {(scheduledFor ?? "NULL")})";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", Guid.NewGuid());
    ins.Parameters.AddWithValue("grp", (object?)coalesceGroup ?? DBNull.Value);
    await ins.ExecuteNonQueryAsync();
    return messageId;
  }

  private static async Task<List<Guid>> _claimAllAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT source, work_id FROM claim_work(
        p_instance_id => @id,
        p_service_name => 'test',
        p_host_name => 'test-host',
        p_process_id => 1,
        p_max_streams => 100,
        p_partition_count => 10000,
        p_lease_seconds => 300
      )";
    cmd.Parameters.AddWithValue("id", instanceId);

    var claimed = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      if (reader.GetString(0) == "outbox" && !await reader.IsDBNullAsync(1)) {
        claimed.Add(reader.GetGuid(1));
      }
    }
    return claimed;
  }

  #endregion

  [Test]
  public async Task CoalescePendingRow_DoesNotBlockLaterRowsOnItsStreamAsync() {
    // #668 H3: a coalesce-pending row parks with scheduled_for = created + MaxDelay. The
    // claim path's stream-ordering guard read that as "an earlier deferred delivery" and
    // refused to claim ANY later row on the same stream for the whole window — re-armed by
    // every new coalesce row, i.e. permanently under sustained ingest. That is the
    // publish-at-zero half of the incident: the backlog was not just unfolded, it was
    // gating unrelated rows on its streams. Coalesce rows are parked for FOLDING, not
    // deferred deliveries — they must not gate their stream.
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) { await connection.OpenAsync(); }
    var instanceId = Guid.NewGuid();
    await _heartbeatAsync(connection, instanceId);

    var sharedStream = Guid.NewGuid();
    // Earlier coalesce-pending row: parked into the future, same stream.
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = $@"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts,
           created_at, stream_id, partition_number, coalesce_group, scheduled_for)
        VALUES (@msg, 'test-topic', 'TestEvent', '{{}}', '{{}}', 0, 0,
           NOW() - INTERVAL '10 seconds', @stream, 0, 'sys-audit', NOW() + INTERVAL '110 seconds')";
      ins.Parameters.AddWithValue("msg", Guid.NewGuid());
      ins.Parameters.AddWithValue("stream", sharedStream);
      await ins.ExecuteNonQueryAsync();
    }
    // Later NORMAL row on the same stream: claimable now.
    var normalId = Guid.NewGuid();
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = $@"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts,
           created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{{}}', '{{}}', 0, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", normalId);
      ins.Parameters.AddWithValue("stream", sharedStream);
      await ins.ExecuteNonQueryAsync();
    }

    var claimed = await _claimAllAsync(connection, instanceId);

    await Assert.That(claimed).Contains(normalId)
      .Because("a parked coalesce row is the FOLD worker's business; the stream's live "
             + "traffic must keep flowing around it — blocking it for the MaxDelay window "
             + "is how a bulk ingest starves its own streams");
  }

}
