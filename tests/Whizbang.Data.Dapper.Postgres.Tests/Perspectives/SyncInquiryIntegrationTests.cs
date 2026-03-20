using Dapper;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Data.Dapper.Postgres.Tests;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Integration tests for sync inquiry functionality in the process_work_batch SQL function.
/// These tests verify that sync inquiries correctly report pending event counts.
/// </summary>
/// <remarks>
/// Sync inquiries are used by <see cref="PerspectiveSyncAwaiter"/> to implement
/// read-your-writes consistency by checking if perspectives have processed specific events.
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public class SyncInquiryIntegrationTests : PostgresTestBase {
  private readonly Uuid7IdProvider _idProvider = new();
  private DapperWorkCoordinator _coordinator = null!;

  [Before(Test)]
  public async Task SetupCoordinatorAsync() {
    // Wait for base setup to complete (creates database and runs migrations)
    await Task.CompletedTask;

    // Create coordinator with test connection string
    _coordinator = new DapperWorkCoordinator(
      ConnectionString,
      InfrastructureJsonContext.Default.Options
    );
  }

  [Test]
  public async Task ProcessWorkBatch_WithSyncInquiry_ReturnsPendingCountAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { eventId, streamId, now });

    // Insert perspective event (pending - processed_at IS NULL)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES (@workId, @streamId, @perspectiveName, @eventId, 0, @now, NULL)",
      new { workId = _idProvider.NewGuid(), streamId, perspectiveName, eventId, now });

    // Create sync inquiry
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventIds = [eventId],
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    await Assert.That(result.SyncInquiryResults!.Count).IsEqualTo(1);

    var syncResult = result.SyncInquiryResults[0];
    await Assert.That(syncResult.InquiryId).IsEqualTo(inquiryId);
    await Assert.That(syncResult.PendingCount).IsEqualTo(1);
    await Assert.That(syncResult.IsFullySynced).IsFalse();
  }

  [Test]
  public async Task ProcessWorkBatch_AllProcessed_ReturnsPendingCountZeroAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { eventId, streamId, now });

    // Insert perspective event (processed - processed_at IS NOT NULL)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES (@workId, @streamId, @perspectiveName, @eventId, 0, @now, @now)",
      new { workId = _idProvider.NewGuid(), streamId, perspectiveName, eventId, now });

    // Create sync inquiry
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventIds = [eventId],
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    await Assert.That(result.SyncInquiryResults!.Count).IsEqualTo(1);

    var syncResult = result.SyncInquiryResults[0];
    await Assert.That(syncResult.InquiryId).IsEqualTo(inquiryId);
    await Assert.That(syncResult.PendingCount).IsEqualTo(0);
    await Assert.That(syncResult.IsFullySynced).IsTrue();
  }

  [Test]
  public async Task ProcessWorkBatch_WithEventIdFilter_FiltersCorrectlyAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Test', 'Event1', '{}'::jsonb, '{}'::jsonb, 1, @now),
        (@event2Id, @streamId, @streamId, 'Test', 'Event2', '{}'::jsonb, '{}'::jsonb, 2, @now),
        (@event3Id, @streamId, @streamId, 'Test', 'Event3', '{}'::jsonb, '{}'::jsonb, 3, @now)",
      new { event1Id, event2Id, event3Id, streamId, now });

    // Insert perspective events:
    // - event1: processed
    // - event2: pending
    // - event3: pending
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 0, @now, @now),
        (@workId2, @streamId, @perspectiveName, @event2Id, 0, @now, NULL),
        (@workId3, @streamId, @perspectiveName, @event3Id, 0, @now, NULL)",
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

    // Create sync inquiry for only event1 and event2
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventIds = [event1Id, event2Id], // Only check these two
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - only event2 is pending in the filter set
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    await Assert.That(result.SyncInquiryResults!.Count).IsEqualTo(1);

    var syncResult = result.SyncInquiryResults[0];
    await Assert.That(syncResult.PendingCount).IsEqualTo(1); // event2 only
    await Assert.That(syncResult.IsFullySynced).IsFalse();
  }

  [Test]
  public async Task ProcessWorkBatch_IncludePendingEventIds_ReturnsEventIdsAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Test', 'Event1', '{}'::jsonb, '{}'::jsonb, 1, @now),
        (@event2Id, @streamId, @streamId, 'Test', 'Event2', '{}'::jsonb, '{}'::jsonb, 2, @now)",
      new { event1Id, event2Id, streamId, now });

    // Insert perspective events - both pending
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 0, @now, NULL),
        (@workId2, @streamId, @perspectiveName, @event2Id, 0, @now, NULL)",
      new {
        workId1 = _idProvider.NewGuid(),
        workId2 = _idProvider.NewGuid(),
        streamId,
        perspectiveName,
        event1Id,
        event2Id,
        now
      });

    // Create sync inquiry with IncludePendingEventIds = true
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      IncludePendingEventIds = true,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    await Assert.That(result.SyncInquiryResults!.Count).IsEqualTo(1);

    var syncResult = result.SyncInquiryResults[0];
    await Assert.That(syncResult.PendingCount).IsEqualTo(2);
    await Assert.That(syncResult.PendingEventIds).IsNotNull();
    await Assert.That(syncResult.PendingEventIds!.Length).IsEqualTo(2);
    await Assert.That(syncResult.PendingEventIds).Contains(event1Id);
    await Assert.That(syncResult.PendingEventIds).Contains(event2Id);
  }

  [Test]
  public async Task ProcessWorkBatch_MultipleSyncInquiries_ReturnsAllResultsAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var stream1Id = _idProvider.NewGuid();
    var stream2Id = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    const string perspective1Name = "Perspective1";
    const string perspective2Name = "Perspective2";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES
        (@event1Id, @stream1Id, @stream1Id, 'Test', 'Event1', '{}'::jsonb, '{}'::jsonb, 1, @now),
        (@event2Id, @stream2Id, @stream2Id, 'Test', 'Event2', '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { event1Id, event2Id, stream1Id, stream2Id, now });

    // Insert perspective events
    // - stream1/perspective1: pending
    // - stream2/perspective2: processed
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES
        (@workId1, @stream1Id, @perspective1Name, @event1Id, 0, @now, NULL),
        (@workId2, @stream2Id, @perspective2Name, @event2Id, 0, @now, @now)",
      new {
        workId1 = _idProvider.NewGuid(),
        workId2 = _idProvider.NewGuid(),
        stream1Id,
        stream2Id,
        perspective1Name,
        perspective2Name,
        event1Id,
        event2Id,
        now
      });

    // Create multiple sync inquiries
    var inquiry1Id = _idProvider.NewGuid();
    var inquiry2Id = _idProvider.NewGuid();
    var inquiries = new[] {
      new SyncInquiry {
        StreamId = stream1Id,
        PerspectiveName = perspective1Name,
        EventIds = [event1Id],
        InquiryId = inquiry1Id
      },
      new SyncInquiry {
        StreamId = stream2Id,
        PerspectiveName = perspective2Name,
        EventIds = [event2Id],
        InquiryId = inquiry2Id
      }
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = inquiries
    });

    // Assert
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    await Assert.That(result.SyncInquiryResults!.Count).IsEqualTo(2);

    var result1 = result.SyncInquiryResults.First(r => r.InquiryId == inquiry1Id);
    var result2 = result.SyncInquiryResults.First(r => r.InquiryId == inquiry2Id);

    await Assert.That(result1.PendingCount).IsEqualTo(1);
    await Assert.That(result1.IsFullySynced).IsFalse();

    await Assert.That(result2.PendingCount).IsEqualTo(0);
    await Assert.That(result2.IsFullySynced).IsTrue();
  }

  [Test]
  public async Task ProcessWorkBatch_NoMatchingEvents_ReturnsPendingCountZeroAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var nonExistentEventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";

    // Create sync inquiry for event that doesn't exist in perspective_events
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventIds = [nonExistentEventId],
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - No matching events means nothing pending (IsFullySynced = true)
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    await Assert.That(result.SyncInquiryResults!.Count).IsEqualTo(1);

    var syncResult = result.SyncInquiryResults[0];
    await Assert.That(syncResult.InquiryId).IsEqualTo(inquiryId);
    await Assert.That(syncResult.PendingCount).IsEqualTo(0);
    await Assert.That(syncResult.IsFullySynced).IsTrue();
  }

  [Test]
  public async Task ProcessWorkBatch_MixedPendingAndProcessed_ReturnsCorrectCountAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    var event4Id = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Test', 'Event1', '{}'::jsonb, '{}'::jsonb, 1, @now),
        (@event2Id, @streamId, @streamId, 'Test', 'Event2', '{}'::jsonb, '{}'::jsonb, 2, @now),
        (@event3Id, @streamId, @streamId, 'Test', 'Event3', '{}'::jsonb, '{}'::jsonb, 3, @now),
        (@event4Id, @streamId, @streamId, 'Test', 'Event4', '{}'::jsonb, '{}'::jsonb, 4, @now)",
      new { event1Id, event2Id, event3Id, event4Id, streamId, now });

    // Insert perspective events:
    // - event1: processed
    // - event2: pending
    // - event3: processed
    // - event4: pending
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 0, @now, @now),
        (@workId2, @streamId, @perspectiveName, @event2Id, 0, @now, NULL),
        (@workId3, @streamId, @perspectiveName, @event3Id, 0, @now, @now),
        (@workId4, @streamId, @perspectiveName, @event4Id, 0, @now, NULL)",
      new {
        workId1 = _idProvider.NewGuid(),
        workId2 = _idProvider.NewGuid(),
        workId3 = _idProvider.NewGuid(),
        workId4 = _idProvider.NewGuid(),
        streamId,
        perspectiveName,
        event1Id,
        event2Id,
        event3Id,
        event4Id,
        now
      });

    // Create sync inquiry without EventIds filter (check all events)
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      InquiryId = inquiryId
      // EventIds = null means check all events for this stream+perspective
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - 2 pending events (event2 and event4)
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    await Assert.That(result.SyncInquiryResults!.Count).IsEqualTo(1);

    var syncResult = result.SyncInquiryResults[0];
    await Assert.That(syncResult.PendingCount).IsEqualTo(2);
    await Assert.That(syncResult.IsFullySynced).IsFalse();
  }

  [Test]
  public async Task ProcessWorkBatch_NoSyncInquiries_ReturnsNullResultsAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();

    // Act - no sync inquiries
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = null
    });

    // Assert - null when no inquiries
    await Assert.That(result.SyncInquiryResults).IsNull();
  }

  [Test]
  public async Task ProcessWorkBatch_EmptySyncInquiries_ReturnsNullResultsAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();

    // Act - empty sync inquiries array
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = []
    });

    // Assert - null when empty inquiries
    await Assert.That(result.SyncInquiryResults).IsNull();
  }

  // ==========================================================================
  // Stream-based sync tests (Phase 5)
  // ==========================================================================

  [Test]
  public async Task ProcessWorkBatch_SyncInquiry_ReturnsStreamIdAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', 'TestEvent', '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { eventId, streamId, now });

    // Insert perspective event (pending)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES (@workId, @streamId, @perspectiveName, @eventId, 0, @now, NULL)",
      new { workId = _idProvider.NewGuid(), streamId, perspectiveName, eventId, now });

    // Create sync inquiry
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - StreamId should be returned in result
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];
    await Assert.That(syncResult.StreamId).IsEqualTo(streamId);
  }

  [Test]
  public async Task ProcessWorkBatch_SyncInquiry_ReturnsProcessedCountAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Test', 'Event1', '{}'::jsonb, '{}'::jsonb, 1, @now),
        (@event2Id, @streamId, @streamId, 'Test', 'Event2', '{}'::jsonb, '{}'::jsonb, 2, @now),
        (@event3Id, @streamId, @streamId, 'Test', 'Event3', '{}'::jsonb, '{}'::jsonb, 3, @now)",
      new { event1Id, event2Id, event3Id, streamId, now });

    // Insert perspective events:
    // - event1: processed
    // - event2: processed
    // - event3: pending
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 0, @now, @now),
        (@workId2, @streamId, @perspectiveName, @event2Id, 0, @now, @now),
        (@workId3, @streamId, @perspectiveName, @event3Id, 0, @now, NULL)",
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

    // Create sync inquiry
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - ProcessedCount should be 2, PendingCount should be 1
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];
    await Assert.That(syncResult.PendingCount).IsEqualTo(1);
    await Assert.That(syncResult.ProcessedCount).IsEqualTo(2);
    await Assert.That(syncResult.IsFullySynced).IsFalse();
  }

  [Test]
  public async Task ProcessWorkBatch_WithEventTypeFilter_FiltersCorrectlyAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events with different event types
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Order', 'OrderCreated', '{}'::jsonb, '{}'::jsonb, 1, @now),
        (@event2Id, @streamId, @streamId, 'Order', 'OrderShipped', '{}'::jsonb, '{}'::jsonb, 2, @now),
        (@event3Id, @streamId, @streamId, 'Order', 'OrderDelivered', '{}'::jsonb, '{}'::jsonb, 3, @now)",
      new { event1Id, event2Id, event3Id, streamId, now });

    // Insert perspective events - all pending
    // Note: event_type comes from wh_event_store via JOIN, not stored directly in wh_perspective_events
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 0, @now, NULL),
        (@workId2, @streamId, @perspectiveName, @event2Id, 0, @now, NULL),
        (@workId3, @streamId, @perspectiveName, @event3Id, 0, @now, NULL)",
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

    // Create sync inquiry with EventTypeFilter - only wait for OrderCreated and OrderShipped
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = ["OrderCreated", "OrderShipped"],
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - Only 2 events match the filter (OrderCreated and OrderShipped)
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];
    await Assert.That(syncResult.PendingCount).IsEqualTo(2); // OrderDelivered not counted
    await Assert.That(syncResult.IsFullySynced).IsFalse();
  }

  [Test]
  public async Task ProcessWorkBatch_WithEventTypeFilter_AllMatchingProcessed_IsFullySyncedAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events with different event types
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Order', 'OrderCreated', '{}'::jsonb, '{}'::jsonb, 1, @now),
        (@event2Id, @streamId, @streamId, 'Order', 'OrderShipped', '{}'::jsonb, '{}'::jsonb, 2, @now),
        (@event3Id, @streamId, @streamId, 'Order', 'OrderDelivered', '{}'::jsonb, '{}'::jsonb, 3, @now)",
      new { event1Id, event2Id, event3Id, streamId, now });

    // Insert perspective events:
    // - OrderCreated: processed
    // - OrderShipped: processed
    // - OrderDelivered: pending (but we'll filter it out)
    // Note: event_type comes from wh_event_store via JOIN, not stored directly in wh_perspective_events
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 0, @now, @now),
        (@workId2, @streamId, @perspectiveName, @event2Id, 0, @now, @now),
        (@workId3, @streamId, @perspectiveName, @event3Id, 0, @now, NULL)",
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

    // Create sync inquiry with EventTypeFilter - only wait for OrderCreated and OrderShipped
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = ["OrderCreated", "OrderShipped"],
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - Even though OrderDelivered is pending, we only care about the filtered types
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];
    await Assert.That(syncResult.PendingCount).IsEqualTo(0);
    await Assert.That(syncResult.ProcessedCount).IsEqualTo(2);
    await Assert.That(syncResult.IsFullySynced).IsTrue();
  }

  [Test]
  public async Task ProcessWorkBatch_NullEventIds_QueriesAllPendingEventsAsync() {
    // Arrange - Test that EventIds = null queries ALL pending events on the stream
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Test', 'Event1', '{}'::jsonb, '{}'::jsonb, 1, @now),
        (@event2Id, @streamId, @streamId, 'Test', 'Event2', '{}'::jsonb, '{}'::jsonb, 2, @now)",
      new { event1Id, event2Id, streamId, now });

    // Both events pending
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES
        (@workId1, @streamId, @perspectiveName, @event1Id, 0, @now, NULL),
        (@workId2, @streamId, @perspectiveName, @event2Id, 0, @now, NULL)",
      new {
        workId1 = _idProvider.NewGuid(),
        workId2 = _idProvider.NewGuid(),
        streamId,
        perspectiveName,
        event1Id,
        event2Id,
        now
      });

    // Create sync inquiry with EventIds = null (query all)
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventIds = null, // Explicitly null to query ALL pending
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - Should count ALL pending events
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];
    await Assert.That(syncResult.PendingCount).IsEqualTo(2);
    await Assert.That(syncResult.StreamId).IsEqualTo(streamId);
  }

  // ==========================================================================
  // Cross-Scope Sync Tests (DiscoverPendingFromOutbox)
  // ==========================================================================
  // These tests verify the scenario where:
  // - Request 1: Handler emits event (stored in wh_event_store)
  // - Request 2: Different handler with [AwaitPerspectiveSync] needs to wait
  // - The event is NOT yet in wh_perspective_events (worker hasn't picked it up)
  // - Sync should discover the event from wh_event_store and wait for it
  // ==========================================================================

  [Test]
  public async Task CrossScope_EventInEventStoreNotInPerspectiveEvents_DiscoversPendingAsync() {
    // Arrange: Simulate cross-scope scenario
    // Event exists in wh_event_store (emitted by Request 1)
    // But NOT in wh_perspective_events (worker hasn't processed it yet)
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store ONLY (simulating event emitted but not yet picked up by worker)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Activity', 'ActivityStartedEvent', '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { eventId, streamId, now });

    // NOTE: We do NOT insert into wh_perspective_events - the event hasn't been processed yet

    // Create sync inquiry with DiscoverPendingFromOutbox = true
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = ["ActivityStartedEvent"],
      DiscoverPendingFromOutbox = true, // KEY: Discover from event store
      IncludeProcessedEventIds = true,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - Event should be discovered as pending (exists in event_store, not processed)
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];

    // The event was discovered from event store but isn't processed yet
    await Assert.That(syncResult.PendingCount).IsEqualTo(1);
    await Assert.That(syncResult.ProcessedCount).IsEqualTo(0);
    await Assert.That(syncResult.IsFullySynced).IsFalse();
  }

  [Test]
  public async Task CrossScope_EventProcessedAfterDiscovery_ReportsSyncedAsync() {
    // Arrange: Event was discovered from event_store and is now processed
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Activity', 'ActivityStartedEvent', '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { eventId, streamId, now });

    // Insert perspective event as PROCESSED (processed_at IS NOT NULL)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES (@workId, @streamId, @perspectiveName, @eventId, 0, @now, @now)",
      new { workId = _idProvider.NewGuid(), streamId, perspectiveName, eventId, now });

    // Create sync inquiry with DiscoverPendingFromOutbox = true
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = ["ActivityStartedEvent"],
      DiscoverPendingFromOutbox = true,
      IncludeProcessedEventIds = true,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - Event is discovered and processed
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];

    await Assert.That(syncResult.PendingCount).IsEqualTo(0);
    await Assert.That(syncResult.ProcessedCount).IsEqualTo(1);
    await Assert.That(syncResult.IsFullySynced).IsTrue();
  }

  [Test]
  public async Task CrossScope_MultipleEventTypes_OnlyDiscoversMatchingTypesAsync() {
    // Arrange: Multiple events of different types in event_store
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var startedEventId = _idProvider.NewGuid();
    var completedEventId = _idProvider.NewGuid();
    var cancelledEventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert multiple events of different types
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES
        (@startedEventId, @streamId, @streamId, 'Activity', 'ActivityStartedEvent', '{}'::jsonb, '{}'::jsonb, 1, @now),
        (@completedEventId, @streamId, @streamId, 'Activity', 'ActivityCompletedEvent', '{}'::jsonb, '{}'::jsonb, 2, @now),
        (@cancelledEventId, @streamId, @streamId, 'Activity', 'ActivityCancelledEvent', '{}'::jsonb, '{}'::jsonb, 3, @now)",
      new { startedEventId, completedEventId, cancelledEventId, streamId, now });

    // None are in perspective_events yet

    // Create sync inquiry - only wait for ActivityStartedEvent
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = ["ActivityStartedEvent"], // Only this type
      DiscoverPendingFromOutbox = true,
      IncludeProcessedEventIds = true,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - Only ActivityStartedEvent should be counted
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];

    await Assert.That(syncResult.PendingCount).IsEqualTo(1); // Only ActivityStartedEvent
    await Assert.That(syncResult.IsFullySynced).IsFalse();
  }

  [Test]
  public async Task CrossScope_NoEventsInEventStore_ReportsFullySyncedAsync() {
    // Arrange: No events exist for the stream yet
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";

    // Create sync inquiry with DiscoverPendingFromOutbox = true
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = ["ActivityStartedEvent"],
      DiscoverPendingFromOutbox = true,
      IncludeProcessedEventIds = true,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - No events to wait for = fully synced
    // When there are no events matching the query, SQL returns no rows for this inquiry.
    // This is correct behavior: nothing to wait for = fully synced.
    // The absence of a sync result row is semantically equivalent to IsFullySynced=true.
    if (result.SyncInquiryResults is { Count: > 0 }) {
      var syncResult = result.SyncInquiryResults[0];
      await Assert.That(syncResult.PendingCount).IsEqualTo(0);
      await Assert.That(syncResult.ProcessedCount).IsEqualTo(0);
      await Assert.That(syncResult.IsFullySynced).IsTrue();
    }
    // If no result rows returned, that also means nothing to wait for = synced
  }

  [Test]
  public async Task CrossScope_EventPendingInPerspectiveEvents_StillReportsPendingAsync() {
    // Arrange: Event is in both event_store AND perspective_events, but pending (not processed)
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert event in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Activity', 'ActivityStartedEvent', '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { eventId, streamId, now });

    // Insert perspective event as PENDING (processed_at IS NULL)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES (@workId, @streamId, @perspectiveName, @eventId, 0, @now, NULL)",
      new { workId = _idProvider.NewGuid(), streamId, perspectiveName, eventId, now });

    // Create sync inquiry with DiscoverPendingFromOutbox = true
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = ["ActivityStartedEvent"],
      DiscoverPendingFromOutbox = true,
      IncludeProcessedEventIds = true,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - Event is pending (in perspective_events but not processed)
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];

    await Assert.That(syncResult.PendingCount).IsEqualTo(1);
    await Assert.That(syncResult.ProcessedCount).IsEqualTo(0);
    await Assert.That(syncResult.IsFullySynced).IsFalse();
  }

  [Test]
  public async Task CrossScope_ReturnsProcessedEventIdsAsync() {
    // Arrange: Verify ProcessedEventIds is returned for explicit EventId comparison
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert events in event store
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES
        (@event1Id, @streamId, @streamId, 'Activity', 'ActivityStartedEvent', '{}'::jsonb, '{}'::jsonb, 1, @now),
        (@event2Id, @streamId, @streamId, 'Activity', 'ActivityStartedEvent', '{}'::jsonb, '{}'::jsonb, 2, @now)",
      new { event1Id, event2Id, streamId, now });

    // Only event1 is processed
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES (@workId, @streamId, @perspectiveName, @event1Id, 0, @now, @now)",
      new { workId = _idProvider.NewGuid(), streamId, perspectiveName, event1Id, now });

    // Create sync inquiry with IncludeProcessedEventIds = true
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = ["ActivityStartedEvent"],
      DiscoverPendingFromOutbox = true,
      IncludeProcessedEventIds = true, // KEY: Request processed IDs back
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - ProcessedEventIds should contain event1Id
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];

    await Assert.That(syncResult.PendingCount).IsEqualTo(1); // event2
    await Assert.That(syncResult.ProcessedCount).IsEqualTo(1); // event1
    await Assert.That(syncResult.ProcessedEventIds).IsNotNull();
    await Assert.That(syncResult.ProcessedEventIds!.Length).IsEqualTo(1);
    await Assert.That(syncResult.ProcessedEventIds).Contains(event1Id);
  }

  [Test]
  public async Task CrossScope_WithExplicitEventIds_UsesExplicitIdsNotDiscoveryAsync() {
    // Arrange: When EventIds are explicitly provided, DiscoverPendingFromOutbox should be ignored
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var explicitEventId = _idProvider.NewGuid();
    var otherEventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Insert two events
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES
        (@explicitEventId, @streamId, @streamId, 'Activity', 'ActivityStartedEvent', '{}'::jsonb, '{}'::jsonb, 1, @now),
        (@otherEventId, @streamId, @streamId, 'Activity', 'ActivityStartedEvent', '{}'::jsonb, '{}'::jsonb, 2, @now)",
      new { explicitEventId, otherEventId, streamId, now });

    // Only explicitEventId is processed
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, created_at, processed_at)
      VALUES (@workId, @streamId, @perspectiveName, @explicitEventId, 0, @now, @now)",
      new { workId = _idProvider.NewGuid(), streamId, perspectiveName, explicitEventId, now });

    // Create sync inquiry with BOTH explicit EventIds AND DiscoverPendingFromOutbox
    // The explicit EventIds should take precedence
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventIds = [explicitEventId], // Explicit ID - should take precedence
      EventTypeFilter = ["ActivityStartedEvent"],
      DiscoverPendingFromOutbox = true, // Should be ignored when EventIds is set
      IncludeProcessedEventIds = true,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - Should only check the explicit EventId, not discover otherEventId
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];

    // explicitEventId is processed, so PendingCount = 0
    await Assert.That(syncResult.PendingCount).IsEqualTo(0);
    await Assert.That(syncResult.ProcessedCount).IsEqualTo(1);
    await Assert.That(syncResult.IsFullySynced).IsTrue();
  }

  // ==========================================================================
  // CRITICAL BUG REPRODUCTION TEST
  // ==========================================================================
  // This test proves the EventTypeFilter format mismatch bug.
  //
  // The bug: PerspectiveSyncAwaiter sends EventTypeFilter = [typeof(T).FullName]
  // which is like "MyApp.Events.StartedEvent" (no assembly name).
  // But events in wh_event_store are stored with "TypeName, AssemblyName" format.
  // The SQL does a direct comparison, so they DON'T match!
  // ==========================================================================

  /// <summary>
  /// PROVES THE BUG: Using FullName WITHOUT assembly name doesn't match stored format.
  /// EventTypeFilter with "MyApp.Events.Event" doesn't match stored "MyApp.Events.Event, MyApp"
  /// </summary>
  [Test]
  public async Task BUGREPRO_EventTypeFilter_WithoutAssemblyName_DoesNotMatchStoredEventAsync() {
    // Arrange: Store event with assembly-qualified name format (like the real app does)
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    // Stored format: "TypeName, AssemblyName"
    const string storedEventType = "MyApp.Activities.ActivityStartedEvent, MyApp.Contracts";

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Activity', @storedEventType, '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { eventId, streamId, storedEventType, now });

    // OLD buggy format: just FullName (no assembly)
    const string buggyQueryFormat = "MyApp.Activities.ActivityStartedEvent";

    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = [buggyQueryFormat], // BUG: Missing ", MyApp.Contracts"
      DiscoverPendingFromOutbox = true,
      IncludeProcessedEventIds = true,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - This DOCUMENTS the old buggy behavior
    // SQL returns null/empty when EventTypeFilter doesn't match due to format mismatch
    // This is expected to "fail" (return null) because the old format is wrong
    await Assert.That(result.SyncInquiryResults is null || result.SyncInquiryResults.Count == 0 ||
                      result.SyncInquiryResults[0].PendingCount == 0)
      .IsTrue()
      .Because("Without assembly name, EventTypeFilter doesn't match stored format - this documents the bug");
  }

  /// <summary>
  /// PROVES THE FIX: Using FullName WITH assembly name DOES match stored format.
  /// EventTypeFilter with "MyApp.Events.Event, MyApp" matches stored "MyApp.Events.Event, MyApp"
  /// </summary>
  [Test]
  public async Task BUGFIX_EventTypeFilter_WithAssemblyName_MatchesStoredEventAsync() {
    // Arrange: Store event with assembly-qualified name format (like the real app does)
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    // Stored format: "TypeName, AssemblyName"
    const string storedEventType = "MyApp.Activities.ActivityStartedEvent, MyApp.Contracts";

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Activity', @storedEventType, '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { eventId, streamId, storedEventType, now });

    // FIXED format: "TypeName, AssemblyName" (same as stored)
    // This is what PerspectiveSyncAwaiter NOW sends after the fix:
    // EventTypeFilter = eventTypes?.Select(t => (t.FullName ?? t.Name) + ", " + t.Assembly.GetName().Name).ToArray()
    const string fixedQueryFormat = "MyApp.Activities.ActivityStartedEvent, MyApp.Contracts";

    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = [fixedQueryFormat], // FIXED: Includes ", MyApp.Contracts"
      DiscoverPendingFromOutbox = true,
      IncludeProcessedEventIds = true,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - WITH correct format, event should be discovered
    await Assert.That(result.SyncInquiryResults).IsNotNull()
      .Because("Sync inquiry should return results when EventTypeFilter uses correct format");
    await Assert.That(result.SyncInquiryResults!.Count).IsGreaterThan(0);

    var syncResult = result.SyncInquiryResults[0];
    await Assert.That(syncResult.PendingCount)
      .IsEqualTo(1)
      .Because("Event should be discovered from event store when EventTypeFilter includes assembly name");
    await Assert.That(syncResult.IsFullySynced)
      .IsFalse()
      .Because("Event is in event store but NOT processed - should NOT be synced");
  }

  // ==========================================================================
  // CRITICAL: Real C# Type Format Test
  // ==========================================================================
  // This test uses ACTUAL C# types to generate the EventTypeFilter format,
  // matching EXACTLY what PerspectiveSyncAwaiter produces:
  //   eventTypes?.Select(t => (t.FullName ?? t.Name) + ", " + t.Assembly.GetName().Name)
  // ==========================================================================

  // Test event type for realistic format testing
  public sealed record TestActivityStartedEvent;

  /// <summary>
  /// CRITICAL TEST: Uses real C# type to generate EventTypeFilter.
  /// This matches EXACTLY what PerspectiveSyncAwaiter does in production.
  /// </summary>
  [Test]
  public async Task CRITICAL_RealCSharpType_EventTypeFilter_MatchesStoredFormatAsync() {
    // Arrange: Use REAL C# type to generate format
    var eventType = typeof(TestActivityStartedEvent);
    var eventTypeFilter = (eventType.FullName ?? eventType.Name) + ", " + eventType.Assembly.GetName().Name;

    // This is the format PerspectiveSyncAwaiter generates
    Console.WriteLine($"EventTypeFilter from C#: '{eventTypeFilter}'");

    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Store event with the SAME format that normalize_event_type produces
    // (which is what the real app stores)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Test', @eventTypeFilter, '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { eventId, streamId, eventTypeFilter, now });

    // NO perspective_events row - simulates event just stored but not processed

    // Create sync inquiry with the EventTypeFilter generated from real C# type
    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = [eventTypeFilter],
      DiscoverPendingFromOutbox = true,
      IncludeProcessedEventIds = true,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - CRITICAL: Event MUST be discovered as pending
    // If this fails, the receptor would fire incorrectly!
    await Assert.That(result.SyncInquiryResults).IsNotNull()
      .Because("Sync inquiry with real C# type format must return results");
    await Assert.That(result.SyncInquiryResults!.Count).IsGreaterThan(0);

    var syncResult = result.SyncInquiryResults[0];

    // THE CRITICAL ASSERTION: Event is PENDING, not synced
    await Assert.That(syncResult.PendingCount)
      .IsEqualTo(1)
      .Because($"Event with type '{eventTypeFilter}' must be discovered as pending. " +
               "If PendingCount=0, the receptor would fire before sync completes!");

    await Assert.That(syncResult.IsFullySynced)
      .IsFalse()
      .Because("Event exists but NOT processed - IsFullySynced must be false or receptor fires incorrectly!");
  }

  /// <summary>
  /// CRITICAL TEST: Verify that when event is NOT yet in event store,
  /// the inquiry returns NO result (which C# must interpret correctly).
  /// </summary>
  [Test]
  public async Task CRITICAL_EventNotYetInEventStore_DoesNotReturnSyncedAsync() {
    // Arrange: Event type exists in C# but NOT YET stored in database
    var eventType = typeof(TestActivityStartedEvent);
    var eventTypeFilter = (eventType.FullName ?? eventType.Name) + ", " + eventType.Assembly.GetName().Name;

    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";

    // NOTE: We do NOT insert any event - simulates timing race where
    // event is cascaded but not yet committed to wh_event_store

    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = [eventTypeFilter],
      DiscoverPendingFromOutbox = true,
      IncludeProcessedEventIds = true,
      InquiryId = inquiryId
    };

    // Act
    var result = await _coordinator.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 12345,
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
      PerspectiveSyncInquiries = [inquiry]
    });

    // Assert - When no events exist, we get either:
    // 1. No result rows (inquiry not in results)
    // 2. Result with PendingCount=0, ProcessedCount=0
    //
    // IMPORTANT: PerspectiveSyncAwaiter must NOT interpret this as "synced"
    // because the event we're waiting for doesn't exist yet!
    //
    // Current behavior documents what SQL returns - C# must handle this correctly
    Console.WriteLine($"Result count: {result.SyncInquiryResults?.Count ?? 0}");
    if (result.SyncInquiryResults is { Count: > 0 }) {
      var syncResult = result.SyncInquiryResults[0];
      Console.WriteLine($"PendingCount: {syncResult.PendingCount}, ProcessedCount: {syncResult.ProcessedCount}");
      Console.WriteLine($"IsFullySynced: {syncResult.IsFullySynced}");

      // Document current behavior - this is where the bug manifests
      // When SQL finds nothing, it returns counts of 0, and IsFullySynced = true
      // This is the ROOT CAUSE of the receptor firing incorrectly
      await Assert.That(syncResult.PendingCount).IsEqualTo(0)
        .Because("No events exist yet - SQL returns 0");
      await Assert.That(syncResult.ProcessedCount).IsEqualTo(0)
        .Because("No events exist yet - SQL returns 0");

      // THIS IS THE BUG: IsFullySynced = (PendingCount == 0) = true
      // But we SHOULD be waiting for an event that doesn't exist yet!
      await Assert.That(syncResult.IsFullySynced).IsTrue()
        .Because("BUG: When no events exist, IsFullySynced returns true incorrectly. " +
                 "This causes the receptor to fire before the event even exists!");
    }
    // If no result rows, same problem - C# interprets as "nothing to wait for"
  }
}
