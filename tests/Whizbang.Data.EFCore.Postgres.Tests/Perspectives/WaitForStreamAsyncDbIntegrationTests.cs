using Npgsql;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Database integration tests verifying the full pipeline from event-store-only outbox message
/// through perspective event creation and sync signaling.
/// </summary>
/// <remarks>
/// Reproduces the issue where <see cref="IPerspectiveSyncAwaiter.WaitForStreamAsync"/> times out
/// because the cascade flush via <c>PublishToOutboxAsync(eventStoreOnly: true)</c> doesn't
/// result in perspective events being processed in time.
/// </remarks>
public class WaitForStreamAsyncDbIntegrationTests : EFCoreTestBase {
  private static Guid NewId() => TrackedGuid.NewMedo();

  /// <summary>
  /// Creates an event-store-only outbox message for testing perspective event creation.
  /// </summary>
  private static OutboxMessage CreateEventStoreOnlyOutboxMessage(Guid messageId, Guid streamId) {
    return new OutboxMessage {
      MessageId = messageId,
      Destination = null!,
      Envelope = CreateTestEnvelope(messageId),
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      },
      EnvelopeType = typeof(MessageEnvelope<System.Text.Json.JsonElement>).AssemblyQualifiedName!,
      MessageType = EVENT_TYPE,
      StreamId = streamId,
      IsEvent = true
    };
  }
  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator = null!;

  private const string PERSPECTIVE_NAME = "TestApp.Domain.ActivityFlow+Projection";
  private const string EVENT_TYPE = "TestApp.Contracts.StartedEvent, TestApp.Contracts";

  [Before(Test)]
  public async Task SetupCoordinatorAsync() {
    await Task.CompletedTask;
    _coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      CreateDbContext(),
      InfrastructureJsonContext.Default.Options
    );
  }

  /// <summary>
  /// Registers a message association so the SQL function knows to create perspective events
  /// when events of the specified type are stored.
  /// </summary>
  private async Task RegisterPerspectiveAssociationAsync(string eventType, string perspectiveName) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await connection.ExecuteAsync(@"
      INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name)
      VALUES (@eventType, 'perspective', @perspectiveName, 'TestService')
      ON CONFLICT DO NOTHING",
      new { eventType, perspectiveName });
  }

  [Test]
  public async Task EventStoreOnlyOutbox_CreatesEventStoreEntry_WhenIsEventTrueAsync() {
    // Arrange
    var instanceId = NewId();
    var streamId = NewId();
    var messageId = NewId();

    var outboxMessage = CreateEventStoreOnlyOutboxMessage(messageId, streamId);

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 1,
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [outboxMessage],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      Flags = WorkBatchOptions.None
    });

    // Assert — event should be in wh_event_store
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var eventCount = await connection.ExecuteScalarAsync<int>(
      "SELECT COUNT(*) FROM wh_event_store WHERE event_id = @messageId", new { messageId });

    await Assert.That(eventCount).IsEqualTo(1)
      .Because("Event-store-only outbox message with is_event=true should be stored in wh_event_store");
  }

  [Test]
  public async Task EventStoreOnlyOutbox_CreatesPerspectiveEvents_WhenAssociationRegisteredAsync() {
    // Arrange
    await RegisterPerspectiveAssociationAsync(EVENT_TYPE, PERSPECTIVE_NAME);

    var instanceId = NewId();
    var streamId = NewId();
    var messageId = NewId();

    var outboxMessage = CreateEventStoreOnlyOutboxMessage(messageId, streamId);

    // Act
    await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 1,
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [outboxMessage],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      Flags = WorkBatchOptions.None
    });

    // Assert — perspective events should be created by Phase 4.6
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var perspectiveCount = await connection.ExecuteScalarAsync<int>(
      "SELECT COUNT(*) FROM wh_perspective_events WHERE stream_id = @streamId AND perspective_name = @perspectiveName",
      new { streamId, perspectiveName = PERSPECTIVE_NAME });

    await Assert.That(perspectiveCount).IsEqualTo(1)
      .Because("Phase 4.6 should auto-create perspective events from the event store entry matching the association");
  }

  [Test]
  public async Task EventStoreOnlyOutbox_PerspectiveEvents_LeasedToSameInstanceAsync() {
    // Arrange
    await RegisterPerspectiveAssociationAsync(EVENT_TYPE, PERSPECTIVE_NAME);

    var instanceId = NewId();
    var streamId = NewId();
    var messageId = NewId();

    var outboxMessage = CreateEventStoreOnlyOutboxMessage(messageId, streamId);

    // Act
    await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 1,
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [outboxMessage],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      Flags = WorkBatchOptions.None
    });

    // Assert — perspective events should be leased to the calling instance
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var leasedInstanceId = await connection.ExecuteScalarAsync<Guid?>(
      "SELECT instance_id FROM wh_perspective_events WHERE stream_id = @streamId AND perspective_name = @perspectiveName",
      new { streamId, perspectiveName = PERSPECTIVE_NAME });

    await Assert.That(leasedInstanceId).IsEqualTo(instanceId)
      .Because("Perspective events should be immediately leased to the instance that created the outbox message");
  }

  [Test]
  public async Task PerspectiveEvents_ReturnedOnSubsequentBatchCall_BySameInstanceAsync() {
    // Arrange — store event and create perspective events
    await RegisterPerspectiveAssociationAsync(EVENT_TYPE, PERSPECTIVE_NAME);

    var instanceId = NewId();
    var streamId = NewId();
    var messageId = NewId();

    var outboxMessage = CreateEventStoreOnlyOutboxMessage(messageId, streamId);

    // First call: store the outbox message (creates perspective events)
    await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 1,
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [outboxMessage],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      Flags = WorkBatchOptions.None
    });

    // Act — second call with same instance (simulates PerspectiveWorker polling)
    var secondBatch = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 1,
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      Flags = WorkBatchOptions.None
    });

    // Assert — perspective work should be returned (same instance can see its own leased work)
    await Assert.That(secondBatch.PerspectiveWork.Count).IsGreaterThanOrEqualTo(1)
      .Because("PerspectiveWorker (same instance) should receive perspective work items on subsequent batch calls");

    // Verify the perspective work matches our event
    var perspectiveWork = secondBatch.PerspectiveWork.FirstOrDefault(pw => pw.StreamId == streamId);
    await Assert.That(perspectiveWork).IsNotNull()
      .Because("Perspective work should include our stream");
  }

  [Test]
  public async Task NullDestinationOutbox_ReturnedAsOutboxWork_ForCompletionAsync() {
    // Arrange
    var instanceId = NewId();
    var streamId = NewId();
    var messageId = NewId();

    var outboxMessage = CreateEventStoreOnlyOutboxMessage(messageId, streamId);

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 1,
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [outboxMessage],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      Flags = WorkBatchOptions.None
    });

    // Assert — null-destination messages should still be returned as outbox work
    var outboxWork = result.OutboxWork.FirstOrDefault(ow => ow.MessageId == messageId);
    await Assert.That(outboxWork).IsNotNull()
      .Because("Null-destination outbox messages should be returned as outbox work for completion tracking");
  }
}
