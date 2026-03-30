using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Integration tests for sync inquiry functionality in the process_work_batch SQL function
/// using EF Core work coordinator.
/// </summary>
/// <remarks>
/// These tests verify the cross-scope perspective sync scenario where:
/// - Request 1: Handler emits event (stored in wh_event_store)
/// - Request 2: Different handler with [AwaitPerspectiveSync] needs to wait
/// - Event may NOT yet be in wh_perspective_events (worker hasn't picked it up)
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public class SyncInquiryIntegrationTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new();
  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator = null!;

  [Before(Test)]
  public async Task SetupCoordinatorAsync() {
    // Wait for base setup to complete
    await Task.CompletedTask;

    // Create coordinator with test DbContext
    _coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      CreateDbContext(),
      InfrastructureJsonContext.Default.Options
    );
  }

  // ==========================================================================
  // Cross-Scope Sync Tests (DiscoverPendingFromOutbox)
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

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

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

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

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

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

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

    // Assert - Only ActivityStartedEvent should be counted
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];

    await Assert.That(syncResult.PendingCount).IsEqualTo(1);
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

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

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

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

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

    // Assert - ProcessedEventIds should contain event1Id
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];

    await Assert.That(syncResult.PendingCount).IsEqualTo(1); // event2
    await Assert.That(syncResult.ProcessedCount).IsEqualTo(1); // event1
    await Assert.That(syncResult.ProcessedEventIds).IsNotNull();
    await Assert.That(syncResult.ProcessedEventIds!.Length).IsEqualTo(1);
    await Assert.That(syncResult.ProcessedEventIds).Contains(event1Id);
  }

  // ==========================================================================
  // BUGFIX TESTS: Real Application Format (with assembly-qualified names)
  // ==========================================================================

  /// <summary>
  /// BUG REPRODUCTION TEST: Uses REAL format as stored by normalize_event_type().
  /// In real applications, events are stored with the format "Namespace.TypeName, AssemblyName".
  /// This test verifies that the EventTypeFilter matches the stored format.
  /// </summary>
  [Test]
  public async Task BUGFIX_RealFormat_EventTypeFilterMatchesStoredFormatAsync() {
    // Arrange: Use REAL format as stored by normalize_event_type() in production
    // This is the format that EnvelopeSerializer sends and SQL normalizes
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    // REAL FORMAT: "Namespace.EventType, AssemblyName"
    // This is what normalize_event_type() produces from AssemblyQualifiedName
    const string storedEventType = "ChatActivities.Contracts.StartedEvent, ChatActivities.Contracts";

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Insert event with REAL format (as would be stored via normalize_event_type)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Activity', @eventType, '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { eventId, streamId, eventType = storedEventType, now });

    // EventTypeFilter format: What PerspectiveSyncAwaiter.WaitForStreamAsync produces
    // BUG FIX: Must match format "FullName, AssemblyName"
    const string filterEventType = "ChatActivities.Contracts.StartedEvent, ChatActivities.Contracts";

    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = [filterEventType],
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

    // Assert - Event should be discovered as pending
    // If this test FAILS (PendingCount = 0), the EventTypeFilter doesn't match stored format
    await Assert.That(result.SyncInquiryResults).IsNotNull()
      .Because("Sync inquiry should return a result");

    var syncResult = result.SyncInquiryResults![0];

    await Assert.That(syncResult.PendingCount).IsEqualTo(1)
      .Because($"Event with type '{storedEventType}' should be discovered when filtering with '{filterEventType}'");
    await Assert.That(syncResult.IsFullySynced).IsFalse()
      .Because("Event is not yet processed");
  }

  /// <summary>
  /// REGRESSION TEST: Verify the BUG scenario where filter format doesn't match.
  /// Before the fix, EventTypeFilter was "Namespace.TypeName" (missing ", AssemblyName").
  /// </summary>
  [Test]
  public async Task BUGREPRO_WrongFormat_EventTypeFilterWithoutAssemblyDoesNotMatchAsync() {
    // Arrange: Use REAL stored format but WRONG filter format (before the fix)
    var instanceId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    const string perspectiveName = "TestPerspective";
    var now = DateTimeOffset.UtcNow;

    // REAL stored format
    const string storedEventType = "ChatActivities.Contracts.StartedEvent, ChatActivities.Contracts";

    // BUG: Old format (before fix) - just FullName without assembly
    const string buggyFilterFormat = "ChatActivities.Contracts.StartedEvent";

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'Activity', @eventType, '{}'::jsonb, '{}'::jsonb, 1, @now)",
      new { eventId, streamId, eventType = storedEventType, now });

    var inquiryId = _idProvider.NewGuid();
    var inquiry = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      EventTypeFilter = [buggyFilterFormat],  // WRONG format
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

    // Assert - Event should NOT be discovered because filter format doesn't match stored format
    // This documents the BUG that existed before the fix
    if (result.SyncInquiryResults is { Count: > 0 }) {
      var syncResult = result.SyncInquiryResults[0];
      // The buggy format doesn't match, so no events found = PendingCount = 0
      // This causes IsFullySynced = true (FALSE POSITIVE!)
      await Assert.That(syncResult.PendingCount).IsEqualTo(0)
        .Because("Wrong filter format 'TypeName' doesn't match stored format 'TypeName, Assembly'");
    }
    // If no results, that also means no match (same bug)
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

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

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
      EventIds = [explicitEventId],
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

    // Assert - Should only check the explicit EventId, not discover otherEventId
    await Assert.That(result.SyncInquiryResults).IsNotNull();
    var syncResult = result.SyncInquiryResults![0];

    // explicitEventId is processed, so PendingCount = 0
    await Assert.That(syncResult.PendingCount).IsEqualTo(0);
    await Assert.That(syncResult.ProcessedCount).IsEqualTo(1);
    await Assert.That(syncResult.IsFullySynced).IsTrue();
  }
}
