using System.Text.Json;
using Dapper;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Tests for individual PostgreSQL functions (migrations 009-012).
/// Tests the decomposed functions that will eventually replace process_work_batch.
/// Uses UUIDv7 for all IDs to ensure proper time-ordered database indexing.
/// </summary>
public class PostgresFunctionTests : PostgresTestBase {
  private readonly Uuid7IdProvider _idProvider = new();

  [Test]
  public async Task RegisterInstanceHeartbeat_NewInstance_InsertsSuccessfullyAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    const string serviceName = "TestService";
    const string hostName = "test-host";
    const int processId = 12345;
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
    const string serviceName = "TestService";
    const string hostName = "test-host";
    const int processId = 12345;
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
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now)",
      new { eventId, streamId, now = DateTimeOffset.UtcNow });

    await connection.ExecuteAsync(@"
      INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, instance_id, lease_expiry, created_at)
      VALUES (@messageId, 'test', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @instanceId, @leaseExpiry, @now)",
      new { messageId = outboxMessageId, instanceId = staleInstanceId, leaseExpiry = staleTime.AddMinutes(5), now = staleTime });

    await connection.ExecuteAsync(@"
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, instance_id, lease_expiry, received_at)
      VALUES (@messageId, 'TestHandler', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @instanceId, @leaseExpiry, @now)",
      new { messageId = inboxMessageId, instanceId = staleInstanceId, leaseExpiry = staleTime.AddMinutes(5), now = staleTime });

    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, status, created_at)
      VALUES (@workId, @streamId, 'TestPerspective', @eventId, @instanceId, @leaseExpiry, 1, @now)",
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
      INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, stream_id, created_at)
      VALUES (@messageId, 'test-destination', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @streamId, @now)",
      new { messageId, streamId, now });

    // Prepare completion with Published flag (4)
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var completions = JsonSerializer.Serialize(new[] {
      new { MessageId = (Guid)messageId, Status = 4 }
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
      INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, stream_id, created_at)
      VALUES (@messageId, 'test-destination', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @streamId, @now)",
      new { messageId, streamId, now });

    // Prepare completion with Published flag (4)
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var completions = JsonSerializer.Serialize(new[] {
      new { MessageId = (Guid)messageId, Status = 4 }
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
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, stream_id, received_at)
      VALUES (@messageId, 'TestHandler', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @streamId, @now)",
      new { messageId, streamId, now });

    // Prepare completion with EventStored flag (2)
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var completions = JsonSerializer.Serialize(new[] {
      new { MessageId = (Guid)messageId, Status = 2 }
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
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store first
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now)",
      new { eventId, streamId, now });

    // Insert perspective event
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at)
      VALUES (@workId, @streamId, @perspectiveName, @eventId, 1, @now)",
      new { workId, streamId, perspectiveName, eventId, now });

    // Prepare completion
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var completions = JsonSerializer.Serialize(new[] {
      new { EventWorkId = (Guid)workId, StatusFlags = 1 }
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

  /// <summary>
  /// v0.671 — multi-row regression lock for the bulk DELETE/UPDATE refactor of
  /// <c>process_perspective_event_completions</c>. The current PL/pgSQL FOR-loop
  /// implementation does TWO statements per completion (a SELECT to fetch
  /// stream_id/perspective_name, then a DELETE). For N completions that's 2N
  /// round trips through the planner. production during-import gate-hold data
  /// (PR #252 cycle) showed <c>CompletePerspectiveAsync</c> at avg 122 ms /
  /// max 11.7 s — process_perspective_event_completions is one of three
  /// sub-calls inside <c>complete_perspective</c>, and the loop-per-row pattern
  /// is the per-call structural cost.
  /// </summary>
  /// <remarks>
  /// This test must pass on BOTH the current loop-per-row implementation AND
  /// the post-refactor single-statement implementation. It locks the
  /// invariants the refactor must preserve:
  ///   1. Returns exactly one row per completion that matched an existing
  ///      <c>wh_perspective_events</c> row — not-found IDs MUST be silently
  ///      skipped (not returned).
  ///   2. Returned <c>stream_id</c> / <c>perspective_name</c> match the
  ///      pre-delete row data for each EventWorkId.
  ///   3. Matched rows MUST be removed from <c>wh_perspective_events</c>
  ///      (production mode).
  /// </remarks>
  [Test]
  public async Task ProcessPerspectiveEventCompletions_MultiRowBatch_ReturnsAllMatchedAndSkipsMissingAsync() {
    // Arrange — three valid completions across two streams + one bogus ID
    var stream1 = _idProvider.NewGuid();
    var stream2 = _idProvider.NewGuid();
    var work1 = _idProvider.NewGuid();
    var work2 = _idProvider.NewGuid();
    var work3 = _idProvider.NewGuid();
    var bogusWork = _idProvider.NewGuid();
    var event1 = _idProvider.NewGuid();
    var event2 = _idProvider.NewGuid();
    var event3 = _idProvider.NewGuid();
    const string perspective1 = "PerspectiveA";
    const string perspective2 = "PerspectiveB";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event-store rows for the three real events (FK requirement).
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@e1, @s1, @s1, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now),
             (@e2, @s1, @s1, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now),
             (@e3, @s2, @s2, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now)",
      new { e1 = event1, e2 = event2, e3 = event3, s1 = stream1, s2 = stream2, now });

    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at)
      VALUES (@w1, @s1, @p1, @e1, 1, @now),
             (@w2, @s1, @p2, @e2, 1, @now),
             (@w3, @s2, @p1, @e3, 1, @now)",
      new { w1 = work1, w2 = work2, w3 = work3, s1 = stream1, s2 = stream2, e1 = event1, e2 = event2, e3 = event3, p1 = perspective1, p2 = perspective2, now });

    var completions = JsonSerializer.Serialize(new[] {
      new { EventWorkId = (Guid)work1, StatusFlags = 1 },
      new { EventWorkId = (Guid)work2, StatusFlags = 1 },
      new { EventWorkId = (Guid)work3, StatusFlags = 1 },
      new { EventWorkId = (Guid)bogusWork, StatusFlags = 1 },  // doesn't exist — must be silently skipped
    });

    // Act
    var results = (await connection.QueryAsync<PerspectiveCompletionResult>(@"
      SELECT event_work_id, stream_id, perspective_name, was_deleted
      FROM process_perspective_event_completions(@completions::jsonb, @now, false)",
      new { completions, now })).ToList();

    // Assert — exactly 3 returned rows (bogus skipped), all marked deleted
    await Assert.That(results.Count).IsEqualTo(3)
      .Because("Bogus EventWorkId must be silently skipped (not returned). The refactor MUST NOT add a row for a not-found ID — both the loop pattern and the bulk DELETE/RETURNING pattern naturally satisfy this; this test locks it.");

    var byWorkId = results.ToDictionary(r => r.event_work_id);
    await Assert.That(byWorkId.ContainsKey((Guid)work1)).IsTrue();
    await Assert.That(byWorkId.ContainsKey((Guid)work2)).IsTrue();
    await Assert.That(byWorkId.ContainsKey((Guid)work3)).IsTrue();
    await Assert.That(byWorkId.ContainsKey((Guid)bogusWork)).IsFalse();

    await Assert.That(byWorkId[(Guid)work1].stream_id).IsEqualTo((Guid)stream1);
    await Assert.That(byWorkId[(Guid)work1].perspective_name).IsEqualTo(perspective1);
    await Assert.That(byWorkId[(Guid)work2].stream_id).IsEqualTo((Guid)stream1);
    await Assert.That(byWorkId[(Guid)work2].perspective_name).IsEqualTo(perspective2);
    await Assert.That(byWorkId[(Guid)work3].stream_id).IsEqualTo((Guid)stream2);
    await Assert.That(byWorkId[(Guid)work3].perspective_name).IsEqualTo(perspective1);

    foreach (var r in results) {
      await Assert.That(r.was_deleted).IsTrue();
    }

    // Verify all three real rows were deleted, bogus was a no-op
    var remaining = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_perspective_events WHERE event_work_id = ANY(@ids)",
      new { ids = new[] { (Guid)work1, (Guid)work2, (Guid)work3, (Guid)bogusWork } });
    await Assert.That(remaining).IsEqualTo(0);
  }

  [Test]
  public async Task UpdatePerspectiveCursors_UpdatesCursorWithHighestSequenceAsync() {
    // Arrange
    var streamId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Test', 'Event1', 1, @now),
        (@event2Id, @streamId, @streamId, 'Test', 'Event2', 2, @now),
        (@event3Id, @streamId, @streamId, 'Test', 'Event3', 3, @now)",
      new { event1Id, event2Id, event3Id, streamId, now });

    // Insert perspective events (1 and 2 processed, 3 not processed)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 1, @now, @now),
        (@workId2, @streamId, @perspectiveName, @event2Id, 1, @now, @now),
        (@workId3, @streamId, @perspectiveName, @event3Id, 1, @now, NULL)",
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
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var completedEvents = JsonSerializer.Serialize(new[] {
      new { StreamId = (Guid)streamId, PerspectiveName = perspectiveName }
    });

    // Act
    await connection.ExecuteAsync(@"
      SELECT update_perspective_cursors(@completedEvents::jsonb, false)",
      new { completedEvents });

    // Assert - checkpoint should be at event2 (highest with no gaps)
    var checkpointEventId = await connection.QuerySingleOrDefaultAsync<Guid?>(@"
      SELECT last_event_id FROM wh_perspective_cursors
      WHERE stream_id = @streamId AND perspective_name = @perspectiveName",
      new { streamId, perspectiveName });

    await Assert.That(checkpointEventId).IsEqualTo(event2Id);
  }

  /// <summary>
  /// v0.671 — multi-pair regression lock for the bulk INSERT/UPDATE refactor of
  /// <c>update_perspective_cursors</c>. The current PL/pgSQL FOR-loop
  /// implementation does FOUR statements per (StreamId, PerspectiveName) pair
  /// (latest-gap-free SELECT, is-complete NOT EXISTS, UPDATE, conditional
  /// INSERT). Bulk pattern collapses that to two statements (one UPDATE for
  /// existing cursors, one INSERT for new pairs), regardless of M.
  ///
  /// Mixed-state scenario this test locks:
  ///   - pair A: existing cursor, gap-free progress to event2 → advance
  ///     last_event_id, status stays incomplete (event3 still pending)
  ///   - pair B: existing cursor, fully drained (no pending events) →
  ///     status=Complete (2), last_event_id unchanged
  ///   - pair C: existing cursor, no events with processed_at NOT NULL,
  ///     but pending events exist → no change (last_event_id unchanged,
  ///     status unchanged)
  /// </summary>
  [Test]
  public async Task UpdatePerspectiveCursors_MultiPair_MixedStates_RetainOldSemanticsAsync() {
    var streamA = _idProvider.NewGuid();
    var streamB = _idProvider.NewGuid();
    var streamC = _idProvider.NewGuid();
    const string perspName = "Perspective_BulkTest";

    var eA1 = _idProvider.NewGuid();
    var eA2 = _idProvider.NewGuid();
    var eA3 = _idProvider.NewGuid();
    var eB1 = _idProvider.NewGuid();
    var eC1 = _idProvider.NewGuid();
    var initialA = _idProvider.NewGuid();
    var initialB = _idProvider.NewGuid();
    var initialC = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Event store rows for FK
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES
        (@eA1, @sA, @sA, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now),
        (@eA2, @sA, @sA, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now),
        (@eA3, @sA, @sA, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now),
        (@eB1, @sB, @sB, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now),
        (@eC1, @sC, @sC, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now)",
      new { eA1, eA2, eA3, eB1, eC1, sA = streamA, sB = streamB, sC = streamC, now });

    // Pair A: events 1,2 processed; 3 pending → expect last_event_id=eA2, status stays 0
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES
        (@w1, @sA, @p, @eA1, 1, @now, @now),
        (@w2, @sA, @p, @eA2, 1, @now, @now),
        (@w3, @sA, @p, @eA3, 1, @now, NULL)",
      new {
        w1 = _idProvider.NewGuid(),
        w2 = _idProvider.NewGuid(),
        w3 = _idProvider.NewGuid(),
        sA = streamA,
        p = perspName,
        eA1,
        eA2,
        eA3,
        now
      });

    // Pair B: no remaining events (fully drained in prod) → expect status=2, last_event_id unchanged
    // (matches the production path where process_perspective_event_completions DELETEd the rows)

    // Pair C: pending events with no processed_at → no change
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES (@w1, @sC, @p, @eC1, 1, @now, NULL)",
      new { w1 = _idProvider.NewGuid(), sC = streamC, p = perspName, eC1, now });

    // Pre-existing cursors so UPDATE path fires (not INSERT path)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status)
      VALUES
        (@sA, @p, @initialA, 0),
        (@sB, @p, @initialB, 0),
        (@sC, @p, @initialC, 0)",
      new { sA = streamA, sB = streamB, sC = streamC, p = perspName, initialA, initialB, initialC });

    var completedEvents = JsonSerializer.Serialize(new[] {
      new { StreamId = (Guid)streamA, PerspectiveName = perspName },
      new { StreamId = (Guid)streamB, PerspectiveName = perspName },
      new { StreamId = (Guid)streamC, PerspectiveName = perspName },
    });

    await connection.ExecuteAsync(@"
      SELECT update_perspective_cursors(@completedEvents::jsonb, false)",
      new { completedEvents });

    var cursorA = await connection.QuerySingleAsync<(Guid LastEventId, short Status)>(@"
      SELECT last_event_id, status FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p",
      new { s = streamA, p = perspName });
    await Assert.That(cursorA.LastEventId).IsEqualTo((Guid)eA2)
      .Because("Pair A: events 1&2 processed, gap-free run ends at eA2 (eA3 still pending). Cursor must advance to eA2.");
    await Assert.That((int)cursorA.Status).IsEqualTo(0)
      .Because("Pair A is not complete (eA3 still pending), status must stay at 0.");

    var cursorB = await connection.QuerySingleAsync<(Guid LastEventId, short Status)>(@"
      SELECT last_event_id, status FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p",
      new { s = streamB, p = perspName });
    await Assert.That(cursorB.LastEventId).IsEqualTo((Guid)initialB)
      .Because("Pair B has no perspective_events rows (drained). last_event_id must be PRESERVED (COALESCE with NULL gap-free result).");
    await Assert.That((int)cursorB.Status).IsEqualTo(2)
      .Because("Pair B is drained — NOT EXISTS unprocessed = TRUE → status must be set to 2 (Complete).");

    var cursorC = await connection.QuerySingleAsync<(Guid LastEventId, short Status)>(@"
      SELECT last_event_id, status FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p",
      new { s = streamC, p = perspName });
    await Assert.That(cursorC.LastEventId).IsEqualTo((Guid)initialC)
      .Because("Pair C has only-pending events. Gap-free result is NULL. last_event_id MUST be preserved.");
    await Assert.That((int)cursorC.Status).IsEqualTo(0)
      .Because("Pair C is not complete (eC1 pending). Status must stay 0.");
  }

  /// <summary>
  /// v0.672 — coverage lock for the debug-mode UPDATE-RETURNING branch
  /// inside <c>process_perspective_event_completions</c>. PR #254's
  /// MultiRowBatch test exercised only the production DELETE-RETURNING
  /// path; the debug branch (stamp <c>status |= status_flags</c>,
  /// <c>processed_at = p_now</c>, clear lease columns; row stays in table)
  /// went uncovered. This test exercises the debug branch end-to-end:
  /// the row MUST remain in <c>wh_perspective_events</c>, the status
  /// flags MUST OR together, lease columns MUST be cleared.
  /// </summary>
  [Test]
  public async Task ProcessPerspectiveEventCompletions_DebugMode_UpdatesInPlaceAndPreservesRowAsync() {
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    var workId = _idProvider.NewGuid();
    var leaseInstance = _idProvider.NewGuid();
    var leaseExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
    var now = DateTimeOffset.UtcNow;
    const string perspName = "Perspective_DebugMode";

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@e, @s, @s, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now)",
      new { e = eventId, s = streamId, now });

    // Insert with an existing status (bit 0 set) + a non-null instance lease,
    // so the debug-mode UPDATE has something to OR with and something to clear.
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at, instance_id, lease_expiry)
      VALUES (@w, @s, @p, @e, 1, @now, NULL, @ii, @lx)",
      new { w = workId, s = streamId, p = perspName, e = eventId, now, ii = leaseInstance, lx = leaseExpiry });

    // Status flags = 2 (Failed) — should OR with the existing 1 → final status = 3.
    var completions = JsonSerializer.Serialize(new[] {
      new { EventWorkId = (Guid)workId, StatusFlags = 2 }
    });

    // Act — debug_mode=TRUE (third argument). Returns matched rows like prod mode
    // but row stays in wh_perspective_events.
    var results = (await connection.QueryAsync<PerspectiveCompletionResult>(@"
      SELECT event_work_id, stream_id, perspective_name, was_deleted
      FROM process_perspective_event_completions(@completions::jsonb, @now, true)",
      new { completions, now })).ToList();

    await Assert.That(results.Count).IsEqualTo(1)
      .Because("Debug mode MUST still return one matched row in RETURNING — caller (complete_perspective) uses these for the cursor advancement orchestration.");
    await Assert.That(results[0].event_work_id).IsEqualTo((Guid)workId);
    await Assert.That(results[0].stream_id).IsEqualTo((Guid)streamId);
    await Assert.That(results[0].perspective_name).IsEqualTo(perspName);
    await Assert.That(results[0].was_deleted).IsFalse()
      .Because("Debug mode MUST report was_deleted=FALSE — the row was UPDATEd in place, not deleted.");

    var row = await connection.QuerySingleAsync<(int Status, DateTime? ProcessedAt, Guid? InstanceId, DateTime? LeaseExpiry)>(@"
      SELECT status, processed_at, instance_id, lease_expiry
      FROM wh_perspective_events WHERE event_work_id = @w",
      new { w = workId });

    await Assert.That(row.Status).IsEqualTo(3)
      .Because("Debug mode MUST OR p_completions[i].StatusFlags with existing status: 1 (existing) | 2 (new) = 3.");
    await Assert.That(row.ProcessedAt).IsNotNull()
      .Because("Debug mode MUST stamp processed_at = p_now so update_perspective_cursors' gap-free SELECT sees this row as processed.");
    await Assert.That(row.InstanceId).IsNull()
      .Because("Debug mode MUST clear instance_id so claim_orphaned_perspective_events doesn't try to re-claim the row.");
    await Assert.That(row.LeaseExpiry).IsNull()
      .Because("Debug mode MUST clear lease_expiry for the same reason — row is no longer leased.");

    var stillExists = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_perspective_events WHERE event_work_id = @w",
      new { w = workId });
    await Assert.That(stillExists).IsEqualTo(1)
      .Because("Debug mode MUST retain the row (vs production-mode DELETE) — debug exists precisely to keep these rows for forensic inspection.");
  }

  /// <summary>
  /// v0.672 — coverage lock for the second statement (bulk INSERT) inside
  /// <c>update_perspective_cursors</c>. PR #254's MultiPair test exercised
  /// only the UPDATE-existing-cursor path; the INSERT-new-cursor path went
  /// uncovered, which SonarCloud flagged as 4 uncovered new lines in
  /// <c>016_UpdatePerspectiveCheckpoints.sql</c>. This test exercises the
  /// INSERT path: a pair with NO pre-existing cursor + gap-free processed
  /// events. The function must CREATE the cursor row with last_event_id =
  /// gap-free event and status derived from is_complete.
  /// </summary>
  /// <remarks>
  /// The WHERE NOT EXISTS clause in the function's `needed_inserts` CTE filters
  /// to pairs without a cursor; the `WHERE new_last_event_id IS NOT NULL` filter
  /// further restricts to pairs with progress to record (the NOT NULL constraint
  /// on <c>wh_perspective_cursors.last_event_id</c> would error on a null).
  /// Both invariants get exercised here:
  ///
  ///   - newPair: no cursor exists; events 1 and 2 processed, 3 pending
  ///     → INSERT a new cursor with last_event_id = event2Id and status = 0
  ///       (NOT complete because event3 is still pending).
  ///   - noProgressPair: no cursor exists; only pending events
  ///     → SKIP (new_last_event_id IS NULL filter).
  ///     (Note: this case is structurally rare in production because
  ///     <c>store_perspective_events</c> creates the cursor when the first
  ///     event is stored; but the function must safely no-op rather than fail
  ///     the entire batch on this corner.)
  /// </remarks>
  [Test]
  public async Task UpdatePerspectiveCursors_InsertPath_NewPairWithGapFreeProgress_CreatesCursorAsync() {
    var newPair = _idProvider.NewGuid();
    var noProgressPair = _idProvider.NewGuid();
    const string perspName = "Perspective_InsertPathTest";

    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    var pendingEventId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Event-store rows for FK
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES
        (@e1, @sN, @sN, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now),
        (@e2, @sN, @sN, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now),
        (@e3, @sN, @sN, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now),
        (@p1, @sNP, @sNP, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now)",
      new { e1 = event1Id, e2 = event2Id, e3 = event3Id, p1 = pendingEventId, sN = newPair, sNP = noProgressPair, now });

    // newPair: events 1 & 2 processed; 3 pending → gap-free is event2
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES
        (@w1, @sN, @p, @e1, 1, @now, @now),
        (@w2, @sN, @p, @e2, 1, @now, @now),
        (@w3, @sN, @p, @e3, 1, @now, NULL)",
      new {
        w1 = _idProvider.NewGuid(),
        w2 = _idProvider.NewGuid(),
        w3 = _idProvider.NewGuid(),
        sN = newPair,
        p = perspName,
        e1 = event1Id,
        e2 = event2Id,
        e3 = event3Id,
        now
      });

    // noProgressPair: only-pending event → gap-free will be NULL
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES (@w, @sNP, @p, @e, 1, @now, NULL)",
      new {
        w = _idProvider.NewGuid(),
        sNP = noProgressPair,
        p = perspName,
        e = pendingEventId,
        now
      });

    // CRITICAL: do NOT pre-create cursors. The INSERT path requires no
    // existing cursor for the pair (WHERE NOT EXISTS filter inside the CTE).
    // Sanity check that none exist before the call.
    var preExisting = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_perspective_cursors WHERE stream_id = ANY(@ids)",
      new { ids = new[] { (Guid)newPair, (Guid)noProgressPair } });
    await Assert.That(preExisting).IsEqualTo(0)
      .Because("Test setup MUST leave both pairs without a cursor — that's the precondition the INSERT path exists to handle.");

    var completedEvents = JsonSerializer.Serialize(new[] {
      new { StreamId = (Guid)newPair, PerspectiveName = perspName },
      new { StreamId = (Guid)noProgressPair, PerspectiveName = perspName },
    });

    // Act
    await connection.ExecuteAsync(@"
      SELECT update_perspective_cursors(@completedEvents::jsonb, false)",
      new { completedEvents });

    // Assert — newPair MUST have a new cursor row created
    var newCursor = await connection.QuerySingleOrDefaultAsync<(Guid LastEventId, short Status)?>(@"
      SELECT last_event_id, status FROM wh_perspective_cursors
      WHERE stream_id = @s AND perspective_name = @p",
      new { s = newPair, p = perspName });
    await Assert.That(newCursor.HasValue).IsTrue()
      .Because("INSERT path MUST create a cursor row for a new pair with gap-free progress. This is the line 125-129 INSERT statement in 016_UpdatePerspectiveCheckpoints.sql.");
    await Assert.That(newCursor!.Value.LastEventId).IsEqualTo((Guid)event2Id)
      .Because("Gap-free analysis: events 1+2 processed in order, event 3 pending → last_event_id MUST be event2Id.");
    await Assert.That((int)newCursor.Value.Status).IsEqualTo(0)
      .Because("Pair is NOT complete (event 3 still pending) → status MUST be 0, not 2.");

    // Assert — noProgressPair MUST be skipped (no cursor created)
    var noProgressCursor = await connection.QuerySingleOrDefaultAsync<(Guid LastEventId, short Status)?>(@"
      SELECT last_event_id, status FROM wh_perspective_cursors
      WHERE stream_id = @s AND perspective_name = @p",
      new { s = noProgressPair, p = perspName });
    await Assert.That(noProgressCursor.HasValue).IsFalse()
      .Because("INSERT path MUST filter out pairs with NULL gap-free event_id (WHERE new_last_event_id IS NOT NULL). Creating a cursor with NULL last_event_id would violate the NOT NULL constraint and fail the entire batch — the filter is load-bearing.");
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
      INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, stream_id, attempts, created_at)
      VALUES (@messageId, 'test-destination', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @streamId, 0, @now)",
      new { messageId, streamId, now });

    // Prepare failure with Failed flag (32768)
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var failures = JsonSerializer.Serialize(new[] {
      new { MessageId = (Guid)messageId, CompletedStatus = 1, Error = "Test error", FailureReason = 1 }
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

    // Phase H step 8 — claim_orphaned_* is the SOLE source of attempt counting;
    // process_outbox_failures records the error + releases the lease without bumping
    // attempts. The initial attempts=0 stays 0 until a subsequent claim_orphaned_outbox
    // re-claims this row.
    var attempts = await connection.QuerySingleAsync<int>(@"
      SELECT attempts FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
    await Assert.That(attempts).IsEqualTo(0);

    // Check scheduled_for is in the future (exponential backoff)
    var scheduledFor = await connection.QuerySingleAsync<DateTimeOffset?>(@"
      SELECT scheduled_for FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
    await Assert.That(scheduledFor).IsNotNull();
    await Assert.That(scheduledFor!.Value).IsGreaterThan(now);
  }

  [Test]
  public async Task ProcessOutboxFailures_CapsExponentialBackoffAt5MinutesAsync() {
    // Arrange - Message with high attempts count that would overflow without cap
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    const int highAttempts = 100; // POWER(2, 101) would overflow PostgreSQL interval without cap

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert outbox message with high attempt count
    await connection.ExecuteAsync(@"
      INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, stream_id, attempts, created_at)
      VALUES (@messageId, 'test-destination', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @streamId, @highAttempts, @now)",
      new { messageId, streamId, highAttempts, now });

    // Prepare failure
    var failures = JsonSerializer.Serialize(new[] {
      new { MessageId = (Guid)messageId, CompletedStatus = 1, Error = "Test error", FailureReason = 1 }
    });

    // Act - This should NOT throw "22008: interval out of range"
    await connection.ExecuteAsync(@"
      SELECT process_outbox_failures(@failures::jsonb, @now)",
      new { failures, now });

    // Assert - scheduled_for should be capped at approximately 5 minutes
    var scheduledFor = await connection.QuerySingleAsync<DateTimeOffset>(@"
      SELECT scheduled_for FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });

    // Maximum backoff is 30s * 10 = 300s = 5 minutes
    var maxExpectedScheduledFor = now.AddMinutes(6); // Add a little buffer
    var minExpectedScheduledFor = now.AddMinutes(4); // Should be close to 5 minutes

    await Assert.That(scheduledFor).IsGreaterThan(minExpectedScheduledFor);
    await Assert.That(scheduledFor).IsLessThan(maxExpectedScheduledFor);
  }

  [Test]
  public async Task ProcessInboxFailures_CapsExponentialBackoffAt5MinutesAsync() {
    // Arrange - Message with high attempts count that would overflow without cap
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    const int highAttempts = 100; // POWER(2, 101) would overflow PostgreSQL interval without cap

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert inbox message with high attempt count
    await connection.ExecuteAsync(@"
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, stream_id, attempts, received_at)
      VALUES (@messageId, 'TestHandler', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @streamId, @highAttempts, @now)",
      new { messageId, streamId, highAttempts, now });

    // Prepare failure
    var failures = JsonSerializer.Serialize(new[] {
      new { MessageId = (Guid)messageId, CompletedStatus = 1, Error = "Test error", FailureReason = 1 }
    });

    // Act - This should NOT throw "22008: interval out of range"
    await connection.ExecuteAsync(@"
      SELECT process_inbox_failures(@failures::jsonb, @now)",
      new { failures, now });

    // Assert - scheduled_for should be capped at approximately 5 minutes
    var scheduledFor = await connection.QuerySingleAsync<DateTimeOffset>(@"
      SELECT scheduled_for FROM wh_inbox WHERE message_id = @messageId",
      new { messageId });

    // Maximum backoff is 30s * 10 = 300s = 5 minutes
    var maxExpectedScheduledFor = now.AddMinutes(6);
    var minExpectedScheduledFor = now.AddMinutes(4);

    await Assert.That(scheduledFor).IsGreaterThan(minExpectedScheduledFor);
    await Assert.That(scheduledFor).IsLessThan(maxExpectedScheduledFor);
  }

  [Test]
  public async Task ProcessPerspectiveEventFailures_CapsExponentialBackoffAt5MinutesAsync() {
    // Arrange - Event with high attempts count that would overflow without cap
    var workId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    const int highAttempts = 100; // POWER(2, 101) would overflow PostgreSQL interval without cap

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store first
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now)",
      new { eventId, streamId, now });

    // Insert perspective event with high attempt count
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
      VALUES (@workId, @streamId, 'TestPerspective', @eventId, 1, @highAttempts, @now)",
      new { workId, streamId, eventId, highAttempts, now });

    // Prepare failure
    var failures = JsonSerializer.Serialize(new[] {
      new { EventWorkId = (Guid)workId, CompletedStatus = 1, Error = "Test error", FailureReason = 1 }
    });

    // Act - This should NOT throw "22008: interval out of range"
    await connection.ExecuteAsync(@"
      SELECT process_perspective_event_failures(@failures::jsonb, @now)",
      new { failures, now });

    // Assert - scheduled_for should be capped at approximately 5 minutes
    var scheduledFor = await connection.QuerySingleAsync<DateTimeOffset>(@"
      SELECT scheduled_for FROM wh_perspective_events WHERE event_work_id = @workId",
      new { workId });

    // Maximum backoff is 30s * 10 = 300s = 5 minutes
    var maxExpectedScheduledFor = now.AddMinutes(6);
    var minExpectedScheduledFor = now.AddMinutes(4);

    await Assert.That(scheduledFor).IsGreaterThan(minExpectedScheduledFor);
    await Assert.That(scheduledFor).IsLessThan(maxExpectedScheduledFor);
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
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', nextval('wh_event_sequence'), @now)",
      new { eventId, streamId, now });

    // Insert perspective event
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
      VALUES (@workId, @streamId, 'TestPerspective', @eventId, 1, 0, @now)",
      new { workId, streamId, eventId, now });

    // Prepare failure
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var failures = JsonSerializer.Serialize(new[] {
      new { EventWorkId = (Guid)workId, CompletedStatus = 1, Error = "Test error", FailureReason = 1 }
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

    // Phase H step 8 — claim_orphaned_* is the sole attempt counter; failures don't bump.
    var attempts = await connection.QuerySingleAsync<int>(@"
      SELECT attempts FROM wh_perspective_events WHERE event_work_id = @workId",
      new { workId });
    await Assert.That(attempts).IsEqualTo(0);
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
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var messages = JsonSerializer.Serialize(new[] {
      new {
        MessageId = (Guid)messageId,
        Destination = "test-destination",
        MessageType = "TestEvent",
        EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestEvent]], Whizbang.Core",
        EnvelopeData = "{}",
        Metadata = "{}",
        Scope = (string?)null,
        StreamId = (Guid)streamId,
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
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;
    var leaseExpiry = now.AddMinutes(5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store first
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', 1, @now)",
      new { eventId, streamId, now });

    // Prepare event
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var events = JsonSerializer.Serialize(new[] {
      new {
        StreamId = (Guid)streamId,
        PerspectiveName = perspectiveName,
        EventId = (Guid)eventId
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
      INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, stream_id, created_at)
      VALUES (@messageId, 'test', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @pendingStreamId, @now)",
      new { messageId = _idProvider.NewGuid(), pendingStreamId, now });

    // Act — Phase H step 6 slice 1 rewrote cleanup_completed_streams to take UUID[]
    // directly (was a temp-table dance previously).
    await connection.ExecuteAsync(@"
      SELECT cleanup_completed_streams(@streamIds::uuid[])",
      new { streamIds = new[] { (Guid)completedStreamId, (Guid)pendingStreamId } });

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
    _ = _idProvider.NewGuid();
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
      INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, stream_id, partition_number, created_at, instance_id, lease_expiry)
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

    // Stale cutoff: p_now - 30 s. Blocking instances must have heartbeat >= this to count.
    // This test doesn't rely on the liveness check (instance1 is both owner and claimer),
    // so the stale cutoff value is irrelevant — but the parameter is required.
    var staleCutoff = now.AddSeconds(-30);

    // Act - instance1 with calculated rank claims work
    var claimed = await connection.QueryAsync<ClaimResult>(@"
      SELECT message_id, stream_id
      FROM claim_orphaned_outbox(@instance1, @expectedRank, 2, @leaseExpiry, @now, 10000, @staleCutoff)",
      new { instance1, expectedRank, leaseExpiry, now, staleCutoff });

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
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, stream_id, received_at, instance_id, lease_expiry)
      VALUES
        (@message1Id, 'TestHandler', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @stream1Id, @now, NULL, NULL),
        (@message2Id, 'TestHandler', 'Test', '{}'::jsonb, '{}'::jsonb, 1, @stream2Id, @now, NULL, NULL)",
      new { message1Id, message2Id, stream1Id, stream2Id, now });

    // Register both instances as heartbeating so the claim's liveness check treats them as live.
    // Without these registrations, instance2's ownership of stream2 would be treated as
    // abandoned and instance1 would incorrectly claim stream2's message.
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES
        (@instance1, 'TestService', 'test-host', 1234, @now, @now),
        (@instance2, 'TestService', 'test-host', 5678, @now, @now)",
      new { instance1, instance2, now });

    // Insert active streams - instance1 owns stream1, instance2 owns stream2
    await connection.ExecuteAsync(@"
      INSERT INTO wh_active_streams (stream_id, assigned_instance_id, lease_expiry, partition_number, last_activity_at)
      VALUES
        (@stream1Id, @instance1, @leaseExpiry, 1, @now),
        (@stream2Id, @instance2, @leaseExpiry, 2, @now)",
      new { stream1Id, stream2Id, instance1, instance2, leaseExpiry, now });

    // Stale cutoff at now - 30 s; both instances' heartbeats are fresh so both are "live".
    var staleCutoff = now.AddSeconds(-30);

    // Act - instance1 claims work
    var claimed = await connection.QueryAsync<ClaimResult>(@"
      SELECT message_id, stream_id
      FROM claim_orphaned_inbox(@instance1, 0, 2, @leaseExpiry, @now, 10000, @staleCutoff)",
      new { instance1, leaseExpiry, now, staleCutoff });

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
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;
    var leaseExpiry = now.AddMinutes(5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Test', 'Event1', 1, @now),
        (@event2Id, @streamId, @streamId, 'Test', 'Event2', 2, @now),
        (@event3Id, @streamId, @streamId, 'Test', 'Event3', 3, @now)",
      new { event1Id, event2Id, event3Id, streamId, now });

    // Insert perspective events - event1 claimed elsewhere, event2 and event3 orphaned
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, instance_id, lease_expiry)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 1, @now, @otherInstance, @futureExpiry),
        (@workId2, @streamId, @perspectiveName, @event2Id, 1, @now, NULL, NULL),
        (@workId3, @streamId, @perspectiveName, @event3Id, 1, @now, NULL, NULL)",
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
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;
    var leaseExpiry = now.AddMinutes(5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Test', 'Event1', 1, @now),
        (@event2Id, @streamId, @streamId, 'Test', 'Event2', 2, @now)",
      new { event1Id, event2Id, streamId, now });

    // Insert perspective events - both orphaned
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, instance_id, lease_expiry)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 1, @now, NULL, NULL),
        (@workId2, @streamId, @perspectiveName, @event2Id, 1, @now, NULL, NULL)",
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

  [Test]
  public async Task StoreOutboxMessages_DuplicateMessageId_IdempotentAsync() {
    // Arrange
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var leaseExpiry = now.AddMinutes(5);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    var messages = JsonSerializer.Serialize(new[] {
      new {
        MessageId = (Guid)messageId,
        Destination = "test-destination",
        MessageType = "TestEvent",
        EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestEvent]], Whizbang.Core",
        EnvelopeData = "{}",
        Metadata = "{}",
        Scope = (string?)null,
        StreamId = (Guid)streamId,
        IsEvent = false
      }
    });

    // Act — store same message twice
    var result1 = await connection.QueryAsync<StoreMessageResult>(@"
      SELECT message_id, stream_id, was_newly_created
      FROM store_outbox_messages(@messages::jsonb, @instanceId, @leaseExpiry, @now, 10000)",
      new { messages, instanceId, leaseExpiry, now });

    var result2 = await connection.QueryAsync<StoreMessageResult>(@"
      SELECT message_id, stream_id, was_newly_created
      FROM store_outbox_messages(@messages::jsonb, @instanceId, @leaseExpiry, @now, 10000)",
      new { messages, instanceId, leaseExpiry, now });

    // Assert — first call is new, second is idempotent
    await Assert.That(result1.Single().was_newly_created).IsTrue();
    await Assert.That(result2.Single().was_newly_created).IsFalse();

    // Verify only one row exists
    var count = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
    await Assert.That(count).IsEqualTo(1);
  }

  // Helper record types for query results
  private sealed record WorkBatchRow(
    int? instance_rank,
    int? active_instance_count,
    string source,
    Guid work_id,
    Guid? work_stream_id,
    int? partition_number,
    string? destination,
    string? message_type,
    string? envelope_type,
    string? message_data,
    string? metadata,
    int status,
    int attempts,
    bool is_newly_stored,
    bool is_orphaned,
    string? error,
    int? failure_reason,
    string? perspective_name);

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
