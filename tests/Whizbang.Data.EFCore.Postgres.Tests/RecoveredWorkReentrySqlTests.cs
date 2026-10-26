// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A recovered outbox or perspective dead letter re-enters through its own arrival path, <c>store_outbox_messages</c>
/// or <c>store_perspective_events</c>, differing only by naming the dead letter it came from (#1722).
/// </summary>
/// <remarks>
/// Recovery re-inserted both with column lists of its own that had drifted from the stores: an outbox row that was not
/// an event, had no scope or flags, the envelope type <c>recovered</c>, partition 0 and background priority, and a
/// perspective work row with partition 0, no rewind check and background priority; and it marked the dead letter
/// Recovered before anything ran. The dead letter now keeps every field the store needs, the store applies every
/// arrival rule, and the dead letter reads Recovered only once the message is published or applied.
/// </remarks>
/// <docs>operations/dead-letter-queue/recovery</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/239_RecoveredWorkReentersThroughItsStore.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RecoveredWorkReentrySqlTests : EFCoreTestBase {
  private const string TOPIC = "recovered-work-topic";
  private const string EVENT_TYPE = "Upstream.Contracts.OrderShipped, Upstream.Contracts";
  private const string EPHEMERAL_TYPE = "Upstream.Contracts.CourierMoved, Upstream.Contracts";
  private const string PERSPECTIVE = "ShippedOrderPerspective";
  private const string TENANT = "tenant-recovered-work";
  private const int PARTITIONS = 10000;
  private const int REWIND_REQUIRED = 32;

  // ── outbox ──────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task ARecoveredOutboxMessage_IsStoredInTheShapeItWasStoredInAsync() {
    var id = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    await _storeOutboxAsync(_event(id, (Guid)TrackedGuid.New()));
    var stored = await _outboxShapeAsync(connection, id);
    var deadLetterId = await _moveAsync(connection, "wh_outbox", id);

    await Assert.That(await _recoverAsync(connection, deadLetterId)).IsTrue();

    await Assert.That(await _outboxShapeAsync(connection, id)).IsEqualTo(stored)
      .Because("everything but the arrival time and the lease is what the message was stored with");
    await Assert.That((await _deadLetterAsync(connection, deadLetterId)).Status).IsEqualTo((int)DeadLetterRecoveryStatus.Recovering)
      .Because("the store claims the dead letter, which reads Recovered only once the message is published");
  }

  [Test]
  public async Task AnOutboxEventRefusedBeforeItWasAppended_IsAppendedWhenRecovered_WithItsScopeAndFlagsAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    // The stream already holds an ephemeral event, so the store refuses the Sourced one and dead-letters it (WHIZ133).
    var ephemeral = (Guid)TrackedGuid.New();
    await _storeOutboxAsync(_event(ephemeral, streamId) with { MessageType = EPHEMERAL_TYPE, Flags = EventFlags.Ephemeral });
    await _storeOutboxAsync(_event(id, streamId));
    var deadLetterId = await _scalarAsync<Guid>(connection,
      "SELECT dead_letter_id FROM wh_dead_letters WHERE source_table = 'wh_outbox' AND source_id = @id", id);
    await Assert.That(await _eventStreamAsync(connection, id)).IsNull().Because("the refused event was never appended");
    // The conflict is resolved: the ephemeral events move to a stream of their own.
    await _executeAsync(connection, "DELETE FROM wh_event_store WHERE event_id = @id", ephemeral);

    await Assert.That(await _recoverAsync(connection, deadLetterId)).IsTrue();

    await Assert.That(await _eventStreamAsync(connection, id)).IsEqualTo(streamId)
      .Because("the recovered message is the event it was stored as, so the store appends it to its stream");
    await Assert.That(await _scalarAsync<int>(connection, "SELECT flags FROM wh_event_store WHERE event_id = @id", id))
      .IsEqualTo((int)EventFlags.AtMostOnce)
      .Because("the flags the event was stored with are kept by its dead letter");
    await Assert.That(await _scalarAsync<string>(connection, "SELECT scope ->> 't' FROM wh_event_store WHERE event_id = @id", id))
      .IsEqualTo(TENANT)
      .Because("the scope the event was stored with is kept by its dead letter");
  }

  [Test]
  public async Task ARecovery_OfAnOutboxMessageStillInTheOutbox_LeavesItsDeadLetterDueAsync() {
    var id = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    var message = _event(id, (Guid)TrackedGuid.New());
    await _storeOutboxAsync(message);
    var deadLetterId = await _moveAsync(connection, "wh_outbox", id);
    // The producer stores the message again (a republish of the same id).
    await _storeOutboxAsync(message);

    await Assert.That(await _recoverAsync(connection, deadLetterId)).IsFalse()
      .Because("the message the outbox still holds is not replaced by its recovery");
    await Assert.That((await _deadLetterAsync(connection, deadLetterId)).Status).IsEqualTo((int)DeadLetterRecoveryStatus.Pending);
    await Assert.That(await _scalarAsync<long>(connection, "SELECT COUNT(*) FROM wh_outbox WHERE message_id = @id", id)).IsEqualTo(1L);
  }

  [Test]
  public async Task ARecoveredOutboxMessage_DeadLetteredAgain_IsHeldForReview_NamingTheNewDeadLetterAsync() {
    var id = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    await _storeOutboxAsync(_event(id, (Guid)TrackedGuid.New()));
    var deadLetterId = await _moveAsync(connection, "wh_outbox", id);
    await Assert.That(await _recoverAsync(connection, deadLetterId)).IsTrue();

    var again = await _moveAsync(connection, "wh_outbox", id);

    var (status, recoveredAt, notes, disposition) = await _deadLetterAsync(connection, deadLetterId);
    await Assert.That(status).IsEqualTo((int)DeadLetterRecoveryStatus.HoldForReview)
      .Because("the recovered message failed again, so its recovery did not complete");
    await Assert.That(recoveredAt).IsNull();
    await Assert.That(notes).Contains(again.ToString()).Because("the row records which dead letter holds the message now");
    await Assert.That(disposition).IsEqualTo((int)DeadLetterDisposition.HoldIndefinitely)
      .Because("a cohort release must not re-drive the message while the new dead letter holds it");
  }

  // ── perspective ─────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task ARecoveredPerspectiveWorkRow_IsStoredInTheShapeTheEventStoreChainStoresItInAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    await _associatePerspectiveAsync(connection);
    await _storeOutboxAsync(_event(first, streamId), _event(second, streamId));
    var deadLetterId = await _moveAsync(connection, "wh_perspective_events", await _workIdAsync(connection, first));

    await Assert.That(await _recoverAsync(connection, deadLetterId)).IsTrue();

    await Assert.That(await _perspectiveShapeAsync(connection, first)).IsEqualTo(await _perspectiveShapeAsync(connection, second))
      .Because("everything but the event, its work id, its arrival time and its lease is the chain's");
    await Assert.That((await _deadLetterAsync(connection, deadLetterId)).Status).IsEqualTo((int)DeadLetterRecoveryStatus.Recovering)
      .Because("the store claims the dead letter, which reads Recovered only once the event is applied");
  }

  [Test]
  public async Task ARecoveredPerspectiveEvent_BehindItsCursor_FlagsTheCursorForRewindAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    await _associatePerspectiveAsync(connection);
    await _storeOutboxAsync(_event(first, streamId), _event(second, streamId));
    var deadLetterId = await _moveAsync(connection, "wh_perspective_events", await _workIdAsync(connection, first));
    // The perspective went on to apply the later event while the first was dead-lettered.
    await _executeAsync(connection, "DELETE FROM wh_perspective_events WHERE event_id = @id", second);
    await using (var cursor = connection.CreateCommand()) {
      cursor.CommandText = """
        INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at)
        VALUES (@stream, @name, @last, 0, NOW())
        """;
      cursor.Parameters.AddWithValue("stream", streamId);
      cursor.Parameters.AddWithValue("name", PERSPECTIVE);
      cursor.Parameters.AddWithValue("last", second);
      await cursor.ExecuteNonQueryAsync();
    }

    await Assert.That(await _recoverAsync(connection, deadLetterId)).IsTrue();

    await Assert.That(await _scalarAsync<int>(connection,
      "SELECT status & 32 FROM wh_perspective_cursors WHERE stream_id = @id", streamId)).IsEqualTo(REWIND_REQUIRED)
      .Because("an event behind the cursor is applied by replaying the stream in order, as the chain arranges for a late event");
    await Assert.That(await _scalarAsync<Guid>(connection,
      "SELECT rewind_trigger_event_id FROM wh_perspective_cursors WHERE stream_id = @id", streamId)).IsEqualTo(first);
  }

  [Test]
  public async Task ARecoveredPerspectiveEvent_NoLongerInTheEventStore_IsHeldForReview_AndNotReEnteredAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var first = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    await _associatePerspectiveAsync(connection);
    await _storeOutboxAsync(_event(first, streamId));
    var deadLetterId = await _moveAsync(connection, "wh_perspective_events", await _workIdAsync(connection, first));
    // The stream was purged while the work row was dead-lettered.
    await _executeAsync(connection, "DELETE FROM wh_event_store WHERE event_id = @id", first);

    await Assert.That(await _recoverAsync(connection, deadLetterId)).IsFalse();

    await Assert.That(await _scalarAsync<long>(connection,
      "SELECT COUNT(*) FROM wh_perspective_events WHERE event_id = @id", first)).IsEqualTo(0L)
      .Because("there is no event to apply");
    var (status, recoveredAt, notes, _) = await _deadLetterAsync(connection, deadLetterId);
    await Assert.That(status).IsEqualTo((int)DeadLetterRecoveryStatus.HoldForReview)
      .Because("re-driving it can never apply anything, so an operator decides");
    await Assert.That(recoveredAt).IsNull();
    await Assert.That(notes).Contains("no longer in the event store");
  }

  [Test]
  public async Task ARecoveredPerspectiveWorkRow_DeadLetteredAgain_IsHeldForReview_NamingTheNewDeadLetterAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var first = (Guid)TrackedGuid.New();
    await using var connection = await _openAsync();
    await _associatePerspectiveAsync(connection);
    await _storeOutboxAsync(_event(first, streamId));
    var workId = await _workIdAsync(connection, first);
    var deadLetterId = await _moveAsync(connection, "wh_perspective_events", workId);
    await Assert.That(await _recoverAsync(connection, deadLetterId)).IsTrue();

    var again = await _moveAsync(connection, "wh_perspective_events", await _workIdAsync(connection, first));

    var (status, recoveredAt, notes, disposition) = await _deadLetterAsync(connection, deadLetterId);
    await Assert.That(status).IsEqualTo((int)DeadLetterRecoveryStatus.HoldForReview);
    await Assert.That(recoveredAt).IsNull();
    await Assert.That(notes).Contains(again.ToString());
    await Assert.That(disposition).IsEqualTo((int)DeadLetterDisposition.HoldIndefinitely);
  }

  // ===== Helpers =====

  private static OutboxMessage _event(Guid id, Guid streamId) =>
    CreateTestOutboxMessage(id, TOPIC, streamId, isEvent: true) with {
      MessageType = EVENT_TYPE,
      EnvelopeType = EnvelopeTypeNameHelper.Format(EVENT_TYPE),
      Flags = EventFlags.AtMostOnce,
      Scope = new PerspectiveScope { TenantId = TENANT },
      Priority = 120,
    };

  private async Task _storeOutboxAsync(params OutboxMessage[] messages) {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.StoreOutboxMessagesAsync(messages, partitionCount: PARTITIONS);
  }

  /// <summary>The stored outbox row, without the arrival time, the lease and the dead letter it names.</summary>
  private static async Task<string> _outboxShapeAsync(NpgsqlConnection connection, Guid id) =>
    await _scalarAsync<string>(connection, """
      SELECT (to_jsonb(o) - 'created_at' - 'instance_id' - 'lease_expiry' - 'recovered_from_dead_letter_id')::text
      FROM wh_outbox o WHERE o.message_id = @id
      """, id);

  /// <summary>The perspective work row of an event, without what names the event, its work, its arrival or its lease.</summary>
  private static async Task<string> _perspectiveShapeAsync(NpgsqlConnection connection, Guid eventId) =>
    await _scalarAsync<string>(connection, """
      SELECT (to_jsonb(pe) - 'event_work_id' - 'event_id' - 'created_at' - 'instance_id' - 'lease_expiry'
              - 'stream_version' - 'recovered_from_dead_letter_id')::text
      FROM wh_perspective_events pe WHERE pe.event_id = @id
      """, eventId);

  private static async Task<Guid> _workIdAsync(NpgsqlConnection connection, Guid eventId) =>
    await _scalarAsync<Guid>(connection,
      "SELECT event_work_id FROM wh_perspective_events WHERE event_id = @id AND perspective_name = 'ShippedOrderPerspective'", eventId);

  private static async Task<Guid?> _eventStreamAsync(NpgsqlConnection connection, Guid eventId) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT stream_id FROM wh_event_store WHERE event_id = @id";
    command.Parameters.AddWithValue("id", eventId);
    return await command.ExecuteScalarAsync() as Guid?;
  }

  private static async Task _associatePerspectiveAsync(NpgsqlConnection connection) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test', normalize_event_type(@t), NOW(), NOW())
      """;
    command.Parameters.AddWithValue("t", EVENT_TYPE);
    command.Parameters.AddWithValue("p", PERSPECTIVE);
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>Moves a row into dead-letter custody as the workers' attempts gates do.</summary>
  private static async Task<Guid> _moveAsync(NpgsqlConnection connection, string sourceTable, Guid sourceId) {
    var deadLetterId = (Guid)TrackedGuid.New();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT move_to_dead_letters(@dl, @source, @id, @reason, 'attempts exceeded', @inst, 'test/1')";
    command.Parameters.AddWithValue("dl", deadLetterId);
    command.Parameters.AddWithValue("source", sourceTable);
    command.Parameters.AddWithValue("id", sourceId);
    command.Parameters.AddWithValue("reason", (int)MessageFailureReason.MaxAttemptsExceeded);
    command.Parameters.AddWithValue("inst", Guid.CreateVersion7());
    await Assert.That(await command.ExecuteScalarAsync()).IsEqualTo(deadLetterId);
    return deadLetterId;
  }

  private static async Task<bool> _recoverAsync(NpgsqlConnection connection, Guid deadLetterId) =>
    await _scalarAsync<bool>(connection, "SELECT recover_dead_letter(@id)", deadLetterId);

  private static async Task<(int Status, DateTimeOffset? RecoveredAt, string Notes, int Disposition)> _deadLetterAsync(
      NpgsqlConnection connection, Guid deadLetterId) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT recovery_status, recovered_at, COALESCE(operator_notes, ''), COALESCE(operator_disposition, 0)
      FROM wh_dead_letters WHERE dead_letter_id = @id
      """;
    command.Parameters.AddWithValue("id", deadLetterId);
    await using var reader = await command.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    return (reader.GetInt32(0),
      await reader.IsDBNullAsync(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
      reader.GetString(2),
      reader.GetInt32(3));
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
