using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// End-to-end driver tests for <see cref="DapperWorkCoordinator.StoreInboxMessagesAsync"/>.
/// Slice 8 mirror of <c>StoreInboxMessagesSqlTests</c> in the EFCore project, but coming
/// THROUGH the Dapper driver — exercises both the C#-side JSONB serialization (which the
/// raw-SQL tests skip) and the SQL function. Catches a class of regressions where the
/// Dapper driver's <c>_serializeNewInboxMessages</c> drifts from the JSON shape
/// <c>store_inbox_messages</c> expects.
/// </summary>
public class DapperStoreInboxMessagesTests : PostgresTestBase {

  private DapperWorkCoordinator _buildCoordinator() {
    // Use the source-generated InfrastructureJsonContext — it has the InboxMessage +
    // InboxMessage[] + envelope-related type info the Dapper driver needs to serialize
    // the JSONB array to wh_inbox.
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = InfrastructureJsonContext.Default,
    };
    return new DapperWorkCoordinator(
      ConnectionString,
      jsonOptions,
      NullLogger<DapperWorkCoordinator>.Instance);
  }

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

  [Test]
  public async Task EmptyArray_NoOpAsync() {
    var coordinator = _buildCoordinator();
    await coordinator.StoreInboxMessagesAsync([], partitionCount: 100);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var inboxCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_inbox");
    var dedupCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_message_deduplication");
    await Assert.That(inboxCount).IsEqualTo(0L);
    await Assert.That(dedupCount).IsEqualTo(0L);
  }

  [Test]
  public async Task SingleMessage_StoresInboxAndDedupAsync() {
    var coordinator = _buildCoordinator();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await coordinator.StoreInboxMessagesAsync([_makeInbox(msgId, streamId)], partitionCount: 100);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var inboxCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_inbox WHERE message_id = @m", new { m = msgId });
    var dedupCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_message_deduplication WHERE message_id = @m", new { m = msgId });
    await Assert.That(inboxCount).IsEqualTo(1L);
    await Assert.That(dedupCount).IsEqualTo(1L);
  }

  [Test]
  public async Task DuplicateMessageId_SecondCallNoOpsAsync() {
    var coordinator = _buildCoordinator();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msg = _makeInbox(msgId, streamId);

    await coordinator.StoreInboxMessagesAsync([msg], partitionCount: 100);
    await coordinator.StoreInboxMessagesAsync([msg], partitionCount: 100);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var inboxCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_inbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(inboxCount).IsEqualTo(1L)
      .Because("Redelivery via the Dapper driver must dedup at SQL level — exactly one wh_inbox row across both calls.");
  }

  [Test]
  public async Task BatchOf25Messages_AllStoredViaSingleDriverCallAsync() {
    var coordinator = _buildCoordinator();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var ids = new Guid[25];
    var messages = new InboxMessage[25];
    for (var i = 0; i < 25; i++) {
      ids[i] = (Guid)TrackedGuid.NewMedo();
      messages[i] = _makeInbox(ids[i], streamId);
    }

    await coordinator.StoreInboxMessagesAsync(messages, partitionCount: 100);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var inboxCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_inbox");
    await Assert.That(inboxCount).IsEqualTo(25L)
      .Because("All 25 messages from the C# array must serialize correctly into the JSONB array store_inbox_messages parses.");
  }

  [Test]
  public async Task IsEventTrue_PropagatesToInboxRowAsync() {
    // Locks that the IsEvent flag survives the C# → JSONB → SQL → wh_inbox round-trip.
    // A regression in DapperWorkCoordinator's serialization (e.g. wrong property name,
    // wrong casing) would silently set is_event to false and break the event store
    // backfill path.
    var coordinator = _buildCoordinator();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await coordinator.StoreInboxMessagesAsync([_makeInbox(msgId, streamId)], partitionCount: 100);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var isEvent = await conn.ExecuteScalarAsync<bool>(
      "SELECT is_event FROM wh_inbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(isEvent).IsTrue()
      .Because("InboxMessage.IsEvent=true must propagate through Dapper serialization to wh_inbox.is_event.");
  }

  // --- Observation projection ------------------------------------------------
  // StoreInboxMessagesWithObservationsAsync is the redelivery-aware sibling: it stores the
  // same way, then reads back a projection saying which of the just-offered messages the
  // broker had already delivered. The consumer uses that to tell a first delivery from a
  // redelivery without a second round trip. None of it had a test.

  [Test]
  public async Task WithObservations_EmptyArray_ReturnsNoObservationsAsync() {
    var coordinator = _buildCoordinator();

    var observations = await coordinator.StoreInboxMessagesWithObservationsAsync([], partitionCount: 100);

    await Assert.That(observations).IsEmpty();
  }

  [Test]
  public async Task WithObservations_StoresTheMessageAsync() {
    var coordinator = _buildCoordinator();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await coordinator.StoreInboxMessagesWithObservationsAsync(
      [_makeInbox(msgId, streamId)], partitionCount: 100);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var stored = await conn.ExecuteScalarAsync<int>(
      "SELECT count(*) FROM wh_inbox WHERE message_id = @m", new { m = msgId });

    await Assert.That(stored).IsEqualTo(1)
      .Because("the observation overload must persist exactly what the plain overload does");
  }

  [Test]
  public async Task WithObservations_FirstDelivery_ReportsNoRedeliveryAsync() {
    var coordinator = _buildCoordinator();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    var observations = await coordinator.StoreInboxMessagesWithObservationsAsync(
      [_makeInbox(msgId, streamId)], partitionCount: 100);

    await Assert.That(observations).IsNotNull();
    await Assert.That(observations.Any(o => o.ObservationCount > 1)).IsFalse()
      .Because("nothing had been offered before, so a first delivery must not read as a redelivery");
  }

  [Test]
  public async Task WithObservations_SecondOfferOfTheSameMessage_IsObservedAsync() {
    // The point of the overload: offering the same message twice has to be distinguishable
    // from two distinct messages, or a consumer cannot tell a broker redelivery from new work.
    var coordinator = _buildCoordinator();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await coordinator.StoreInboxMessagesWithObservationsAsync(
      [_makeInbox(msgId, streamId)], partitionCount: 100);
    var second = await coordinator.StoreInboxMessagesWithObservationsAsync(
      [_makeInbox(msgId, streamId)], partitionCount: 100);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var rows = await conn.ExecuteScalarAsync<int>(
      "SELECT count(*) FROM wh_inbox WHERE message_id = @m", new { m = msgId });

    await Assert.That(rows).IsEqualTo(1)
      .Because("the store is idempotent on message_id — a redelivery updates rather than duplicates");
    await Assert.That(second).IsNotNull();
  }

  [Test]
  public async Task WithObservations_SeveralMessages_AreAllStoredAsync() {
    var coordinator = _buildCoordinator();
    var messages = new InboxMessage[5];
    var ids = new Guid[5];
    for (var i = 0; i < 5; i++) {
      ids[i] = (Guid)TrackedGuid.NewMedo();
      messages[i] = _makeInbox(ids[i], (Guid)TrackedGuid.NewMedo());
    }

    await coordinator.StoreInboxMessagesWithObservationsAsync(messages, partitionCount: 100);

    using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var stored = await conn.ExecuteScalarAsync<int>(
      "SELECT count(*) FROM wh_inbox WHERE message_id = ANY(@ids)", new { ids });

    await Assert.That(stored).IsEqualTo(5);
  }
}
