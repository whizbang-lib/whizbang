using System.Text.Json;
using Dapper;
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
/// Tests for Phase 4.6B out-of-order detection in auto-created perspective events.
/// Verifies that process_work_batch sets RewindRequired flag when perspective events
/// are created for events that the cursor has already advanced past.
///
/// These tests go through the real EFCore work coordinator path (C# → SQL),
/// reproducing the exact code path that runs in production.
///
/// Bug scenario: Runner reads ahead from wh_event_store, advancing the cursor.
/// Later ticks create perspective events for those same events via Phase 4.6.
/// Without Phase 4.6B, these events sit stuck forever in wh_perspective_events.
/// </summary>
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

  [Test]
  public async Task ProcessWorkBatch_WithOutOfOrderEvent_SetsRewindRequiredOnCursorAsync() {
    // Arrange - Register perspective association
    await RegisterMessageAssociationAsync(
      "TestApp.Events.OrderCreatedEvent, TestApp",
      "perspective",
      "OrderListPerspective",
      "TestService");

    var streamId = _idProvider.NewGuid();
    // UUID7: eventId1 < eventId2 < eventId3 (time-ordered)
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();

    // Only pre-insert the cursor's event in event_store (FK constraint requires it).
    // Do NOT pre-insert eventId1 — it must flow through Phase 4.5A to be tracked
    // in v_stored_outbox_events so Phase 4.6 creates the perspective event.
    await InsertEventStoreRowAsync(eventId3, streamId, "TestApp.Events.OrderCreatedEvent, TestApp", "{}");

    // Pre-create cursor that has already advanced to eventId3
    // (simulates runner having read ahead from wh_event_store)
    await InsertPerspectiveCursorAsync(streamId, "OrderListPerspective",
      lastEventId: eventId3, status: 2);

    // Act - Send an event with eventId1 (OLDER than cursor's last_event_id)
    // Goes through real C# → SQL path: EFCoreWorkCoordinator → process_work_batch
    var result = await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchContext(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      Metadata: null,
      OutboxCompletions: [],
      OutboxFailures: [],
      InboxCompletions: [],
      InboxFailures: [],
      ReceptorCompletions: [],
      ReceptorFailures: [],
      PerspectiveCompletions: [],
      PerspectiveFailures: [],
      NewOutboxMessages: [CreateEventOutboxMessage(eventId1, streamId,
        "TestApp.Events.OrderCreatedEvent, TestApp")],
      NewInboxMessages: [],
      RenewOutboxLeaseIds: [],
      RenewInboxLeaseIds: [],
      LeaseSeconds: 300));

    // Assert - Cursor should have RewindRequired flag set (bit 5 = 32)
    var cursor = await GetPerspectiveCursorAsync(streamId, "OrderListPerspective");
    await Assert.That(cursor).IsNotNull();
    await Assert.That(cursor!.Status & 32).IsEqualTo(32)
      .Because("Phase 4.6B should detect out-of-order event and set RewindRequired flag");
    await Assert.That(cursor.RewindTriggerEventId).IsEqualTo(eventId1)
      .Because("rewind_trigger_event_id should be set to the out-of-order event");
  }

  [Test]
  public async Task ProcessWorkBatch_WithInOrderEvent_DoesNotSetRewindRequiredAsync() {
    // Arrange
    await RegisterMessageAssociationAsync(
      "TestApp.Events.OrderCreatedEvent, TestApp",
      "perspective",
      "OrderListPerspective",
      "TestService");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();

    // Only pre-insert cursor's event in event_store (FK constraint)
    await InsertEventStoreRowAsync(eventId1, streamId, "TestApp.Events.OrderCreatedEvent, TestApp", "{}");

    // Cursor at eventId1
    await InsertPerspectiveCursorAsync(streamId, "OrderListPerspective",
      lastEventId: eventId1, status: 2);

    // Act - Send eventId2 (> eventId1, in order)
    await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchContext(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      Metadata: null,
      OutboxCompletions: [],
      OutboxFailures: [],
      InboxCompletions: [],
      InboxFailures: [],
      ReceptorCompletions: [],
      ReceptorFailures: [],
      PerspectiveCompletions: [],
      PerspectiveFailures: [],
      NewOutboxMessages: [CreateEventOutboxMessage(eventId2, streamId,
        "TestApp.Events.OrderCreatedEvent, TestApp")],
      NewInboxMessages: [],
      RenewOutboxLeaseIds: [],
      RenewInboxLeaseIds: [],
      LeaseSeconds: 300));

    // Assert - No rewind for in-order events
    var cursor = await GetPerspectiveCursorAsync(streamId, "OrderListPerspective");
    await Assert.That(cursor!.Status & 32).IsEqualTo(0)
      .Because("In-order events should NOT set RewindRequired");
    await Assert.That(cursor.RewindTriggerEventId).IsNull()
      .Because("rewind_trigger_event_id should remain NULL");
  }

  [Test]
  public async Task ProcessWorkBatch_RealLifeOrchestrationScenario_SetsRewindOnLateEventsAsync() {
    // Arrange - Reproduces the exact production bug:
    // 1. Orchestration starts → first batch creates perspective event → runner reads ahead
    // 2. Fan-out produces many events in later batches
    // 3. Phase 4.6 creates perspective events for late-arriving events
    // 4. These are behind the cursor → should trigger rewind

    await RegisterMessageAssociationAsync(
      "TestApp.Events.OrchestrationStartedEvent, TestApp",
      "perspective",
      "OrchestrationPerspective",
      "TestService");

    await RegisterMessageAssociationAsync(
      "TestApp.Events.ItemProcessedEvent, TestApp",
      "perspective",
      "OrchestrationPerspective",
      "TestService");

    var streamId = _idProvider.NewGuid();

    // Simulate: first batch stores the started event
    var startedEventId = _idProvider.NewGuid();

    // Simulate: fan-out produces 5 item events (UUID7, so all > startedEventId)
    var itemEventIds = Enumerable.Range(0, 5)
      .Select(_ => _idProvider.NewGuid())
      .ToArray();

    // Only pre-insert the LAST event for cursor FK constraint.
    // The early events must flow through Phase 4.5A → Phase 4.6 → Phase 4.6B.
    var lastItemEventId = itemEventIds[^1];
    await InsertEventStoreRowAsync(lastItemEventId, streamId,
      "TestApp.Events.ItemProcessedEvent, TestApp", "{}");

    // Simulate: runner read ahead from event_store and processed everything
    // Cursor is now at the LAST item event
    await InsertPerspectiveCursorAsync(streamId, "OrchestrationPerspective",
      lastEventId: lastItemEventId, status: 2);

    // Act - Later tick: Phase 4.6 would create perspective events for earlier items
    // We simulate by sending the EARLIER item events through process_work_batch
    var lateOutboxMessages = itemEventIds[..3]  // First 3 items (all < cursor)
      .Select(id => CreateEventOutboxMessage(id, streamId,
        "TestApp.Events.ItemProcessedEvent, TestApp"))
      .ToArray();

    await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchContext(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      Metadata: null,
      OutboxCompletions: [],
      OutboxFailures: [],
      InboxCompletions: [],
      InboxFailures: [],
      ReceptorCompletions: [],
      ReceptorFailures: [],
      PerspectiveCompletions: [],
      PerspectiveFailures: [],
      NewOutboxMessages: lateOutboxMessages,
      NewInboxMessages: [],
      RenewOutboxLeaseIds: [],
      RenewInboxLeaseIds: [],
      LeaseSeconds: 300));

    // Assert - Cursor should have RewindRequired flag, trigger at earliest late event
    var cursor = await GetPerspectiveCursorAsync(streamId, "OrchestrationPerspective");
    await Assert.That(cursor!.Status & 32).IsEqualTo(32)
      .Because("Late perspective events from fan-out should trigger rewind");
    await Assert.That(cursor.RewindTriggerEventId).IsEqualTo(itemEventIds[0])
      .Because("rewind_trigger_event_id should be the earliest late event");
  }

  // Helper methods

  private async Task InsertEventStoreRowAsync(
      Guid eventId, Guid streamId, string eventType, string eventData) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var metadata = JsonSerializer.Serialize(new { MessageId = eventId, Hops = Array.Empty<object>() });
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type,
        event_data, metadata, scope, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'TestAggregate', @eventType,
        @eventData::jsonb, @metadata::jsonb, NULL,
        (SELECT COALESCE(MAX(version), 0) + 1 FROM wh_event_store WHERE stream_id = @streamId), NOW())",
      new { eventId, streamId, eventType, eventData, metadata });
  }

  private async Task RegisterMessageAssociationAsync(
      string messageType, string associationType, string targetName, string serviceName) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
      VALUES (@messageType, @associationType, @targetName, @serviceName, NOW(), NOW())",
      new { messageType, associationType, targetName, serviceName });
  }

  private async Task InsertPerspectiveCursorAsync(
      Guid streamId, string perspectiveName,
      Guid? lastEventId = null, short status = 0,
      Guid? rewindTriggerEventId = null) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, rewind_trigger_event_id)
      VALUES (@streamId, @perspectiveName, @lastEventId, @status, @rewindTriggerEventId)",
      new { streamId, perspectiveName, lastEventId, status, rewindTriggerEventId });
  }

  private async Task<PerspectiveCursorRow?> GetPerspectiveCursorAsync(
      Guid streamId, string perspectiveName) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return await connection.QueryFirstOrDefaultAsync<PerspectiveCursorRow>(@"
      SELECT stream_id as StreamId, perspective_name as PerspectiveName,
             last_event_id as LastEventId, status as Status,
             error as Error, rewind_trigger_event_id as RewindTriggerEventId
      FROM wh_perspective_cursors
      WHERE stream_id = @streamId AND perspective_name = @perspectiveName",
      new { streamId, perspectiveName });
  }

  private OutboxMessage CreateEventOutboxMessage(
      Guid eventId, Guid streamId, string eventType) {
    return new OutboxMessage {
      MessageId = eventId,
      Destination = null,  // Events don't have destinations
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(eventId),
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [],
        Payload = JsonDocument.Parse("{}").RootElement
      },
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(eventId),
        Hops = []
      },
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{eventType}]], Whizbang.Core",
      MessageType = eventType,
      StreamId = streamId,
      IsEvent = true
    };
  }

  private sealed record PerspectiveCursorRow(
    Guid StreamId,
    string PerspectiveName,
    Guid? LastEventId,
    short Status,
    string? Error,
    Guid? RewindTriggerEventId);
}
