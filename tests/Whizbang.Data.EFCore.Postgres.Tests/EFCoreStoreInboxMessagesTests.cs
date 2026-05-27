using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End-to-end driver tests for <see cref="EFCoreWorkCoordinator{TContext}.StoreInboxMessagesAsync"/>.
/// Slice 8 mirror of <c>StoreInboxMessagesSqlTests</c> (raw SQL) and the Dapper-driver
/// counterpart in <c>DapperStoreInboxMessagesTests</c> — exercises the EFCore driver's
/// JSONB serialization through the full path so a regression in either the C# wrapper
/// or the SQL function fails this test cohort.
/// </summary>
public class EFCoreStoreInboxMessagesTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _build(WorkCoordinationDbContext ctx)
    => new(ctx, JsonContextRegistry.CreateCombinedOptions());

  private static InboxMessage _makeInbox(Guid messageId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{\"p\":1}").RootElement,
      []);
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      StreamId = streamId,
      IsEvent = true,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
    };
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn, string sql, params (string name, object val)[] parms) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (n, v) in parms) {
      cmd.Parameters.AddWithValue(n, v);
    }
    var result = await cmd.ExecuteScalarAsync();
    return result is long l ? l : 0L;
  }

  [Test]
  public async Task EmptyArray_NoOpAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _build(dbContext);

    await coordinator.StoreInboxMessagesAsync([], partitionCount: 100);

    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await Assert.That(await _countAsync(conn, "SELECT COUNT(*) FROM wh_inbox")).IsEqualTo(0L);
    await Assert.That(await _countAsync(conn, "SELECT COUNT(*) FROM wh_message_deduplication")).IsEqualTo(0L);
  }

  [Test]
  public async Task SingleMessage_StoresInboxAndDedupAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _build(dbContext);
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await coordinator.StoreInboxMessagesAsync([_makeInbox(msgId, streamId)], partitionCount: 100);

    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await Assert.That(await _countAsync(conn, "SELECT COUNT(*) FROM wh_inbox WHERE message_id = @m", ("m", msgId))).IsEqualTo(1L);
    await Assert.That(await _countAsync(conn, "SELECT COUNT(*) FROM wh_message_deduplication WHERE message_id = @m", ("m", msgId))).IsEqualTo(1L);
  }

  [Test]
  public async Task DuplicateMessageId_SecondCallNoOpsAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _build(dbContext);
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msg = _makeInbox(msgId, streamId);

    await coordinator.StoreInboxMessagesAsync([msg], partitionCount: 100);
    await coordinator.StoreInboxMessagesAsync([msg], partitionCount: 100);

    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await Assert.That(await _countAsync(conn, "SELECT COUNT(*) FROM wh_inbox WHERE message_id = @m", ("m", msgId))).IsEqualTo(1L)
      .Because("EFCore driver redelivery must dedup at SQL level — exactly one wh_inbox row across both calls.");
  }

  [Test]
  public async Task BatchOf25Messages_AllStoredViaSingleDriverCallAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _build(dbContext);
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messages = new InboxMessage[25];
    for (var i = 0; i < 25; i++) {
      messages[i] = _makeInbox((Guid)TrackedGuid.NewMedo(), streamId);
    }

    await coordinator.StoreInboxMessagesAsync(messages, partitionCount: 100);

    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await Assert.That(await _countAsync(conn, "SELECT COUNT(*) FROM wh_inbox")).IsEqualTo(25L);
  }

  [Test]
  public async Task IsEventTrue_PropagatesToInboxRowAsync() {
    // Locks the EFCore C# → JSONB → SQL → wh_inbox round-trip for IsEvent.
    await using var dbContext = CreateDbContext();
    var coordinator = _build(dbContext);
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await coordinator.StoreInboxMessagesAsync([_makeInbox(msgId, streamId)], partitionCount: 100);

    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT is_event FROM wh_inbox WHERE message_id = @m";
    cmd.Parameters.AddWithValue("m", msgId);
    var isEvent = (bool?)await cmd.ExecuteScalarAsync();
    await Assert.That(isEvent).IsTrue()
      .Because("InboxMessage.IsEvent=true must propagate through EFCore serialization to wh_inbox.is_event.");
  }
}
