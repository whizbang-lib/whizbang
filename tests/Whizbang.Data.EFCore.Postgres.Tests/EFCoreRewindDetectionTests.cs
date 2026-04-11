using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for Phase 4.6B out-of-order detection, debounce, and completion cleanup.
/// Goes through the real EFCore work coordinator path (C# → SQL).
/// Uses raw NpgsqlCommand for setup/verification — no Dapper dependency.
/// </summary>
/// <tests>src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql</tests>
/// <tests>src/Whizbang.Data.Postgres/Migrations/005_CreateCompletePerspectiveCheckpointFunction.sql</tests>
public class EFCoreRewindDetectionTests : EFCoreTestBase {
  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _sut = null!;
  private Guid _instanceId;
  private readonly Uuid7IdProvider _idProvider = new();

  [Before(Test)]
  public async Task TestSetupAsync() {
    _instanceId = _idProvider.NewGuid();
    var dbContext = CreateDbContext();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    _sut = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, jsonOptions);
    await Task.CompletedTask;
  }

  #region Phase 4.6B Out-of-Order Detection

  [Test]
  public async Task ProcessWorkBatch_WithOutOfOrderEvent_SetsRewindRequiredOnCursorAsync() {
    // Arrange
    await _registerMessageAssociationAsync(
      "TestApp.Events.OrderCreatedEvent, TestApp", "perspective",
      "OrderListPerspective", "TestService");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();

    await _insertEventStoreRowAsync(eventId3, streamId, "TestApp.Events.OrderCreatedEvent, TestApp", "{}");
    await _insertPerspectiveCursorAsync(streamId, "OrderListPerspective", lastEventId: eventId3, status: 2);

    // Act
    await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchContext(
      _instanceId, "TestService", "test-host", 12345, Metadata: null,
      OutboxCompletions: [], OutboxFailures: [],
      InboxCompletions: [], InboxFailures: [],
      ReceptorCompletions: [], ReceptorFailures: [],
      PerspectiveCompletions: [], PerspectiveFailures: [],
      NewOutboxMessages: [_createEventOutboxMessage(eventId1, streamId, "TestApp.Events.OrderCreatedEvent, TestApp")],
      NewInboxMessages: [], RenewOutboxLeaseIds: [], RenewInboxLeaseIds: [],
      LeaseSeconds: 300));

    // Assert
    var (status, triggerEventId, _, _) = await _getPerspectiveCursorRewindStateAsync(streamId, "OrderListPerspective");
    await Assert.That(status & 32).IsEqualTo(32)
      .Because("Phase 4.6B should detect out-of-order event and set RewindRequired flag");
    await Assert.That(triggerEventId).IsEqualTo(eventId1)
      .Because("rewind_trigger_event_id should be set to the out-of-order event");
  }

  [Test]
  public async Task ProcessWorkBatch_WithInOrderEvent_DoesNotSetRewindRequiredAsync() {
    // Arrange
    await _registerMessageAssociationAsync(
      "TestApp.Events.OrderCreatedEvent, TestApp", "perspective",
      "OrderListPerspective", "TestService");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();

    await _insertEventStoreRowAsync(eventId1, streamId, "TestApp.Events.OrderCreatedEvent, TestApp", "{}");
    await _insertPerspectiveCursorAsync(streamId, "OrderListPerspective", lastEventId: eventId1, status: 2);

    // Act
    await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchContext(
      _instanceId, "TestService", "test-host", 12345, Metadata: null,
      OutboxCompletions: [], OutboxFailures: [],
      InboxCompletions: [], InboxFailures: [],
      ReceptorCompletions: [], ReceptorFailures: [],
      PerspectiveCompletions: [], PerspectiveFailures: [],
      NewOutboxMessages: [_createEventOutboxMessage(eventId2, streamId, "TestApp.Events.OrderCreatedEvent, TestApp")],
      NewInboxMessages: [], RenewOutboxLeaseIds: [], RenewInboxLeaseIds: [],
      LeaseSeconds: 300));

    // Assert
    var (status, triggerEventId, _, _) = await _getPerspectiveCursorRewindStateAsync(streamId, "OrderListPerspective");
    await Assert.That(status & 32).IsEqualTo(0)
      .Because("In-order events should NOT set RewindRequired");
    await Assert.That(triggerEventId).IsNull()
      .Because("rewind_trigger_event_id should remain NULL");
  }

  [Test]
  public async Task ProcessWorkBatch_RealLifeOrchestrationScenario_SetsRewindOnLateEventsAsync() {
    // Arrange
    await _registerMessageAssociationAsync(
      "TestApp.Events.OrchestrationStartedEvent, TestApp", "perspective",
      "OrchestrationPerspective", "TestService");
    await _registerMessageAssociationAsync(
      "TestApp.Events.ItemProcessedEvent, TestApp", "perspective",
      "OrchestrationPerspective", "TestService");

    var streamId = _idProvider.NewGuid();
    var startedEventId = _idProvider.NewGuid();
    var itemEventIds = Enumerable.Range(0, 5).Select(_ => _idProvider.NewGuid()).ToArray();
    var lastItemEventId = itemEventIds[^1];

    await _insertEventStoreRowAsync(lastItemEventId, streamId, "TestApp.Events.ItemProcessedEvent, TestApp", "{}");
    await _insertPerspectiveCursorAsync(streamId, "OrchestrationPerspective", lastEventId: lastItemEventId, status: 2);

    // Act — send 3 earlier items (all < cursor)
    var lateOutboxMessages = itemEventIds[..3]
      .Select(id => _createEventOutboxMessage(id, streamId, "TestApp.Events.ItemProcessedEvent, TestApp"))
      .ToArray();

    await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchContext(
      _instanceId, "TestService", "test-host", 12345, Metadata: null,
      OutboxCompletions: [], OutboxFailures: [],
      InboxCompletions: [], InboxFailures: [],
      ReceptorCompletions: [], ReceptorFailures: [],
      PerspectiveCompletions: [], PerspectiveFailures: [],
      NewOutboxMessages: lateOutboxMessages,
      NewInboxMessages: [], RenewOutboxLeaseIds: [], RenewInboxLeaseIds: [],
      LeaseSeconds: 300));

    // Assert
    var (status, triggerEventId, _, _) = await _getPerspectiveCursorRewindStateAsync(streamId, "OrchestrationPerspective");
    await Assert.That(status & 32).IsEqualTo(32)
      .Because("Late perspective events from fan-out should trigger rewind");
    await Assert.That(triggerEventId).IsEqualTo(itemEventIds[0])
      .Because("rewind_trigger_event_id should be the earliest late event");
  }

  #endregion

  #region Debounce

  [Test]
  public async Task ProcessWorkBatch_Debounce_SlidingWindowHoldsBackEventsAsync() {
    // Arrange
    await _registerMessageAssociationAsync(
      "TestApp.Events.OrderCreatedEvent, TestApp", "perspective",
      "OrderListPerspective", "TestService");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();

    await _insertEventStoreRowAsync(eventId3, streamId, "TestApp.Events.OrderCreatedEvent, TestApp", "{}");
    await _insertPerspectiveCursorAsync(streamId, "OrderListPerspective", lastEventId: eventId3, status: 2);

    // Act 1 — Store late event → flags cursor
    await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchContext(
      _instanceId, "TestService", "test-host", 12345, Metadata: null,
      OutboxCompletions: [], OutboxFailures: [],
      InboxCompletions: [], InboxFailures: [],
      ReceptorCompletions: [], ReceptorFailures: [],
      PerspectiveCompletions: [], PerspectiveFailures: [],
      NewOutboxMessages: [_createEventOutboxMessage(eventId1, streamId, "TestApp.Events.OrderCreatedEvent, TestApp")],
      NewInboxMessages: [], RenewOutboxLeaseIds: [], RenewInboxLeaseIds: [],
      LeaseSeconds: 300));

    // Assert — both timestamps set
    var (status, _, flaggedAt, firstFlaggedAt) = await _getPerspectiveCursorRewindStateAsync(streamId, "OrderListPerspective");
    await Assert.That(status & 32).IsEqualTo(32)
      .Because("RewindRequired should be set");
    await Assert.That(flaggedAt.HasValue).IsTrue()
      .Because("Sliding window edge should be set");
    await Assert.That(firstFlaggedAt.HasValue).IsTrue()
      .Because("Max cap anchor should be set");

    // Act 2 — Next batch within window: zero perspective work
    var result2 = await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchContext(
      _instanceId, "TestService", "test-host", 12345, Metadata: null,
      OutboxCompletions: [], OutboxFailures: [],
      InboxCompletions: [], InboxFailures: [],
      ReceptorCompletions: [], ReceptorFailures: [],
      PerspectiveCompletions: [], PerspectiveFailures: [],
      NewOutboxMessages: [], NewInboxMessages: [],
      RenewOutboxLeaseIds: [], RenewInboxLeaseIds: [],
      LeaseSeconds: 300));

    await Assert.That(result2.PerspectiveWork).Count().IsEqualTo(0)
      .Because("Debounce should hold back perspective events within the sliding window");
  }

  #endregion

  #region Completion Cleanup

  [Test]
  public async Task ProcessWorkBatch_Completion_ClearsAllRewindColumnsAsync() {
    // Arrange
    await _registerMessageAssociationAsync(
      "TestApp.Events.OrderCreatedEvent, TestApp", "perspective",
      "OrderListPerspective", "TestService");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();

    await _insertEventStoreRowAsync(eventId3, streamId, "TestApp.Events.OrderCreatedEvent, TestApp", "{}");
    await _insertPerspectiveCursorAsync(streamId, "OrderListPerspective", lastEventId: eventId3, status: 2);

    // Flag cursor
    await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchContext(
      _instanceId, "TestService", "test-host", 12345, Metadata: null,
      OutboxCompletions: [], OutboxFailures: [],
      InboxCompletions: [], InboxFailures: [],
      ReceptorCompletions: [], ReceptorFailures: [],
      PerspectiveCompletions: [], PerspectiveFailures: [],
      NewOutboxMessages: [_createEventOutboxMessage(eventId1, streamId, "TestApp.Events.OrderCreatedEvent, TestApp")],
      NewInboxMessages: [], RenewOutboxLeaseIds: [], RenewInboxLeaseIds: [],
      LeaseSeconds: 300));

    // Verify flags set
    var (beforeStatus, beforeTrigger, beforeFlagged, beforeFirst) =
      await _getPerspectiveCursorRewindStateAsync(streamId, "OrderListPerspective");
    await Assert.That(beforeTrigger.HasValue).IsTrue();
    await Assert.That(beforeFlagged.HasValue).IsTrue();
    await Assert.That(beforeFirst.HasValue).IsTrue();

    // Act — Complete cursor (simulating successful rewind)
    await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchContext(
      _instanceId, "TestService", "test-host", 12345, Metadata: null,
      OutboxCompletions: [], OutboxFailures: [],
      InboxCompletions: [], InboxFailures: [],
      ReceptorCompletions: [], ReceptorFailures: [],
      PerspectiveCompletions: [new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = "OrderListPerspective",
        LastEventId = eventId3,
        Status = PerspectiveProcessingStatus.Completed
      }],
      PerspectiveFailures: [],
      NewOutboxMessages: [], NewInboxMessages: [],
      RenewOutboxLeaseIds: [], RenewInboxLeaseIds: [],
      LeaseSeconds: 300));

    // Assert — all rewind columns cleared
    var (_, afterTrigger, afterFlagged, afterFirst) =
      await _getPerspectiveCursorRewindStateAsync(streamId, "OrderListPerspective");
    await Assert.That(afterTrigger).IsNull()
      .Because("Completion should clear rewind_trigger_event_id");
    await Assert.That(afterFlagged.HasValue).IsFalse()
      .Because("Completion should clear rewind_flagged_at");
    await Assert.That(afterFirst.HasValue).IsFalse()
      .Because("Completion should clear rewind_first_flagged_at");
  }

  #endregion

  #region Helpers (raw NpgsqlCommand — no Dapper)

  private async Task _insertEventStoreRowAsync(Guid eventId, Guid streamId, string eventType, string eventData) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    var metadata = $$$"""{"MessageId":"{{{eventId}}}","Hops":[]}""";

    await using var cmd = new NpgsqlCommand("""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type,
        event_data, metadata, scope, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'TestAggregate', @eventType,
        @eventData::jsonb, @metadata::jsonb, NULL,
        (SELECT COALESCE(MAX(version), 0) + 1 FROM wh_event_store WHERE stream_id = @streamId), NOW())
      """, connection);

    cmd.Parameters.AddWithValue("eventId", eventId);
    cmd.Parameters.AddWithValue("streamId", streamId);
    cmd.Parameters.AddWithValue("eventType", eventType);
    cmd.Parameters.AddWithValue("eventData", eventData);
    cmd.Parameters.AddWithValue("metadata", metadata);

    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _registerMessageAssociationAsync(
      string messageType, string associationType, string targetName, string serviceName) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await using var cmd = new NpgsqlCommand("""
      INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
      VALUES (@messageType, @associationType, @targetName, @serviceName, NOW(), NOW())
      """, connection);

    cmd.Parameters.AddWithValue("messageType", messageType);
    cmd.Parameters.AddWithValue("associationType", associationType);
    cmd.Parameters.AddWithValue("targetName", targetName);
    cmd.Parameters.AddWithValue("serviceName", serviceName);

    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _insertPerspectiveCursorAsync(
      Guid streamId, string perspectiveName, Guid? lastEventId = null, short status = 0) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await using var cmd = new NpgsqlCommand("""
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status)
      VALUES (@streamId, @perspectiveName, @lastEventId, @status)
      """, connection);

    cmd.Parameters.AddWithValue("streamId", streamId);
    cmd.Parameters.AddWithValue("perspectiveName", perspectiveName);
    cmd.Parameters.AddWithValue("lastEventId", (object?)lastEventId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("status", status);

    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<(short Status, Guid? TriggerEventId, DateTime? FlaggedAt, DateTime? FirstFlaggedAt)>
      _getPerspectiveCursorRewindStateAsync(Guid streamId, string perspectiveName) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await using var cmd = new NpgsqlCommand("""
      SELECT status, rewind_trigger_event_id, rewind_flagged_at, rewind_first_flagged_at
      FROM wh_perspective_cursors
      WHERE stream_id = @streamId AND perspective_name = @perspectiveName
      """, connection);

    cmd.Parameters.AddWithValue("streamId", streamId);
    cmd.Parameters.AddWithValue("perspectiveName", perspectiveName);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return (0, null, null, null);
    }

    return (
      reader.GetInt16(0),
      reader.IsDBNull(1) ? null : reader.GetGuid(1),
      reader.IsDBNull(2) ? null : reader.GetDateTime(2),
      reader.IsDBNull(3) ? null : reader.GetDateTime(3));
  }

  private static OutboxMessage _createEventOutboxMessage(Guid eventId, Guid streamId, string eventType) {
    return new OutboxMessage {
      MessageId = eventId,
      Destination = null,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(eventId),
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [],
        Payload = JsonDocument.Parse("{}").RootElement
      },
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(eventId), Hops = [] },
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{eventType}]], Whizbang.Core",
      MessageType = eventType,
      StreamId = streamId,
      IsEvent = true
    };
  }

  #endregion
}
