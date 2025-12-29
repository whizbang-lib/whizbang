using Dapper;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Tests for individual PostgreSQL functions (migrations 009-012).
/// Tests the decomposed functions that will eventually replace process_work_batch.
/// Uses UUIDv7 for all IDs to ensure proper time-ordered database indexing.
/// </summary>
public class PostgresFunctionTests : PostgresTestBase {
  private readonly Uuid7IdProvider _idProvider = new Uuid7IdProvider();

  [Test]
  public async Task RegisterInstanceHeartbeat_NewInstance_InsertsSuccessfullyAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var serviceName = "TestService";
    var hostName = "test-host";
    var processId = 12345;
    var now = DateTimeOffset.UtcNow;

    // Act
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var leaseExpiry = now.AddMinutes(5);
    await connection.ExecuteAsync(@"
      SELECT register_instance_heartbeat(
        @instanceId, @serviceName, @hostName, @processId, NULL, @now, @leaseExpiry
      )",
      new { instanceId, serviceName, hostName, processId, now, leaseExpiry });

    // Assert
    var instance = await connection.QuerySingleOrDefaultAsync<ServiceInstanceRow>(@"
      SELECT instance_id, service_name, host_name, process_id, last_heartbeat_at
      FROM wh_service_instances WHERE instance_id = @instanceId",
      new { instanceId });

