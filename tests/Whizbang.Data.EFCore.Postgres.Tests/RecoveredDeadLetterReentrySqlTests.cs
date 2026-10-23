// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A recovered inbox or broker dead letter re-enters through <c>store_inbox_messages</c>, the arrival path every
/// delivery takes, and its dead letter is settled when the message completes (#1664).
/// </summary>
/// <remarks>
/// Recovery used to write the inbox row with INSERTs of its own, which drifted from a delivery (not an event, no
/// stream) and lost every recovered event, and it marked the dead letter Recovered before anything ran. These tests
/// hold the three differences a recovery is allowed (it claims its dead letter, it is not dropped as a duplicate,
/// its row names the dead letter), that everything else is the delivery's, and how the completion settles it.
/// </remarks>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/231_RecoveredEventsReachTheEventStore.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RecoveredDeadLetterReentrySqlTests : EFCoreTestBase {
  private const string MESSAGE_TYPE = "Upstream.Contracts.OrderPlaced, Upstream.Contracts";
  private const int PENDING = 0;
  private const int RECOVERING = 1;
  private const int HOLD_FOR_REVIEW = 2;
  private const int RECOVERED = 3;
  private const int HOLD_INDEFINITELY = 2;
  private const int STORED = 1;
  private const int EVENT_STORED = 2;
  private const int PUBLISHED = 4;

  [Test]
  public async Task ARecoveredMessage_IsStoredInTheSameWorkShapeAsADeliveryOfTheSameEnvelopeAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var delivered = (Guid)TrackedGuid.New();
    var recovered = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    var deadLetterId = await _insertDeadLetterAsync(connection, "broker", recovered);

    await _storeAsync(_message(delivered, streamId, isEvent: true));
    await _storeAsync(_message(recovered, streamId, isEvent: true) with { RecoveredFromDeadLetterId = deadLetterId });

    await Assert.That(await _workShapeAsync(connection, recovered)).IsEqualTo(await _workShapeAsync(connection, delivered))
      .Because("everything but the message id, the arrival time and the dead letter it names is the delivery's");
    await Assert.That(await _recoveredFromAsync(connection, recovered)).IsEqualTo(deadLetterId);
    await Assert.That(await _recoveredFromAsync(connection, delivered)).IsNull();
    await Assert.That((await _deadLetterAsync(connection, deadLetterId)).Status).IsEqualTo(RECOVERING)
      .Because("the store claims the dead letter, which reads Recovered only once the message is processed");
  }

  [Test]
  public async Task ARecoveredMessage_AlreadySeen_IsStored_AndItsObservationCountedAsync() {
    var messageId = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    var deadLetterId = await _insertDeadLetterAsync(connection, "broker", messageId);
    await _executeAsync(connection,
      "INSERT INTO wh_message_deduplication (message_id, first_seen_at, observation_count) VALUES (@id, NOW(), 1)", messageId);

    await _storeAsync(_message(messageId, (Guid)TrackedGuid.New(), isEvent: true) with { RecoveredFromDeadLetterId = deadLetterId });

    await Assert.That(await _inboxRowExistsAsync(connection, messageId)).IsTrue()
      .Because("a recovered id was usually seen before, and recovery must not be dropped as its duplicate");
    await Assert.That(await _scalarAsync<int>(connection,
      "SELECT observation_count FROM wh_message_deduplication WHERE message_id = @id", messageId)).IsEqualTo(2)
      .Because("the re-entry still counts as an observation, so poison bounding keeps working");
  }

  [Test]
  public async Task ARecovery_OfADeadLetterAlreadyClaimed_StoresNothingAsync() {
    var messageId = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    var deadLetterId = await _insertDeadLetterAsync(connection, "broker", messageId, status: RECOVERED);

    await _storeAsync(_message(messageId, (Guid)TrackedGuid.New(), isEvent: true) with { RecoveredFromDeadLetterId = deadLetterId });

    await Assert.That(await _inboxRowExistsAsync(connection, messageId)).IsFalse()
      .Because("a dead letter another recovery claimed, or one already settled, is not re-entered twice");
    await Assert.That((await _deadLetterAsync(connection, deadLetterId)).Status).IsEqualTo(RECOVERED);
  }

  [Test]
  public async Task ARecovery_OfAMessageStillInTheInbox_LeavesItsDeadLetterDueAsync() {
    var messageId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    await _storeAsync(_message(messageId, streamId, isEvent: true));
    var deadLetterId = await _insertDeadLetterAsync(connection, "wh_inbox", messageId);

    await _storeAsync(_message(messageId, streamId, isEvent: true) with { RecoveredFromDeadLetterId = deadLetterId });

    await Assert.That(await _recoveredFromAsync(connection, messageId)).IsNull()
      .Because("the message the inbox still holds is not replaced by its recovery");
    await Assert.That((await _deadLetterAsync(connection, deadLetterId)).Status).IsEqualTo(PENDING);
  }

  [Test]
  public async Task ARecoveredMessage_CompletedUnprocessedByAGate_IsHeldForReview_WithTheOutcomeAsync() {
    var messageId = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    var deadLetterId = await _insertDeadLetterAsync(connection, "wh_inbox", messageId);
    await _storeAsync(_message(messageId, (Guid)TrackedGuid.New(), isEvent: false) with { RecoveredFromDeadLetterId = deadLetterId });

    // The inbox dispatch's discard gates complete a row as Published, never as EventStored.
    await _completeAsync(connection, messageId, STORED | PUBLISHED);

    var (status, recoveredAt, notes, _) = await _deadLetterAsync(connection, deadLetterId);
    await Assert.That(status).IsEqualTo(HOLD_FOR_REVIEW)
      .Because("a message a gate discarded was not processed, so its dead letter must not read Recovered");
    await Assert.That(recoveredAt).IsNull();
    await Assert.That(notes).Contains("completed without being processed");
  }

  [Test]
  public async Task ARecoveredCommand_CompletedAsProcessed_IsRecoveredAsync() {
    var messageId = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    var deadLetterId = await _insertDeadLetterAsync(connection, "wh_inbox", messageId);
    await _storeAsync(_message(messageId, (Guid)TrackedGuid.New(), isEvent: false) with { RecoveredFromDeadLetterId = deadLetterId });

    await _completeAsync(connection, messageId, EVENT_STORED);
    // A repeated completion settles nothing twice.
    await _completeAsync(connection, messageId, STORED | PUBLISHED);

    var (status, recoveredAt, _, generations) = await _deadLetterAsync(connection, deadLetterId);
    await Assert.That(status).IsEqualTo(RECOVERED);
    await Assert.That(recoveredAt).IsNotNull();
    await Assert.That(generations).Contains("test/1");
  }

  [Test]
  public async Task ARecoveredEvent_AlreadyAppended_CompletedByAGateAfterTheCopy_IsRecoveredAsync() {
    var messageId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    var deadLetterId = await _insertDeadLetterAsync(connection, "broker", messageId);
    await _storeAsync(_message(messageId, streamId, isEvent: true) with { RecoveredFromDeadLetterId = deadLetterId });
    await _executeAsync(connection,
      "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at) "
      + "SELECT message_id, stream_id, stream_id, 'Upstream', message_type, 1, NOW() FROM wh_inbox WHERE message_id = @id", messageId);

    // A gate after the copy declines only the receptors: the event is already appended.
    await _completeAsync(connection, messageId, STORED | PUBLISHED);

    await Assert.That((await _deadLetterAsync(connection, deadLetterId)).Status).IsEqualTo(RECOVERED);
  }

  [Test]
  public async Task ARecoveredMessage_DeadLetteredAgain_IsHeldForReview_NamingTheNewDeadLetterAsync() {
    var messageId = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    var deadLetterId = await _insertDeadLetterAsync(connection, "broker", messageId);
    await _storeAsync(_message(messageId, (Guid)TrackedGuid.New(), isEvent: false) with { RecoveredFromDeadLetterId = deadLetterId });

    var again = (Guid)TrackedGuid.New();
    await using (var move = connection.CreateCommand()) {
      move.CommandText = "SELECT move_to_dead_letters(@dl, 'wh_inbox', @id, 3, 'attempts exceeded', @inst, 'test/1')";
      move.Parameters.AddWithValue("dl", again);
      move.Parameters.AddWithValue("id", messageId);
      move.Parameters.AddWithValue("inst", Guid.CreateVersion7());
      await move.ExecuteScalarAsync();
    }

    var (status, recoveredAt, notes, _) = await _deadLetterAsync(connection, deadLetterId);
    await Assert.That(status).IsEqualTo(HOLD_FOR_REVIEW);
    await Assert.That(recoveredAt).IsNull();
    await Assert.That(notes).Contains(again.ToString())
      .Because("the row records which dead letter holds the message's custody now");
    await Assert.That(await _scalarAsync<int>(connection,
      "SELECT operator_disposition FROM wh_dead_letters WHERE dead_letter_id = @id", deadLetterId)).IsEqualTo(HOLD_INDEFINITELY)
      .Because("a cohort release must not re-drive the message while the new dead letter holds it");
  }

  [Test]
  public async Task AFirstDeadLetter_OrAnotherMessages_LeavesARecoveredRowAloneAsync() {
    var settled = (Guid)TrackedGuid.New();
    var other = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    var settledDeadLetter = await _insertDeadLetterAsync(connection, "broker", settled, status: RECOVERED);
    var outboxDeadLetter = await _insertDeadLetterAsync(connection, "wh_outbox", other, status: RECOVERED);

    // Another message's first dead letter from the inbox.
    _ = await _insertDeadLetterAsync(connection, "wh_inbox", (Guid)TrackedGuid.New());
    // The same message dead-lettered from the outbox: not an inbox re-delivery.
    _ = await _insertDeadLetterAsync(connection, "wh_outbox", settled);
    // An inbox dead letter of a message whose earlier row is the outbox's.
    _ = await _insertDeadLetterAsync(connection, "wh_inbox", other);

    await Assert.That((await _deadLetterAsync(connection, settledDeadLetter)).Status).IsEqualTo(RECOVERED);
    await Assert.That((await _deadLetterAsync(connection, outboxDeadLetter)).Status).IsEqualTo(RECOVERED);
  }

  [Test]
  public async Task RecoverDeadLetter_OfAnInboxOrBrokerRow_LeavesItForReentryAsync() {
    var messageId = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    var deadLetterId = await _insertDeadLetterAsync(connection, "broker", messageId);

    var result = await _scalarAsync<bool>(connection, "SELECT recover_dead_letter(@id)", deadLetterId);

    await Assert.That(result).IsFalse();
    await Assert.That(await _inboxRowExistsAsync(connection, messageId)).IsFalse()
      .Because("recover_dead_letter has no inbox writes of its own any more");
    await Assert.That((await _deadLetterAsync(connection, deadLetterId)).Status).IsEqualTo(PENDING)
      .Because("the row stays due for the recovery that re-enters it");
  }

  [Test]
  public async Task ReadRedeliveryAsync_ReadsAnInboxOrBrokerRow_AndNothingElseAsync() {
    var messageId = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    var broker = await _insertDeadLetterAsync(connection, "broker", messageId);
    var outbox = await _insertDeadLetterAsync(connection, "wh_outbox", (Guid)TrackedGuid.New());
    await using var dbContext = CreateDbContext();
    var service = new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(dbContext);

    var read = await service.ReadRedeliveryAsync(broker);
    var notInbox = await service.ReadRedeliveryAsync(outbox);
    var missing = await service.ReadRedeliveryAsync(Guid.CreateVersion7());

    await Assert.That(read).IsNotNull();
    await Assert.That(read!.DeadLetterId).IsEqualTo(broker);
    await Assert.That(read.SourceTable).IsEqualTo("broker");
    await Assert.That(read.SourceId).IsEqualTo(messageId);
    await Assert.That(read.MessageType).IsEqualTo(MESSAGE_TYPE);
    using var stored = JsonDocument.Parse(read.EnvelopeJson);
    await Assert.That(stored.RootElement.GetProperty("id").GetGuid()).IsEqualTo(messageId)
      .Because("the stored envelope is read back as it was kept");
    await Assert.That(notInbox).IsNull();
    await Assert.That(missing).IsNull();
  }

  // ===== Helpers =====

  private static InboxMessage _message(Guid messageId, Guid streamId, bool isEvent) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("""{"reference":"R-1"}""").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "OrderPlacedHandler",
      Envelope = envelope,
      EnvelopeType = EnvelopeTypeNameHelper.Format(MESSAGE_TYPE),
      StreamId = streamId,
      IsEvent = isEvent,
      Flags = EventFlags.None,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
      MessageType = MESSAGE_TYPE,
      SourceServiceId = Guid.Parse("0198f5d2-0000-7000-8000-000000000001"),
      SourceCommitSequence = 42,
      Priority = 150,
    };
  }

  private async Task _storeAsync(InboxMessage message) {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.StoreInboxMessagesAsync([message], partitionCount: 100);
  }

  private static async Task<Guid> _insertDeadLetterAsync(NpgsqlConnection connection, string sourceTable, Guid messageId, int status = PENDING) {
    var deadLetterId = (Guid)TrackedGuid.New();
    await using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO wh_dead_letters
        (dead_letter_id, source_table, source_id, message_type, envelope, failure_reason, attempts_when_dlq,
         generation, recovery_status, recovered_at)
      VALUES (@dl, @source, @id, @type, jsonb_build_object('event_data', jsonb_build_object('id', @id), 'metadata', '{}'::jsonb),
              17, 1, 'test/1', @status, CASE WHEN @status = 3 THEN NOW() END)
      """;
    command.Parameters.AddWithValue("dl", deadLetterId);
    command.Parameters.AddWithValue("source", sourceTable);
    command.Parameters.AddWithValue("id", messageId);
    command.Parameters.AddWithValue("type", MESSAGE_TYPE);
    command.Parameters.AddWithValue(nameof(status), status);
    await command.ExecuteNonQueryAsync();
    return deadLetterId;
  }

  /// <summary>Both rows of a stored message, without the columns that name it, date it or name its dead letter.</summary>
  private static async Task<string> _workShapeAsync(NpgsqlConnection connection, Guid messageId) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT ((to_jsonb(i) - 'message_id' - 'received_at' - 'event_data' - 'metadata')
              || jsonb_build_object('state', to_jsonb(ist) - 'message_id' - 'received_at' - 'recovered_from_dead_letter_id'))::text
      FROM wh_inbox i JOIN wh_inbox_state ist ON ist.message_id = i.message_id
      WHERE i.message_id = @id
      """;
    command.Parameters.AddWithValue("id", messageId);
    return (string)(await command.ExecuteScalarAsync())!;
  }

  private static async Task<Guid?> _recoveredFromAsync(NpgsqlConnection connection, Guid messageId) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT recovered_from_dead_letter_id FROM wh_inbox_state WHERE message_id = @id";
    command.Parameters.AddWithValue("id", messageId);
    return await command.ExecuteScalarAsync() as Guid?;
  }

  private static async Task<bool> _inboxRowExistsAsync(NpgsqlConnection connection, Guid messageId) =>
    await _scalarAsync<bool>(connection, "SELECT EXISTS (SELECT 1 FROM wh_inbox WHERE message_id = @id)", messageId);

  private static async Task<(int Status, DateTimeOffset? RecoveredAt, string Notes, string[] Generations)> _deadLetterAsync(
      NpgsqlConnection connection, Guid deadLetterId) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT recovery_status, recovered_at, COALESCE(operator_notes, ''), retried_on_generations
      FROM wh_dead_letters WHERE dead_letter_id = @id
      """;
    command.Parameters.AddWithValue("id", deadLetterId);
    await using var reader = await command.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    return (reader.GetInt32(0),
      await reader.IsDBNullAsync(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
      reader.GetString(2),
      reader.GetFieldValue<string[]>(3));
  }

  private static async Task _completeAsync(NpgsqlConnection connection, Guid messageId, int status) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT * FROM process_inbox_completions(jsonb_build_array(jsonb_build_object('MessageId', @id, 'Status', @status)), NOW())";
    command.Parameters.AddWithValue("id", messageId);
    command.Parameters.AddWithValue(nameof(status), status);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // drain
    }
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection connection, string sql, Guid id) {
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue(nameof(id), id);
    return (T)(await command.ExecuteScalarAsync())!;
  }

  private static async Task _executeAsync(NpgsqlConnection connection, string sql, Guid id) {
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue(nameof(id), id);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }
}
