using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// SQL-level integration locks for <c>store_inbox_messages</c> (migration 021). This
/// function is the storage primitive the Whizbang receive boundary calls via
/// <see cref="IWorkCoordinator.StoreInboxMessagesAsync"/> — it carries the bulk-insert
/// invariants slice 8 of plans/pump-then-process.md leans on. There were NO direct SQL
/// tests for it before this file: lock the current contract (deduplication via
/// wh_message_deduplication, stream-pinning UPSERT into wh_active_streams,
/// ON CONFLICT DO NOTHING on inbox PK) so any future bulk-INSERT-FROM-unnest refactor
/// has to keep these properties.
/// </summary>
[Category("Shard2")]
public class StoreInboxMessagesSqlTests : EFCoreTestBase {

  [Test]
  public async Task EmptyArray_ReturnsNoRows_NoSideEffectsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, instanceId);

    var rowsReturned = await _callStoreInboxAsync(conn, instanceId, "[]");

    await Assert.That(rowsReturned).IsEqualTo(0)
      .Because("Empty input must return zero rows and run no INSERTs.");
    await Assert.That(await _countInboxAsync(conn)).IsEqualTo(0);
    await Assert.That(await _countDedupAsync(conn)).IsEqualTo(0);
  }

  [Test]
  public async Task SingleMessage_InsertsInboxAndDedup_ReturnsWasNewlyCreatedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, instanceId);

    var json = $"[{_messageJson(msgId, streamId)}]";
    var rowsReturned = await _callStoreInboxAsync(conn, instanceId, json);

    await Assert.That(rowsReturned).IsEqualTo(1)
      .Because("Function must return one row per newly stored message.");
    await Assert.That(await _countInboxAsync(conn)).IsEqualTo(1);
    await Assert.That(await _countDedupAsync(conn)).IsEqualTo(1);
  }

  [Test]
  public async Task DuplicateMessageId_SecondCallNoOpsViaDedupTableAsync() {
    // Slice-8 invariant: the dedup table is the source of truth for "have we seen this
    // message before" — wh_inbox uses ON CONFLICT DO NOTHING on its PK as a defensive
    // second layer. A redelivery (e.g., transport ack failure) calls store_inbox_messages
    // again with the same message_id; the second call must be a no-op (no second inbox
    // row, no second dedup row, returns 0 rows).
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, instanceId);

    var json = $"[{_messageJson(msgId, streamId)}]";
    var first = await _callStoreInboxAsync(conn, instanceId, json);
    var second = await _callStoreInboxAsync(conn, instanceId, json);

    await Assert.That(first).IsEqualTo(1);
    await Assert.That(second).IsEqualTo(0)
      .Because("Redelivery must not produce a second NewlyCreated row.");
    await Assert.That(await _countInboxAsync(conn)).IsEqualTo(1)
      .Because("Inbox row count must remain 1 across both calls — dedup blocks the second insert.");
    await Assert.That(await _countDedupAsync(conn)).IsEqualTo(1);
  }

  [Test]
  public async Task BatchOf50Messages_InsertsAllAndReturnsAllAsync() {
    // The bulk-batch path the receive boundary depends on: a single
    // store_inbox_messages call with N messages must atomically store all N. Locks the
    // contract; future bulk-INSERT-FROM-unnest refactors must preserve "N in → N stored".
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, instanceId);

    var ids = new Guid[50];
    var sb = new System.Text.StringBuilder("[");
    for (var i = 0; i < 50; i++) {
      ids[i] = (Guid)TrackedGuid.NewMedo();
      if (i > 0) {
        sb.Append(',');
      }
      sb.Append(_messageJson(ids[i], streamId));
    }
    sb.Append(']');

    var rowsReturned = await _callStoreInboxAsync(conn, instanceId, sb.ToString());

    await Assert.That(rowsReturned).IsEqualTo(50);
    await Assert.That(await _countInboxAsync(conn)).IsEqualTo(50);
    await Assert.That(await _countDedupAsync(conn)).IsEqualTo(50);
  }

  [Test]
  public async Task FirstMessageForStream_PinsInWhActiveStreamsAsync() {
    // Phase H step 6 slice 1 invariant (anchored here for slice 8): the first store call
    // for a stream UPSERTs into wh_active_streams with first-write-wins semantics —
    // subsequent calls from OTHER instances must not steal ownership.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var firstOwner = (Guid)TrackedGuid.NewMedo();
    var secondOwner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var m1 = (Guid)TrackedGuid.NewMedo();
    var m2 = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, firstOwner);
    await _registerInstanceAsync(conn, secondOwner);

    // First store binds the stream to firstOwner.
    await _callStoreInboxAsync(conn, firstOwner, $"[{_messageJson(m1, streamId)}]");
    var ownerAfterFirst = await _readActiveStreamOwnerAsync(conn, streamId);
    await Assert.That(ownerAfterFirst).IsEqualTo(firstOwner);

    // Second store from a different instance must NOT steal ownership.
    await _callStoreInboxAsync(conn, secondOwner, $"[{_messageJson(m2, streamId)}]");
    var ownerAfterSecond = await _readActiveStreamOwnerAsync(conn, streamId);
    await Assert.That(ownerAfterSecond).IsEqualTo(firstOwner)
      .Because("First-write-wins: ON CONFLICT DO NOTHING on wh_active_streams.stream_id PK preserves the original owner.");
  }

  // ===== helpers =====

  private static async Task<int> _callStoreInboxAsync(NpgsqlConnection conn, Guid instanceId, string messagesJson) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM store_inbox_messages(@p_msgs::jsonb, @p_inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    cmd.Parameters.Add(new NpgsqlParameter("p_msgs", NpgsqlDbType.Jsonb) { Value = messagesJson });
    cmd.Parameters.AddWithValue("p_inst", instanceId);
    var result = await cmd.ExecuteScalarAsync();
    return result is long l ? (int)l : 0;
  }

  private static async Task<int> _countInboxAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_inbox";
    var result = await cmd.ExecuteScalarAsync();
    return result is long l ? (int)l : 0;
  }

  private static async Task<int> _countDedupAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_message_deduplication";
    var result = await cmd.ExecuteScalarAsync();
    return result is long l ? (int)l : 0;
  }

  private static async Task<Guid?> _readActiveStreamOwnerAsync(NpgsqlConnection conn, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @s LIMIT 1";
    cmd.Parameters.AddWithValue("s", streamId);
    var result = await cmd.ExecuteScalarAsync();
    return result is Guid g ? g : null;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Builds a JSON object literal matching the shape store_inbox_messages parses
  /// from each array element. Includes only the fields the function reads.</summary>
  private static string _messageJson(Guid messageId, Guid streamId) {
    return $$"""
      {
        "MessageId": "{{messageId}}",
        "HandlerName": "TestHandler",
        "EnvelopeType": "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
        "MessageType": "Test.X, Test",
        "Envelope": {"p":1},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true
      }
      """;
  }
}