    await Assert.That(instance).IsNotNull();
    await Assert.That(instance!.instance_id).IsEqualTo(instanceId);
    await Assert.That(instance.service_name).IsEqualTo(serviceName);
    await Assert.That(instance.host_name).IsEqualTo(hostName);
    await Assert.That(instance.process_id).IsEqualTo(processId);
    await Assert.That(instance.last_heartbeat_at).IsGreaterThanOrEqualTo(now.AddSeconds(-1));
  }

  [Test]
  public async Task RegisterInstanceHeartbeat_ExistingInstance_UpdatesHeartbeatAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var serviceName = "TestService";
    var hostName = "test-host";
    var processId = 12345;
    var originalTime = DateTimeOffset.UtcNow.AddMinutes(-5);
    var updatedTime = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert original instance
    var originalLeaseExpiry = originalTime.AddMinutes(5);
    await connection.ExecuteAsync(@"
      SELECT register_instance_heartbeat(
        @instanceId, @serviceName, @hostName, @processId, NULL, @originalTime, @originalLeaseExpiry
      )",
      new { instanceId, serviceName, hostName, processId, originalTime, originalLeaseExpiry });

    // Act - Update heartbeat
    var updatedLeaseExpiry = updatedTime.AddMinutes(5);
    await connection.ExecuteAsync(@"
      SELECT register_instance_heartbeat(
        @instanceId, @serviceName, @hostName, @processId, NULL, @updatedTime, @updatedLeaseExpiry
      )",
      new { instanceId, serviceName, hostName, processId, updatedTime, updatedLeaseExpiry });

    // Assert
    var instance = await connection.QuerySingleOrDefaultAsync<ServiceInstanceHeartbeatRow>(@"
      SELECT instance_id, last_heartbeat_at
      FROM wh_service_instances WHERE instance_id = @instanceId",
      new { instanceId });

    await Assert.That(instance).IsNotNull();
    await Assert.That(instance!.last_heartbeat_at).IsGreaterThanOrEqualTo(updatedTime.AddSeconds(-1));
    await Assert.That(instance.last_heartbeat_at).IsGreaterThan(originalTime);
  }

  [Test]
  public async Task CleanupStaleInstances_StaleInstance_DeletesAndReleasesWorkAsync() {
    // Arrange
    var staleInstanceId = _idProvider.NewGuid();
    var currentInstanceId = _idProvider.NewGuid();
    var staleTime = DateTimeOffset.UtcNow.AddMinutes(-15);
    var currentTime = DateTimeOffset.UtcNow;
    var cutoffTime = DateTimeOffset.UtcNow.AddMinutes(-10);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert stale instance
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@instanceId, 'StaleService', 'stale-host', 999, @staleTime, @staleTime)",
      new { instanceId = staleInstanceId, staleTime });

    // Insert current instance
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@instanceId, 'CurrentService', 'current-host', 123, @currentTime, @currentTime)",
      new { instanceId = currentInstanceId, currentTime });

    // Insert work items leased by stale instance
    var outboxMessageId = _idProvider.NewGuid();
    var inboxMessageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Insert event in event store first
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, sequence_number, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', '{}'::jsonb, '{}'::jsonb, nextval('wh_event_sequence'), 1, @now)",
      new { eventId, streamId, now = DateTimeOffset.UtcNow });

    await connection.ExecuteAsync(@"
      INSERT INTO wh_outbox (message_id, destination, event_type, event_data, metadata, status, instance_id, lease_expiry, created_at)
      VALUES (@messageId, 'test', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @instanceId, @leaseExpiry, @now)",
      new { messageId = outboxMessageId, instanceId = staleInstanceId, leaseExpiry = staleTime.AddMinutes(5), now = staleTime });

    await connection.ExecuteAsync(@"
      INSERT INTO wh_inbox (message_id, handler_name, event_type, event_data, metadata, status, instance_id, lease_expiry, received_at)
      VALUES (@messageId, 'TestHandler', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @instanceId, @leaseExpiry, @now)",
      new { messageId = inboxMessageId, instanceId = staleInstanceId, leaseExpiry = staleTime.AddMinutes(5), now = staleTime });

    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, sequence_number, instance_id, lease_expiry, status, created_at)
      VALUES (@workId, @streamId, 'TestPerspective', @eventId, 1, @instanceId, @leaseExpiry, 1, @now)",
      new { workId = _idProvider.NewGuid(), streamId, eventId, instanceId = staleInstanceId, leaseExpiry = staleTime.AddMinutes(5), now = staleTime });

    // Act
    var deletedIds = await connection.QueryAsync<Guid>(@"
      SELECT deleted_instance_id FROM cleanup_stale_instances(@cutoffTime)",
      new { cutoffTime });

    // Assert
    await Assert.That(deletedIds).Contains(staleInstanceId);
    await Assert.That(deletedIds).DoesNotContain(currentInstanceId);

    // Verify stale instance was deleted
    var staleExists = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_service_instances WHERE instance_id = @instanceId",
      new { instanceId = staleInstanceId });
    await Assert.That(staleExists).IsEqualTo(0);

    // Verify current instance still exists
    var currentExists = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_service_instances WHERE instance_id = @instanceId",
      new { instanceId = currentInstanceId });
    await Assert.That(currentExists).IsEqualTo(1);

    // Verify work items were released
    var outboxInstanceId = await connection.QuerySingleOrDefaultAsync<Guid?>(@"
      SELECT instance_id FROM wh_outbox WHERE message_id = @messageId",
      new { messageId = outboxMessageId });
    await Assert.That(outboxInstanceId).IsNull();

    var inboxInstanceId = await connection.QuerySingleOrDefaultAsync<Guid?>(@"
      SELECT instance_id FROM wh_inbox WHERE message_id = @messageId",
      new { messageId = inboxMessageId });
    await Assert.That(inboxInstanceId).IsNull();
  }

  [Test]
  public async Task CalculateInstanceRank_MultipleInstances_ReturnsCorrectRankAsync() {
    // Arrange
    var instance1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var instance2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    var instance3 = Guid.Parse("00000000-0000-0000-0000-000000000003");
    var now = DateTimeOffset.UtcNow;
    var cutoff = now.AddMinutes(-5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert instances in non-sequential order
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@id, 'Service', 'host', 1, @now, @now)",
      new { id = instance2, now });

    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@id, 'Service', 'host', 1, @now, @now)",
      new { id = instance1, now });

    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@id, 'Service', 'host', 1, @now, @now)",
      new { id = instance3, now });

    // Act & Assert - Check rank for instance1 (should be 0, first in UUID order)
    var result1 = await connection.QuerySingleAsync<InstanceRankResult>(@"
      SELECT instance_rank, active_instance_count FROM calculate_instance_rank(@instanceId, @cutoff)",
      new { instanceId = instance1, cutoff });

    await Assert.That(result1.instance_rank).IsEqualTo(0);
    await Assert.That(result1.active_instance_count).IsEqualTo(3);

    // Check rank for instance2 (should be 1, second in UUID order)
    var result2 = await connection.QuerySingleAsync<InstanceRankResult>(@"
      SELECT instance_rank, active_instance_count FROM calculate_instance_rank(@instanceId, @cutoff)",
      new { instanceId = instance2, cutoff });

    await Assert.That(result2.instance_rank).IsEqualTo(1);
    await Assert.That(result2.active_instance_count).IsEqualTo(3);

    // Check rank for instance3 (should be 2, third in UUID order)
    var result3 = await connection.QuerySingleAsync<InstanceRankResult>(@"
      SELECT instance_rank, active_instance_count FROM calculate_instance_rank(@instanceId, @cutoff)",
      new { instanceId = instance3, cutoff });

    await Assert.That(result3.instance_rank).IsEqualTo(2);
    await Assert.That(result3.active_instance_count).IsEqualTo(3);
  }

  [Test]
  public async Task CalculateInstanceRank_StaleInstance_ExcludesFromCountAsync() {
    // Arrange
    var activeInstance = _idProvider.NewGuid();
    var staleInstance = _idProvider.NewGuid();
    var activeTime = DateTimeOffset.UtcNow;
    var staleTime = DateTimeOffset.UtcNow.AddMinutes(-15);
    var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert active instance
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@id, 'Active', 'host', 1, @time, @time)",
      new { id = activeInstance, time = activeTime });

    // Insert stale instance
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@id, 'Stale', 'host', 2, @time, @time)",
      new { id = staleInstance, time = staleTime });

    // Act
    var result = await connection.QuerySingleAsync<InstanceRankResult>(@"
      SELECT instance_rank, active_instance_count FROM calculate_instance_rank(@instanceId, @cutoff)",
      new { instanceId = activeInstance, cutoff });

    // Assert - Only active instance counted
    await Assert.That(result.active_instance_count).IsEqualTo(1);
    await Assert.That(result.instance_rank).IsEqualTo(0);
  }

  [Test]
  public async Task CalculateInstanceRank_NonExistentInstance_ThrowsExceptionAsync() {
    // Arrange
    var nonExistentInstance = _idProvider.NewGuid();
    var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Act & Assert
    var exception = await Assert.ThrowsExactlyAsync<Npgsql.PostgresException>(async () => {
      await connection.QuerySingleAsync<InstanceRankResult>(@"
        SELECT instance_rank, active_instance_count FROM calculate_instance_rank(@instanceId, @cutoff)",
        new { instanceId = nonExistentInstance, cutoff });
    });

    await Assert.That(exception!.Message).Contains("Failed to calculate rank");
  }

  [Test]
  public async Task ProcessOutboxCompletions_ProductionMode_DeletesPublishedMessagesAsync() {
    // Arrange
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert outbox message
    await connection.ExecuteAsync(@"
      INSERT INTO wh_outbox (message_id, destination, event_type, event_data, metadata, status, stream_id, created_at)
      VALUES (@messageId, 'test-destination', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @streamId, @now)",
      new { messageId, streamId, now });

    // Prepare completion with Published flag (4)
    var completions = System.Text.Json.JsonSerializer.Serialize(new[] {
      new { MessageId = messageId, Status = 4 }
    });

    // Act
    var results = await connection.QueryAsync<CompletionResult>(@"
      SELECT message_id, stream_id, was_deleted
      FROM process_outbox_completions(@completions::jsonb, @now, false)",
      new { completions, now });

    // Assert
    var result = results.Single();
    await Assert.That(result.message_id).IsEqualTo(messageId);
    await Assert.That(result.stream_id).IsEqualTo(streamId);
    await Assert.That(result.was_deleted).IsTrue();

    // Verify message was deleted
    var exists = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
    await Assert.That(exists).IsEqualTo(0);
  }

  [Test]
  public async Task ProcessOutboxCompletions_DebugMode_RetainsMessagesAsync() {
    // Arrange
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert outbox message
    await connection.ExecuteAsync(@"
      INSERT INTO wh_outbox (message_id, destination, event_type, event_data, metadata, status, stream_id, created_at)
      VALUES (@messageId, 'test-destination', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @streamId, @now)",
      new { messageId, streamId, now });

    // Prepare completion with Published flag (4)
    var completions = System.Text.Json.JsonSerializer.Serialize(new[] {
      new { MessageId = messageId, Status = 4 }
    });

    // Act
    var results = await connection.QueryAsync<CompletionResult>(@"
      SELECT message_id, stream_id, was_deleted
      FROM process_outbox_completions(@completions::jsonb, @now, true)",
      new { completions, now });

    // Assert
    var result = results.Single();
    await Assert.That(result.was_deleted).IsFalse();

    // Verify message was retained
    var status = await connection.QuerySingleAsync<int>(@"
      SELECT status FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
    await Assert.That(status & 4).IsEqualTo(4); // Published flag set
  }

  [Test]
  public async Task ProcessInboxCompletions_ProductionMode_DeletesEventStoredMessagesAsync() {
    // Arrange
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert inbox message
    await connection.ExecuteAsync(@"
      INSERT INTO wh_inbox (message_id, handler_name, event_type, event_data, metadata, status, stream_id, received_at)
      VALUES (@messageId, 'TestHandler', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @streamId, @now)",
      new { messageId, streamId, now });

    // Prepare completion with EventStored flag (2)
    var completions = System.Text.Json.JsonSerializer.Serialize(new[] {
      new { MessageId = messageId, Status = 2 }
    });

    // Act
    var results = await connection.QueryAsync<CompletionResult>(@"
      SELECT message_id, stream_id, was_deleted
      FROM process_inbox_completions(@completions::jsonb, @now, false)",
      new { completions, now });

    // Assert
    var result = results.Single();
    await Assert.That(result.was_deleted).IsTrue();

    // Verify message was deleted
    var exists = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_inbox WHERE message_id = @messageId",
      new { messageId });
    await Assert.That(exists).IsEqualTo(0);
  }

  [Test]
  public async Task ProcessPerspectiveEventCompletions_ProductionMode_DeletesEventsAsync() {
    // Arrange
    var workId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    var perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store first
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, sequence_number, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', '{}'::jsonb, '{}'::jsonb, nextval('wh_event_sequence'), 1, @now)",
      new { eventId, streamId, now });

    // Insert perspective event
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, sequence_number, status, created_at)
      VALUES (@workId, @streamId, @perspectiveName, @eventId, 1, 1, @now)",
      new { workId, streamId, perspectiveName, eventId, now });

    // Prepare completion
    var completions = System.Text.Json.JsonSerializer.Serialize(new[] {
      new { EventWorkId = workId, StatusFlags = 1 }
    });

    // Act
    var results = await connection.QueryAsync<PerspectiveCompletionResult>(@"
      SELECT event_work_id, stream_id, perspective_name, was_deleted
      FROM process_perspective_event_completions(@completions::jsonb, @now, false)",
      new { completions, now });

    // Assert
    var result = results.Single();
    await Assert.That(result.event_work_id).IsEqualTo(workId);
    await Assert.That(result.was_deleted).IsTrue();

    // Verify event was deleted
    var exists = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_perspective_events WHERE event_work_id = @workId",
      new { workId });
    await Assert.That(exists).IsEqualTo(0);
  }

  [Test]
  public async Task UpdatePerspectiveCheckpoints_UpdatesCheckpointWithHighestSequenceAsync() {
    // Arrange
    var streamId = _idProvider.NewGuid();
    var perspectiveName = "TestPerspective";
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, sequence_number, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Test', 'Event1', '{}'::jsonb, '{}'::jsonb, 1, 1, @now),
        (@event2Id, @streamId, @streamId, 'Test', 'Event2', '{}'::jsonb, '{}'::jsonb, 2, 2, @now),
        (@event3Id, @streamId, @streamId, 'Test', 'Event3', '{}'::jsonb, '{}'::jsonb, 3, 3, @now)",
      new { event1Id, event2Id, event3Id, streamId, now });

    // Insert perspective events (1 and 2 processed, 3 not processed)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, sequence_number, status, created_at, processed_at)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 1, 1, @now, @now),
        (@workId2, @streamId, @perspectiveName, @event2Id, 2, 1, @now, @now),
        (@workId3, @streamId, @perspectiveName, @event3Id, 3, 1, @now, NULL)",
      new {
        workId1 = _idProvider.NewGuid(),
        workId2 = _idProvider.NewGuid(),
        workId3 = _idProvider.NewGuid(),
        streamId,
        perspectiveName,
        event1Id,
        event2Id,
        event3Id,
        now
      });

    // Prepare completed events
    var completedEvents = System.Text.Json.JsonSerializer.Serialize(new[] {
      new { StreamId = streamId, PerspectiveName = perspectiveName }
    });

    // Act
    await connection.ExecuteAsync(@"
      SELECT update_perspective_checkpoints(@completedEvents::jsonb, false)",
      new { completedEvents });

    // Assert - checkpoint should be at event2 (highest with no gaps)
    var checkpointEventId = await connection.QuerySingleOrDefaultAsync<Guid?>(@"
      SELECT last_event_id FROM wh_perspective_checkpoints
      WHERE stream_id = @streamId AND perspective_name = @perspectiveName",
      new { streamId, perspectiveName });

    await Assert.That(checkpointEventId).IsEqualTo(event2Id);
  }

  [Test]
  public async Task ProcessOutboxFailures_SetsFailureFlagsAndSchedulesRetryAsync() {
    // Arrange
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert outbox message
    await connection.ExecuteAsync(@"
      INSERT INTO wh_outbox (message_id, destination, event_type, event_data, metadata, status, stream_id, attempts, created_at)
      VALUES (@messageId, 'test-destination', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @streamId, 0, @now)",
      new { messageId, streamId, now });

    // Prepare failure with Failed flag (32768)
    var failures = System.Text.Json.JsonSerializer.Serialize(new[] {
      new { MessageId = messageId, CompletedStatus = 1, Error = "Test error", FailureReason = 1 }
    });

    // Act
    await connection.ExecuteAsync(@"
      SELECT process_outbox_failures(@failures::jsonb, @now)",
      new { failures, now });

    // Assert - check status has Failed flag
    var status = await connection.QuerySingleAsync<int>(@"
      SELECT status FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
    await Assert.That(status & 32768).IsEqualTo(32768); // Failed flag set

    // Check attempts incremented
    var attempts = await connection.QuerySingleAsync<int>(@"
      SELECT attempts FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
    await Assert.That(attempts).IsEqualTo(1);

    // Check scheduled_for is in the future (exponential backoff)
    var scheduledFor = await connection.QuerySingleAsync<DateTimeOffset?>(@"
      SELECT scheduled_for FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
    await Assert.That(scheduledFor).IsNotNull();
    await Assert.That(scheduledFor!.Value).IsGreaterThan(now);
  }

  [Test]
  public async Task ProcessPerspectiveEventFailures_SetsFailureFlagsAndSchedulesRetryAsync() {
    // Arrange
    var workId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store first
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, sequence_number, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', '{}'::jsonb, '{}'::jsonb, nextval('wh_event_sequence'), 1, @now)",
      new { eventId, streamId, now });

    // Insert perspective event
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, sequence_number, status, attempts, created_at)
      VALUES (@workId, @streamId, 'TestPerspective', @eventId, 1, 1, 0, @now)",
      new { workId, streamId, eventId, now });

    // Prepare failure
    var failures = System.Text.Json.JsonSerializer.Serialize(new[] {
      new { EventWorkId = workId, CompletedStatus = 1, Error = "Test error", FailureReason = 1 }
    });

    // Act
    await connection.ExecuteAsync(@"
      SELECT process_perspective_event_failures(@failures::jsonb, @now)",
      new { failures, now });

    // Assert
    var status = await connection.QuerySingleAsync<int>(@"
      SELECT status FROM wh_perspective_events WHERE event_work_id = @workId",
      new { workId });
    await Assert.That(status & 32768).IsEqualTo(32768); // Failed flag set

    var attempts = await connection.QuerySingleAsync<int>(@"
      SELECT attempts FROM wh_perspective_events WHERE event_work_id = @workId",
      new { workId });
    await Assert.That(attempts).IsEqualTo(1);
  }

  [Test]
  public async Task StoreOutboxMessages_InsertsWithImmediateLeaseAsync() {
    // Arrange
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var leaseExpiry = now.AddMinutes(5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Prepare message
    var messages = System.Text.Json.JsonSerializer.Serialize(new[] {
      new {
        MessageId = messageId,
        Destination = "test-destination",
        MessageType = "TestEvent",
        EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestEvent]], Whizbang.Core",
        EnvelopeData = "{}",
        Metadata = "{}",
        Scope = (string?)null,
        StreamId = streamId,
        IsEvent = false
      }
    });

    // Act
    var results = await connection.QueryAsync<StoreMessageResult>(@"
      SELECT message_id, stream_id, was_newly_created
      FROM store_outbox_messages(@messages::jsonb, @instanceId, @leaseExpiry, @now, 10000)",
      new { messages, instanceId, leaseExpiry, now });

    // Assert
    var result = results.Single();
    await Assert.That(result.message_id).IsEqualTo(messageId);
    await Assert.That(result.was_newly_created).IsTrue();

    // Verify message has immediate lease
    var msg = await connection.QuerySingleAsync<OutboxMessageRow>(@"
      SELECT message_id, instance_id, lease_expiry FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
    await Assert.That(msg.instance_id).IsEqualTo(instanceId);
    await Assert.That(msg.lease_expiry).IsGreaterThan(now);
  }

  [Test]
  public async Task StorePerspectiveEvents_InsertsWithImmediateLeaseAsync() {
    // Arrange
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    var instanceId = _idProvider.NewGuid();
    var perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;
    var leaseExpiry = now.AddMinutes(5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store first
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, sequence_number, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, 1, @now)",
      new { eventId, streamId, now });

    // Prepare event
    var events = System.Text.Json.JsonSerializer.Serialize(new[] {
      new {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        EventId = eventId,
        SequenceNumber = 1L
      }
    });

    // Act
    var results = await connection.QueryAsync<StorePerspectiveEventResult>(@"
      SELECT event_work_id as message_id, stream_id, perspective_name, was_newly_created
      FROM store_perspective_events(@events::jsonb, @instanceId, @leaseExpiry, @now)",
      new { events, instanceId, leaseExpiry, now });

    // Assert
    var result = results.Single();
    await Assert.That(result.was_newly_created).IsTrue();

    // Verify event has immediate lease
    var evt = await connection.QuerySingleAsync<PerspectiveEventRow>(@"
      SELECT event_work_id, instance_id, lease_expiry
      FROM wh_perspective_events
      WHERE stream_id = @streamId AND perspective_name = @perspectiveName",
      new { streamId, perspectiveName });
    await Assert.That(evt.instance_id).IsEqualTo(instanceId);
    await Assert.That(evt.lease_expiry).IsGreaterThan(now);
  }

  [Test]
  public async Task CleanupCompletedStreams_RemovesStreamsWithNoPendingWorkAsync() {
    // Arrange
    var completedStreamId = _idProvider.NewGuid();
    var pendingStreamId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var instanceId = _idProvider.NewGuid();

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert active streams
    await connection.ExecuteAsync(@"
      INSERT INTO wh_active_streams (stream_id, assigned_instance_id, lease_expiry, partition_number, last_activity_at)
      VALUES
        (@completedStreamId, @instanceId, @leaseExpiry, 1, @now),
        (@pendingStreamId, @instanceId, @leaseExpiry, 2, @now)",
      new { completedStreamId, pendingStreamId, instanceId, leaseExpiry = now.AddMinutes(5), now });

    // Insert pending outbox message for pendingStreamId
    await connection.ExecuteAsync(@"
      INSERT INTO wh_outbox (message_id, destination, event_type, event_data, metadata, status, stream_id, created_at)
      VALUES (@messageId, 'test', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @pendingStreamId, @now)",
      new { messageId = _idProvider.NewGuid(), pendingStreamId, now });

    // Act - Create temp table and populate with stream IDs to check (in single transaction)
    using var transaction = connection.BeginTransaction();

    await connection.ExecuteAsync(@"
      CREATE TEMP TABLE IF NOT EXISTS temp_completed_perspectives (
        stream_id UUID,
        perspective_name VARCHAR(200),
        PRIMARY KEY (stream_id, perspective_name)
      ) ON COMMIT DROP",
      transaction: transaction);

    await connection.ExecuteAsync(@"
      INSERT INTO temp_completed_perspectives (stream_id, perspective_name)
      VALUES
        (@completedStreamId, 'TestPerspective'),
        (@pendingStreamId, 'TestPerspective')",
      new { completedStreamId, pendingStreamId },
      transaction: transaction);

    await connection.ExecuteAsync(@"
      SELECT cleanup_completed_streams(@now)",
      new { now },
      transaction: transaction);

    transaction.Commit();

    // Assert - completed stream should be removed
    var completedExists = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = @completedStreamId",
      new { completedStreamId });
    await Assert.That(completedExists).IsEqualTo(0);

    // Pending stream should still exist
    var pendingExists = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_active_streams WHERE stream_id = @pendingStreamId",
      new { pendingStreamId });
    await Assert.That(pendingExists).IsEqualTo(1);
  }

  [Test]
  public async Task ClaimOrphanedOutbox_ClaimsMessagesForCorrectPartitionAsync() {
    // Arrange
    var instance1 = _idProvider.NewGuid();
    var instance2 = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var message1Id = _idProvider.NewGuid();
    var message2Id = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var leaseExpiry = now.AddMinutes(5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Calculate partitions for messages
    var partition1 = await connection.QuerySingleAsync<int>(@"
      SELECT compute_partition(@streamId, 10000)", new { streamId });

    // Insert orphaned outbox messages
    await connection.ExecuteAsync(@"
      INSERT INTO wh_outbox (message_id, destination, event_type, event_data, metadata, status, stream_id, partition_number, created_at, instance_id, lease_expiry)
      VALUES
        (@message1Id, 'test', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @streamId, @partition1, @now, NULL, NULL),
        (@message2Id, 'test', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @streamId, @partition1, @now, NULL, NULL)",
      new { message1Id, message2Id, streamId, partition1, now });

    // Insert active stream for instance1
    await connection.ExecuteAsync(@"
      INSERT INTO wh_active_streams (stream_id, assigned_instance_id, lease_expiry, partition_number, last_activity_at)
      VALUES (@streamId, @instance1, @leaseExpiry, @partition1, @now)",
      new { streamId, instance1, leaseExpiry, partition1, now });

    // Calculate which rank should claim this partition (rank = partition % active_count)
    var expectedRank = partition1 % 2;

    // Act - instance1 with calculated rank claims work
    var claimed = await connection.QueryAsync<ClaimResult>(@"
      SELECT message_id, stream_id
      FROM claim_orphaned_outbox(@instance1, @expectedRank, 2, @leaseExpiry, @now, 10000)",
      new { instance1, expectedRank, leaseExpiry, now });

    // Assert - instance1 should claim both messages (owns the stream and correct partition)
    await Assert.That(claimed.Count()).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task ClaimOrphanedInbox_RespectsStreamOwnershipAsync() {
    // Arrange
    var instance1 = _idProvider.NewGuid();
    var instance2 = _idProvider.NewGuid();
    var stream1Id = _idProvider.NewGuid();
    var stream2Id = _idProvider.NewGuid();
    var message1Id = _idProvider.NewGuid();
    var message2Id = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var leaseExpiry = now.AddMinutes(5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert orphaned inbox messages for different streams
    await connection.ExecuteAsync(@"
      INSERT INTO wh_inbox (message_id, handler_name, event_type, event_data, metadata, status, stream_id, received_at, instance_id, lease_expiry)
      VALUES
        (@message1Id, 'TestHandler', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @stream1Id, @now, NULL, NULL),
        (@message2Id, 'TestHandler', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @stream2Id, @now, NULL, NULL)",
      new { message1Id, message2Id, stream1Id, stream2Id, now });

    // Insert active streams - instance1 owns stream1, instance2 owns stream2
    await connection.ExecuteAsync(@"
      INSERT INTO wh_active_streams (stream_id, assigned_instance_id, lease_expiry, partition_number, last_activity_at)
      VALUES
        (@stream1Id, @instance1, @leaseExpiry, 1, @now),
        (@stream2Id, @instance2, @leaseExpiry, 2, @now)",
      new { stream1Id, stream2Id, instance1, instance2, leaseExpiry, now });

    // Act - instance1 claims work
    var claimed = await connection.QueryAsync<ClaimResult>(@"
      SELECT message_id, stream_id
      FROM claim_orphaned_inbox(@instance1, 0, 2, @leaseExpiry, @now, 10000)",
      new { instance1, leaseExpiry, now });

    // Assert - instance1 should only claim message1 (owns stream1)
    await Assert.That(claimed.Count()).IsEqualTo(1);
    await Assert.That(claimed.Single().message_id).IsEqualTo(message1Id);
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_EnsuresSequentialOrderingAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    var perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;
    var leaseExpiry = now.AddMinutes(5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, sequence_number, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Test', 'Event1', '{}'::jsonb, '{}'::jsonb, 1, 1, @now),
        (@event2Id, @streamId, @streamId, 'Test', 'Event2', '{}'::jsonb, '{}'::jsonb, 2, 2, @now),
        (@event3Id, @streamId, @streamId, 'Test', 'Event3', '{}'::jsonb, '{}'::jsonb, 3, 3, @now)",
      new { event1Id, event2Id, event3Id, streamId, now });

    // Insert perspective events - event1 claimed elsewhere, event2 and event3 orphaned
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, sequence_number, status, created_at, instance_id, lease_expiry)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 1, 1, @now, @otherInstance, @futureExpiry),
        (@workId2, @streamId, @perspectiveName, @event2Id, 2, 1, @now, NULL, NULL),
        (@workId3, @streamId, @perspectiveName, @event3Id, 3, 1, @now, NULL, NULL)",
      new {
        workId1 = _idProvider.NewGuid(),
        workId2 = _idProvider.NewGuid(),
        workId3 = _idProvider.NewGuid(),
        streamId,
        perspectiveName,
        event1Id,
        event2Id,
        event3Id,
        now,
        otherInstance = _idProvider.NewGuid(),
        futureExpiry = now.AddMinutes(10)
      });

    // Insert active stream
    await connection.ExecuteAsync(@"
      INSERT INTO wh_active_streams (stream_id, assigned_instance_id, lease_expiry, partition_number, last_activity_at)
      VALUES (@streamId, @instanceId, @leaseExpiry, 1, @now)",
      new { streamId, instanceId, leaseExpiry, now });

    // Act - claim orphaned events
    var claimed = await connection.QueryAsync<ClaimResult>(@"
      SELECT event_work_id as message_id, stream_id
      FROM claim_orphaned_perspective_events(@instanceId, @leaseExpiry, @now)",
      new { instanceId, leaseExpiry, now });

    // Assert - should NOT claim event2 or event3 because event1 is still claimed by another instance
    // (must maintain sequential ordering)
    await Assert.That(claimed.Count()).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_ClaimsWhenNoEarlierUncompletedAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;
    var leaseExpiry = now.AddMinutes(5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, sequence_number, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Test', 'Event1', '{}'::jsonb, '{}'::jsonb, 1, 1, @now),
        (@event2Id, @streamId, @streamId, 'Test', 'Event2', '{}'::jsonb, '{}'::jsonb, 2, 2, @now)",
      new { event1Id, event2Id, streamId, now });

    // Insert perspective events - both orphaned
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, sequence_number, status, created_at, instance_id, lease_expiry)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 1, 1, @now, NULL, NULL),
        (@workId2, @streamId, @perspectiveName, @event2Id, 2, 1, @now, NULL, NULL)",
      new {
        workId1 = _idProvider.NewGuid(),
        workId2 = _idProvider.NewGuid(),
        streamId,
        perspectiveName,
        event1Id,
        event2Id,
        now
      });

    // Insert active stream
    await connection.ExecuteAsync(@"
      INSERT INTO wh_active_streams (stream_id, assigned_instance_id, lease_expiry, partition_number, last_activity_at)
      VALUES (@streamId, @instanceId, @leaseExpiry, 1, @now)",
      new { streamId, instanceId, leaseExpiry, now });

    // Act - claim orphaned events
    var claimed = await connection.QueryAsync<ClaimResult>(@"
      SELECT event_work_id as message_id, stream_id
      FROM claim_orphaned_perspective_events(@instanceId, @leaseExpiry, @now)",
      new { instanceId, leaseExpiry, now });

    // Assert - should claim both events (sequential and all orphaned)
    await Assert.That(claimed.Count()).IsEqualTo(2);
  }

  // Helper record types for query results
  private sealed record ServiceInstanceRow(
    Guid instance_id,
    string service_name,
    string host_name,
    int process_id,
    DateTimeOffset last_heartbeat_at);

  private sealed record ServiceInstanceHeartbeatRow(
    Guid instance_id,
    DateTimeOffset last_heartbeat_at);

  private sealed record InstanceRankResult(
    int instance_rank,
    int active_instance_count);

  private sealed record CompletionResult(
    Guid message_id,
    Guid stream_id,
    bool was_deleted);

  private sealed record PerspectiveCompletionResult(
    Guid event_work_id,
    Guid stream_id,
    string perspective_name,
    bool was_deleted);

  private sealed record StoreMessageResult(
    Guid message_id,
    Guid stream_id,
    bool was_newly_created);

  private sealed record StorePerspectiveEventResult(
    Guid message_id,
    Guid stream_id,
    string perspective_name,
    bool was_newly_created);

  private sealed record OutboxMessageRow(
    Guid message_id,
    Guid instance_id,
    DateTimeOffset lease_expiry);

  private sealed record PerspectiveEventRow(
    Guid event_work_id,
    Guid instance_id,
    DateTimeOffset lease_expiry);

  private sealed record ClaimResult(
    Guid message_id,
    Guid stream_id);
}
