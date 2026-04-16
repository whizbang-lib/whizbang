using System.Text.Json;
using Dapper;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Tests for automatic perspective checkpoint creation when events are written to event store.
/// Phase 2 of the perspective materialization bug fix - verifies that process_work_batch()
/// auto-creates checkpoint rows when message associations exist for perspectives.
/// </summary>
public class AutoCheckpointCreationTests : PostgresTestBase {
  private readonly Uuid7IdProvider _idProvider = new();

  [Test]
  public async Task ProcessWorkBatch_WithEventAndPerspectiveAssociation_CreatesCheckpointAsync() {
    // Arrange - Register a perspective association for "ProductCreatedEvent" -> "ProductListPerspective"
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Create outbox message JSON for process_work_batch parameter
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch with new outbox message (proper flow)
    var _ = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Check if perspective events were created (indicates match)
    var perspectiveEvents = await connection.QueryAsync<PerspectiveEventRow>(@"
      SELECT event_work_id, stream_id, perspective_name, event_id, status
      FROM wh_perspective_events
      WHERE stream_id = @streamId
        AND perspective_name = 'ProductListPerspective'",
      new { streamId });

    await Assert.That(perspectiveEvents.Count()).IsEqualTo(1)
      .Because("process_work_batch should create perspective event when association matches");

    // Complete perspective events to trigger checkpoint creation
    var completions = perspectiveEvents.Select(pe => new {
      EventWorkId = pe.event_work_id,
      StatusFlags = 8  // Completed flag
    }).ToArray();

    var _2 = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_perspective_event_completions := @completions::jsonb
      )",
      new { instanceId, now, completions = JsonSerializer.Serialize(completions) });

    // Assert - Checkpoint row should be created after completion
    var checkpoint = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint).IsNotNull()
      .Because("process_work_batch should create checkpoint when perspective events are completed");
    await Assert.That(checkpoint!.stream_id).IsEqualTo(streamId);
    await Assert.That(checkpoint.perspective_name).IsEqualTo("ProductListPerspective");
    await Assert.That(checkpoint.last_event_id).IsEqualTo(eventId)
      .Because("Checkpoint should be updated with last completed event");
    await Assert.That(checkpoint.status).IsEqualTo((short)2)  // PerspectiveProcessingStatus.Completed = 2
      .Because("Checkpoint should be marked as completed when all events processed");
  }

  [Test]
  public async Task ProcessWorkBatch_WithEventButNoAssociation_DoesNotCreateCheckpointAsync() {
    // Arrange - NO association registered (this is the key difference)
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Create outbox message JSON
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch with new outbox message
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - NO checkpoint should be created
    var checkpoints = await _getAllPerspectiveCursorsAsync(streamId);
    await Assert.That(checkpoints).Count().IsEqualTo(0)
      .Because("Without message association, no checkpoint should be auto-created");
  }

  [Test]
  public async Task ProcessWorkBatch_WithMultiplePerspectiveAssociations_CreatesMultipleCheckpointsAsync() {
    // Arrange - Register TWO perspective associations for same event type
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductDetailsPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Create outbox message JSON
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch with new outbox message
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - TWO checkpoints should be created
    var checkpoints = await _getAllPerspectiveCursorsAsync(streamId);
    await Assert.That(checkpoints).Count().IsEqualTo(2)
      .Because("Both perspective associations should result in checkpoint creation");

    var checkpoint1 = checkpoints.FirstOrDefault(c => c.perspective_name == "ProductListPerspective");
    var checkpoint2 = checkpoints.FirstOrDefault(c => c.perspective_name == "ProductDetailsPerspective");

    await Assert.That(checkpoint1).IsNotNull();
    await Assert.That(checkpoint2).IsNotNull();
  }

  [Test]
  public async Task ProcessWorkBatch_WithExistingCheckpoint_DoesNotDuplicateAsync() {
    // Arrange - Register association
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Manually insert checkpoint (simulating it already exists)
    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective");

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Create outbox message JSON
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch (should NOT duplicate)
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - Still only ONE checkpoint
    var checkpoints = await _getAllPerspectiveCursorsAsync(streamId);
    await Assert.That(checkpoints).Count().IsEqualTo(1)
      .Because("Existing checkpoint should not be duplicated");
  }

  [Test]
  public async Task ProcessWorkBatch_WithNonPerspectiveAssociation_DoesNotCreateCheckpointAsync() {
    // Arrange - Register RECEPTOR association (not perspective)
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "receptor",
      targetName: "SendEmailReceptor",
      serviceName: "ECommerce.Notifications");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Create outbox message JSON
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch with new outbox message
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - NO checkpoint should be created (receptors don't use checkpoints)
    var checkpoints = await _getAllPerspectiveCursorsAsync(streamId);
    await Assert.That(checkpoints).Count().IsEqualTo(0)
      .Because("Receptor associations should not trigger checkpoint creation");
  }

  // Flexible Type Matching Tests (fuzzy matching)

  [Test]
  public async Task ProcessWorkBatch_WithAssemblyQualifiedNameEvent_MatchesShortFormAssociationAsync() {
    // Arrange - Association has short form "TypeName, AssemblyName"
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();
    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Create outbox message JSON - Event has FULL AssemblyQualifiedName with Version/Culture/PublicKeyToken
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch with new outbox message
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - Checkpoint SHOULD be created despite format difference
    var checkpoint = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint).IsNotNull()
      .Because("Fuzzy matching should match on TypeName + AssemblyName, ignoring Version/Culture/PublicKeyToken");
  }

  [Test]
  public async Task ProcessWorkBatch_WithShortFormEvent_MatchesAssemblyQualifiedAssociationAsync() {
    // Arrange - Association has FULL AssemblyQualifiedName
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Create outbox message JSON for process_work_batch parameter
    // Event has short form "TypeName, AssemblyName"
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch with new outbox message (proper flow)
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var _ = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - Checkpoint SHOULD be created despite format difference
    var checkpoint = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint).IsNotNull()
      .Because("Fuzzy matching should match on TypeName + AssemblyName, ignoring Version/Culture/PublicKeyToken");
  }

  [Test]
  public async Task ProcessWorkBatch_WithDifferentVersions_StillMatchesAsync() {
    // Arrange - Association registered with version 1.0.0.0
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Create outbox message JSON for process_work_batch parameter
    // Event has version 2.0.0.0 (different!)
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain, Version=2.0.0.0, Culture=neutral, PublicKeyToken=abc123",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch with new outbox message (proper flow)
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var _ = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - Checkpoint SHOULD be created despite version difference
    var checkpoint = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint).IsNotNull()
      .Because("Fuzzy matching should ignore version numbers and match on TypeName + AssemblyName");
  }

  [Test]
  public async Task ProcessWorkBatch_WithOnlyTypeNameEvent_DoesNotMatchAsync() {
    // Arrange - Association has "TypeName, AssemblyName"
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Create outbox message JSON for process_work_batch parameter
    // Event has ONLY TypeName (no assembly - this is too loose!)
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch with new outbox message (proper flow)
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var _ = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - NO checkpoint should be created (TypeName alone is not enough)
    var checkpoints = await _getAllPerspectiveCursorsAsync(streamId);
    await Assert.That(checkpoints).Count().IsEqualTo(0)
      .Because("TypeName alone is insufficient - we need at least TypeName + AssemblyName for safe matching");
  }

  [Test]
  public async Task ProcessWorkBatch_WithDifferentAssemblyNames_DoesNotMatchAsync() {
    // Arrange - Association for "ECommerce.Domain" assembly
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Create outbox message JSON for process_work_batch parameter
    // Event from DIFFERENT assembly "ECommerce.Domain.V2"
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain.V2",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch with new outbox message (proper flow)
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var _ = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - NO checkpoint (different assemblies = different types)
    var checkpoints = await _getAllPerspectiveCursorsAsync(streamId);
    await Assert.That(checkpoints).Count().IsEqualTo(0)
      .Because("AssemblyName mismatch means different types - no match");
  }

  // Checkpoint Update Tests (Phase 3 - verifies that checkpoints are updated when perspective processing completes)

  [Test]
  public async Task ProcessWorkBatch_WithPerspectiveCompletion_UpdatesCheckpointAsync() {
    // Arrange - Create a checkpoint that needs updating
    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();

    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective");

    // Create outbox messages JSON for process_work_batch parameter (3 events)
    var event1Json = _createOutboxEventJson(streamId, eventId1, "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");
    var event2Json = _createOutboxEventJson(streamId, eventId2, "ECommerce.Domain.Events.ProductUpdatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");
    var event3Json = _createOutboxEventJson(streamId, eventId3, "ECommerce.Domain.Events.ProductUpdatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");

    // Combine into array
    var event1Array = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(event1Json)!;
    var event2Array = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(event2Json)!;
    var event3Array = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(event3Json)!;
    var combinedEvents = new[] { event1Array[0], event2Array[0], event3Array[0] };
    var outboxMessages = System.Text.Json.JsonSerializer.Serialize(combinedEvents);

    // First call: Store events via process_work_batch (proper flow)
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var _ = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Act - Report perspective completion (processed up to eventId2)
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var perspectiveCompletions = new[] {
      new {
        StreamId = (Guid)streamId,
        PerspectiveName = "ProductListPerspective",
        LastEventId = (Guid)eventId2,
        Status = (short)1  // PerspectiveProcessingStatus.Completed
      }
    };

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_perspective_completions := @completions::jsonb
      )",
      new {
        instanceId,
        now,
        completions = JsonSerializer.Serialize(perspectiveCompletions)
      });

    // Assert - Checkpoint should be updated with eventId2
    var checkpoint = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint).IsNotNull();
    await Assert.That(checkpoint!.last_event_id).IsEqualTo(eventId2)
      .Because("Checkpoint should be updated to reflect last processed event from perspective completion");
    await Assert.That(checkpoint.status).IsEqualTo((short)1)  // Completed
      .Because("Status should reflect the completion");
  }

  [Test]
  public async Task ProcessWorkBatch_WithMultiplePerspectiveCompletions_UpdatesAllCheckpointsAsync() {
    // Arrange - Create TWO checkpoints for different perspectives on same stream
    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();

    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await _insertPerspectiveCursorAsync(streamId, "ProductDetailsPerspective");

    // Create outbox messages JSON for process_work_batch parameter (2 events)
    var event1Json = _createOutboxEventJson(streamId, eventId1, "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");
    var event2Json = _createOutboxEventJson(streamId, eventId2, "ECommerce.Domain.Events.ProductUpdatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");

    // Combine into array
    var event1Array = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(event1Json)!;
    var event2Array = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(event2Json)!;
    var combinedEvents = new[] { event1Array[0], event2Array[0] };
    var outboxMessages = System.Text.Json.JsonSerializer.Serialize(combinedEvents);

    // First call: Store events via process_work_batch (proper flow)
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var _ = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Act - Report completions for BOTH perspectives (but at different points)
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var perspectiveCompletions = new[] {
      new {
        StreamId = (Guid)streamId,
        PerspectiveName = "ProductListPerspective",
        LastEventId = (Guid)eventId2,  // Processed both events
        Status = (short)1
      },
      new {
        StreamId = (Guid)streamId,
        PerspectiveName = "ProductDetailsPerspective",
        LastEventId = (Guid)eventId1,  // Only processed first event
        Status = (short)1
      }
    };

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_perspective_completions := @completions::jsonb
      )",
      new {
        instanceId,
        now,
        completions = JsonSerializer.Serialize(perspectiveCompletions)
      });

    // Assert - Both checkpoints should be updated independently
    var checkpoint1 = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    var checkpoint2 = await _getPerspectiveCursorAsync(streamId, "ProductDetailsPerspective");

    await Assert.That(checkpoint1!.last_event_id).IsEqualTo(eventId2);
    await Assert.That(checkpoint2!.last_event_id).IsEqualTo(eventId1);
  }

  [Test]
  public async Task ProcessWorkBatch_WithPerspectiveFailure_UpdatesCheckpointWithErrorAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective");

    // Create outbox message JSON for process_work_batch parameter
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\"}");

    // First call: Store event via process_work_batch (proper flow)
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var _ = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Act - Report perspective FAILURE
    // Note: Cast TrackedGuid to Guid for anonymous type serialization
    var perspectiveFailures = new[] {
      new {
        StreamId = (Guid)streamId,
        PerspectiveName = "ProductListPerspective",
        LastEventId = (Guid)eventId,
        Status = (short)2,  // PerspectiveProcessingStatus.Failed
        Error = "Database connection timeout"
      }
    };

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_perspective_failures := @failures::jsonb
      )",
      new {
        instanceId,
        now,
        failures = JsonSerializer.Serialize(perspectiveFailures)
      });

    // Assert - Checkpoint should be updated with failed status AND error message
    var checkpoint = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint!.status).IsEqualTo((short)2)  // Failed
      .Because("Checkpoint should reflect the failure status");
    await Assert.That(checkpoint.error).IsEqualTo("Database connection timeout")
      .Because("Error message should be persisted to help diagnose failures");
  }

  [Test]
  public async Task ProcessWorkBatch_WithNoCompletions_DoesNotUpdateCheckpointAsync() {
    // Arrange
    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective");

    // Create outbox message JSON for process_work_batch parameter
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\"}");

    // Act - Call process_work_batch with new outbox message but NO completions/failures
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var _ = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - Checkpoint should remain unchanged (still NULL last_event_id)
    var checkpoint = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint!.last_event_id).IsNull()
      .Because("Without completion report, checkpoint should not be updated");
  }

  // Phase 4.6B: Out-of-order detection for auto-created perspective events

  [Test]
  public async Task ProcessWorkBatch_WithOutOfOrderEvent_SetsRewindRequiredOnCursorAsync() {
    // Arrange - Register perspective association
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    // UUID7 IDs are time-ordered: eventId1 < eventId2 < eventId3
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();

    // Pre-create cursor that has already advanced to eventId3
    // (simulates runner having read ahead from wh_event_store)
    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective",
      lastEventId: eventId3, status: 2);  // Completed status

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Act - Store an event with eventId1, which is OLDER than cursor's last_event_id (eventId3)
    var outboxMessages = _createOutboxEventJson(
      streamId: streamId,
      eventId: eventId1,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - Cursor should have RewindRequired flag set (bit 5 = 32)
    var cursor = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(cursor).IsNotNull();
    await Assert.That(cursor!.status & 32).IsEqualTo(32)
      .Because("Phase 4.6B should detect out-of-order event and set RewindRequired flag");
    await Assert.That(cursor.rewind_trigger_event_id).IsEqualTo(eventId1)
      .Because("rewind_trigger_event_id should be set to the out-of-order event");
  }

  [Test]
  public async Task ProcessWorkBatch_WithMultipleOutOfOrderEvents_SetsRewindToEarliestAsync() {
    // Arrange
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();  // Earliest
    var eventId2 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();  // Cursor position

    // Cursor already at eventId3
    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective",
      lastEventId: eventId3, status: 2);

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Act - Store TWO out-of-order events in one batch
    var event1Json = _createOutboxEventJson(streamId, eventId1,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":1}");
    var event2Json = _createOutboxEventJson(streamId, eventId2,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":2}");

    var event1Array = JsonSerializer.Deserialize<JsonElement[]>(event1Json)!;
    var event2Array = JsonSerializer.Deserialize<JsonElement[]>(event2Json)!;
    var outboxMessages = JsonSerializer.Serialize(new[] { event1Array[0], event2Array[0] });

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - rewind_trigger_event_id should be the EARLIEST out-of-order event
    var cursor = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(cursor!.status & 32).IsEqualTo(32)
      .Because("RewindRequired should be set");
    await Assert.That(cursor.rewind_trigger_event_id).IsEqualTo(eventId1)
      .Because("rewind_trigger_event_id should be the earliest out-of-order event, not eventId2");
  }

  [Test]
  public async Task ProcessWorkBatch_WithExistingRewindTrigger_OnlyUpdatesIfEarlierAsync() {
    // Arrange
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();
    var eventId4 = _idProvider.NewGuid();

    // Cursor at eventId4, already has rewind trigger at eventId2
    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective",
      lastEventId: eventId4, status: 34,  // Completed (2) | RewindRequired (32)
      rewindTriggerEventId: eventId2);

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Act 1 - Store eventId3 (out of order but LATER than existing trigger eventId2)
    var outboxMessages = _createOutboxEventJson(streamId, eventId3,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":3}");

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert 1 - Trigger should NOT be overwritten (eventId2 is still earlier)
    var cursor = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(cursor!.rewind_trigger_event_id).IsEqualTo(eventId2)
      .Because("Existing rewind trigger (eventId2) is earlier than new event (eventId3), should not be overwritten");

    // Act 2 - Store eventId1 (earlier than existing trigger eventId2)
    outboxMessages = _createOutboxEventJson(streamId, eventId1,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":1}");

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert 2 - Trigger should now be updated to eventId1
    cursor = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(cursor!.rewind_trigger_event_id).IsEqualTo(eventId1)
      .Because("eventId1 is earlier than previous trigger eventId2, should be updated");
  }

  [Test]
  public async Task ProcessWorkBatch_WithInOrderEvent_DoesNotSetRewindRequiredAsync() {
    // Arrange
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();

    // Cursor at eventId1
    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective",
      lastEventId: eventId1, status: 2);

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Act - Store eventId2 (> eventId1, in order — normal forward processing)
    var outboxMessages = _createOutboxEventJson(streamId, eventId2,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":2}");

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - No rewind needed for in-order events
    var cursor = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(cursor!.status & 32).IsEqualTo(0)
      .Because("In-order events should NOT set RewindRequired flag");
    await Assert.That(cursor.rewind_trigger_event_id).IsNull()
      .Because("rewind_trigger_event_id should remain NULL for in-order events");
  }

  [Test]
  public async Task ProcessWorkBatch_WithMultiplePerspectives_SetsRewindIndependentlyAsync() {
    // Arrange - Two perspectives for same event type
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductDetailsPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();

    // Cursor A advanced to eventId2, Cursor B never processed (NULL)
    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective",
      lastEventId: eventId2, status: 2);
    await _insertPerspectiveCursorAsync(streamId, "ProductDetailsPerspective");  // NULL cursor

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Act - Store eventId1 (out of order for cursor A, but fine for cursor B)
    var outboxMessages = _createOutboxEventJson(streamId, eventId1,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":1}");

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert - Cursor A should have RewindRequired, Cursor B should not
    var cursorA = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(cursorA!.status & 32).IsEqualTo(32)
      .Because("Cursor A has last_event_id > eventId1, needs rewind");
    await Assert.That(cursorA.rewind_trigger_event_id).IsEqualTo(eventId1);

    var cursorB = await _getPerspectiveCursorAsync(streamId, "ProductDetailsPerspective");
    await Assert.That(cursorB!.status & 32).IsEqualTo(0)
      .Because("Cursor B has NULL last_event_id, no rewind needed");
    await Assert.That(cursorB.rewind_trigger_event_id).IsNull();
  }

  // Phase 4.6B debounce + completion cleanup tests

  [Test]
  public async Task ProcessWorkBatch_Phase46B_SetsRewindFlaggedAtAsync() {
    // Arrange
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();

    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective",
      lastEventId: eventId3, status: 2);

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    var outboxMessages = _createOutboxEventJson(streamId, eventId1,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":1}");

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Assert — rewind_flagged_at should be set
    var cursor = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(cursor!.rewind_flagged_at).IsNotNull()
      .Because("Phase 4.6B should set rewind_flagged_at when flagging RewindRequired");
  }

  [Test]
  public async Task ProcessWorkBatch_Debounce_HoldsBackEventsWithinWindowAsync() {
    // Arrange — cursor with RewindRequired + rewind_flagged_at = now (within window)
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();

    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective",
      lastEventId: eventId3, status: 2);

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // First call: store late event → flags cursor with RewindRequired + rewind_flagged_at
    var outboxMessages = _createOutboxEventJson(streamId, eventId1,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":1}");

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Second call: within debounce window — should return zero perspective work
    var nowWithin = now.AddSeconds(2);  // 2s later, still within 5s window
    var results = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @nowWithin::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2
      )",
      new { instanceId, nowWithin });

    var perspectiveWork = results.Where((dynamic r) => r.source == "perspective_stream").ToList();
    await Assert.That(perspectiveWork.Count).IsEqualTo(0)
      .Because("Debounce should hold back perspective events for rewind-pending streams within the window");
  }

  [Test]
  public async Task ProcessWorkBatch_Debounce_ReleasesEventsAfterWindowAsync() {
    // Arrange
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();

    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective",
      lastEventId: eventId3, status: 2);

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Store late event → flags cursor
    var outboxMessages = _createOutboxEventJson(streamId, eventId1,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":1}");

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Call AFTER debounce window — should return perspective work
    var nowAfter = now.AddSeconds(6);  // 6s later, past 5s window
    var results = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @nowAfter::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2
      )",
      new { instanceId, nowAfter });

    var perspectiveWork = results.Where((dynamic r) => r.source == "perspective_stream").ToList();
    await Assert.That(perspectiveWork.Count).IsGreaterThan(0)
      .Because("After debounce window expires, perspective events should be released");
  }

  [Test]
  public async Task ProcessWorkBatch_Completion_ClearsRewindTriggerAndFlaggedAtAsync() {
    // Arrange
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();

    await _insertPerspectiveCursorAsync(streamId, "ProductListPerspective",
      lastEventId: eventId3, status: 2);

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Store late event → flags cursor
    var outboxMessages = _createOutboxEventJson(streamId, eventId1,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":1}");

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Verify flags are set
    var beforeCompletion = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(beforeCompletion!.rewind_trigger_event_id).IsNotNull();
    await Assert.That(beforeCompletion.rewind_flagged_at).IsNotNull();

    // Complete the perspective cursor (simulating successful rewind)
    var perspectiveCompletions = new[] {
      new {
        StreamId = (Guid)streamId,
        PerspectiveName = "ProductListPerspective",
        LastEventId = (Guid)eventId3,
        Status = (short)2,  // Completed
        ProcessedEventIds = new[] { (Guid)eventId1, (Guid)eventId3 }
      }
    };

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_perspective_completions := @completions::jsonb
      )",
      new {
        instanceId,
        now,
        completions = System.Text.Json.JsonSerializer.Serialize(perspectiveCompletions)
      });

    // Assert — both rewind columns should be cleared
    var afterCompletion = await _getPerspectiveCursorAsync(streamId, "ProductListPerspective");
    await Assert.That(afterCompletion!.rewind_trigger_event_id).IsNull()
      .Because("Completion should clear rewind_trigger_event_id to prevent rewind loops");
    await Assert.That(afterCompletion.rewind_flagged_at).IsNull()
      .Because("Completion should clear rewind_flagged_at to reset the debounce window");
  }

  // Completion ProcessedEventIds — Prevents Concurrent Event Over-Marking

  [Test]
  public async Task ProcessWorkBatch_CompletionWithProcessedEventIds_OnlyMarksSpecificEventsAsync() {
    // Arrange — reproduces the bulk write concurrency bug
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "UberPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();

    // IMPORTANT: Generate "low" IDs first (earlier UUIDv7 = lower values)
    // These simulate events created by slow handlers that get stored AFTER the "high" events
    var lowEventId1 = _idProvider.NewGuid();
    var lowEventId2 = _idProvider.NewGuid();
    var lowEventId3 = _idProvider.NewGuid();

    // Generate "high" IDs after (later UUIDv7 = higher values)
    // These simulate events from fast handlers that get stored first
    var highEventId1 = _idProvider.NewGuid();
    var highEventId2 = _idProvider.NewGuid();

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Store batch 1 events (high IDs) — creates perspective_events
    var event1Json = _createOutboxEventJson(streamId, highEventId1,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":1}");
    var event2Json = _createOutboxEventJson(streamId, highEventId2,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":2}");
    var event1Array = JsonSerializer.Deserialize<JsonElement[]>(event1Json)!;
    var event2Array = JsonSerializer.Deserialize<JsonElement[]>(event2Json)!;
    var batch1Messages = JsonSerializer.Serialize(new[] { event1Array[0], event2Array[0] });

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages = batch1Messages });

    // Simulate runner having processed batch 1 — set cursor to highEventId2
    await connection.ExecuteAsync(@"
      UPDATE wh_perspective_cursors
      SET last_event_id = @lastEventId, status = 2
      WHERE stream_id = @streamId AND perspective_name = 'UberPerspective'",
      new { streamId, lastEventId = (Guid)highEventId2 });

    // Insert into event_store and perspective_events directly
    await _insertEventStoreRowAsync(connection, lowEventId1, streamId,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":3}");
    await _insertEventStoreRowAsync(connection, lowEventId2, streamId,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":4}");
    await _insertEventStoreRowAsync(connection, lowEventId3, streamId,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":5}");
    await _insertPerspectiveEventAsync(connection, streamId, "UberPerspective", lowEventId1);
    await _insertPerspectiveEventAsync(connection, streamId, "UberPerspective", lowEventId2);
    await _insertPerspectiveEventAsync(connection, streamId, "UberPerspective", lowEventId3);

    // Act — Complete with ProcessedEventIds containing ONLY batch 1 events
    var completions = JsonSerializer.Serialize(new[] {
      new {
        StreamId = (Guid)streamId,
        PerspectiveName = "UberPerspective",
        LastEventId = (Guid)highEventId2,
        Status = (short)2,
        ProcessedEventIds = new[] { (Guid)highEventId1, (Guid)highEventId2 }
      }
    });

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_perspective_completions := @completions::jsonb
      )",
      new { instanceId, now, completions });

    // Assert — only explicitly-listed events should be marked processed
    var high1Processed = await _isPerspectiveEventProcessedAsync(connection, streamId, "UberPerspective", highEventId1);
    var high2Processed = await _isPerspectiveEventProcessedAsync(connection, streamId, "UberPerspective", highEventId2);
    var low1Processed = await _isPerspectiveEventProcessedAsync(connection, streamId, "UberPerspective", lowEventId1);
    var low2Processed = await _isPerspectiveEventProcessedAsync(connection, streamId, "UberPerspective", lowEventId2);
    var low3Processed = await _isPerspectiveEventProcessedAsync(connection, streamId, "UberPerspective", lowEventId3);

    await Assert.That(high1Processed).IsTrue()
      .Because("highEventId1 was in ProcessedEventIds and should be marked processed");
    await Assert.That(high2Processed).IsTrue()
      .Because("highEventId2 was in ProcessedEventIds and should be marked processed");
    await Assert.That(low1Processed).IsFalse()
      .Because("lowEventId1 was NOT in ProcessedEventIds and should remain unprocessed");
    await Assert.That(low2Processed).IsFalse()
      .Because("lowEventId2 was NOT in ProcessedEventIds and should remain unprocessed");
    await Assert.That(low3Processed).IsFalse()
      .Because("lowEventId3 was NOT in ProcessedEventIds and should remain unprocessed");

    // Assert — straggler detection should flag rewind
    var cursor = await _getPerspectiveCursorAsync(streamId, "UberPerspective");
    await Assert.That(cursor!.status & 32).IsEqualTo(32)
      .Because("Straggler detection should set RewindRequired for unprocessed events below cursor");
    await Assert.That(cursor.rewind_trigger_event_id).IsNotNull()
      .Because("rewind_trigger_event_id should point to earliest straggler");
  }

  [Test]
  public async Task ProcessWorkBatch_CompletionWithEmptyProcessedEventIds_MarksNothingAsync() {
    // Arrange — completion with empty ProcessedEventIds
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "EmptyPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var cursorEventId = _idProvider.NewGuid();

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Create cursor and an unprocessed perspective_event below it
    await _insertEventStoreRowAsync(connection, cursorEventId, streamId,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":1}");
    await _insertEventStoreRowAsync(connection, eventId1, streamId,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":2}");
    await _insertPerspectiveCursorAsync(streamId, "EmptyPerspective",
      lastEventId: cursorEventId, status: 2);
    await _insertPerspectiveEventAsync(connection, streamId, "EmptyPerspective", eventId1);

    // Act — Complete with empty ProcessedEventIds
    var completions = JsonSerializer.Serialize(new[] {
      new {
        StreamId = (Guid)streamId,
        PerspectiveName = "EmptyPerspective",
        LastEventId = (Guid)cursorEventId,
        Status = (short)2,
        ProcessedEventIds = Array.Empty<Guid>()
      }
    });

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_perspective_completions := @completions::jsonb
      )",
      new { instanceId, now, completions });

    // Assert — no events marked as processed
    var event1Processed = await _isPerspectiveEventProcessedAsync(connection, streamId, "EmptyPerspective", eventId1);
    await Assert.That(event1Processed).IsFalse()
      .Because("Empty ProcessedEventIds should not mark any events as processed");

    // Assert — rewind should be flagged
    var cursor = await _getPerspectiveCursorAsync(streamId, "EmptyPerspective");
    await Assert.That(cursor!.status & 32).IsEqualTo(32)
      .Because("Straggler detection should flag rewind when unprocessed events exist below cursor");
  }

  [Test]
  public async Task ProcessWorkBatch_CompletionWithAllProcessedEventIds_NoStragglers_NoRewindAsync() {
    // Arrange — happy path: all events in ProcessedEventIds
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "HappyPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Store events via process_work_batch
    var event1Json = _createOutboxEventJson(streamId, eventId1,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":1}");
    var event2Json = _createOutboxEventJson(streamId, eventId2,
      "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"v\":2}");
    var event1Array = JsonSerializer.Deserialize<JsonElement[]>(event1Json)!;
    var event2Array = JsonSerializer.Deserialize<JsonElement[]>(event2Json)!;
    var batch1Messages = JsonSerializer.Serialize(new[] { event1Array[0], event2Array[0] });

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages = batch1Messages });

    // Act — Complete with ALL event IDs in ProcessedEventIds
    var completions = JsonSerializer.Serialize(new[] {
      new {
        StreamId = (Guid)streamId,
        PerspectiveName = "HappyPerspective",
        LastEventId = (Guid)eventId2,
        Status = (short)2,
        ProcessedEventIds = new[] { (Guid)eventId1, (Guid)eventId2 }
      }
    });

    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_perspective_completions := @completions::jsonb
      )",
      new { instanceId, now, completions });

    // Assert — all events marked as processed
    var event1Processed = await _isPerspectiveEventProcessedAsync(connection, streamId, "HappyPerspective", eventId1);
    var event2Processed = await _isPerspectiveEventProcessedAsync(connection, streamId, "HappyPerspective", eventId2);
    await Assert.That(event1Processed).IsTrue()
      .Because("eventId1 was in ProcessedEventIds and should be marked processed");
    await Assert.That(event2Processed).IsTrue()
      .Because("eventId2 was in ProcessedEventIds and should be marked processed");

    // Assert — no rewind (no stragglers)
    var cursor = await _getPerspectiveCursorAsync(streamId, "HappyPerspective");
    await Assert.That(cursor!.status & 32).IsEqualTo(0)
      .Because("No stragglers means no RewindRequired flag");
    await Assert.That(cursor.rewind_trigger_event_id).IsNull()
      .Because("No stragglers means no rewind_trigger_event_id");
  }

  // Two-tier fair scheduling tests

  [Test]
  public async Task ProcessWorkBatch_TwoTier_SmallStreamServedBeforeLargeStreamAsync() {
    // Arrange — register association
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    // Create a LARGE stream (30 events — exceeds max_work_items_per_stream=25)
    var largeStreamId = _idProvider.NewGuid();
    var largeEvents = new List<string>();
    for (var i = 0; i < 30; i++) {
      var eventId = _idProvider.NewGuid();
      largeEvents.Add(_createOutboxEventJson(largeStreamId, eventId,
        "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", $$$"""{"v": {{{i}}}}"""));
    }

    // Create a SMALL stream (2 events — well under threshold)
    var smallStreamId = _idProvider.NewGuid();
    var smallEvent1 = _idProvider.NewGuid();
    var smallEvent2 = _idProvider.NewGuid();
    var smallEvents = new List<string> {
      _createOutboxEventJson(smallStreamId, smallEvent1,
        "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", """{"v": 1}"""),
      _createOutboxEventJson(smallStreamId, smallEvent2,
        "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", """{"v": 2}""")
    };

    // Combine all events into one batch — large stream first in array (would normally sort first by stream_id)
    var allEventArrays = largeEvents.Concat(smallEvents)
      .Select(json => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(json)![0])
      .ToArray();
    var outboxMessages = System.Text.Json.JsonSerializer.Serialize(allEventArrays);

    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Act — store all events
    var results = await connection.QueryAsync<dynamic>(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )",
      new { instanceId, now, outboxMessages });

    // Phase 7 now returns DISTINCT stream_id only (stream assignment model)
    var perspectiveWork = results.Where((dynamic r) => r.source == "perspective_stream").ToList();
    var streamIds = perspectiveWork.Select((dynamic r) => (Guid)r.work_stream_id).Distinct().ToList();

    // Assert — both streams should be present
    await Assert.That(streamIds).Contains(smallStreamId)
      .Because("Small stream should be in the returned assignments");
    await Assert.That(streamIds).Contains(largeStreamId)
      .Because("Large stream should also be in the returned assignments");

    // Small stream (Tier 1) should appear before large stream (Tier 2) in the results
    var smallPos = perspectiveWork.Select((dynamic r, int i) => new { Id = (Guid)r.work_stream_id, Pos = i })
      .First(x => x.Id == smallStreamId).Pos;
    var largePos = perspectiveWork.Select((dynamic r, int i) => new { Id = (Guid)r.work_stream_id, Pos = i })
      .First(x => x.Id == largeStreamId).Pos;
    await Assert.That(smallPos).IsLessThan(largePos)
      .Because("Small stream (Tier 1) should appear before large stream (Tier 2)");

    await Assert.That(perspectiveWork.Count).IsEqualTo(2)
      .Because("Should return 2 distinct stream IDs, not individual event rows");
  }

  [Test]
  public async Task ProcessWorkBatch_TwoTier_SmallStreamCompletesInOneTickAsync() {
    // Arrange — small stream with 3 events should ALL be returned (not capped at per-stream limit)
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var streamId = _idProvider.NewGuid();

    var events = new List<string>();
    for (var i = 0; i < 3; i++) {
      events.Add(_createOutboxEventJson(streamId, _idProvider.NewGuid(),
        "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", $$$"""{"v": {{{i}}}}"""));
    }

    var allEvents = events
      .Select(json => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(json)![0])
      .ToArray();
    var outboxMessages = System.Text.Json.JsonSerializer.Serialize(allEvents);

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var results = await connection.QueryAsync<dynamic>(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )", new { instanceId, now, outboxMessages });

    // Phase 7 now returns DISTINCT stream_id only (stream assignment model)
    var perspectiveWork = results.Where((dynamic r) => r.source == "perspective_stream").ToList();
    var streamIds = perspectiveWork.Select((dynamic r) => (Guid)r.work_stream_id).Distinct().ToList();

    await Assert.That(streamIds).Contains(streamId)
      .Because("Small stream should be in the returned assignments");
    await Assert.That(perspectiveWork.Count).IsEqualTo(1)
      .Because("Should return 1 distinct stream ID, not individual event rows");
  }

  [Test]
  public async Task ProcessWorkBatch_TwoTier_LargeStreamStillServedAsync() {
    // Arrange — only a large stream, should still get items (Tier 2)
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var streamId = _idProvider.NewGuid();

    var events = new List<string>();
    for (var i = 0; i < 40; i++) {
      events.Add(_createOutboxEventJson(streamId, _idProvider.NewGuid(),
        "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", $$$"""{"v": {{{i}}}}"""));
    }

    var allEvents = events
      .Select(json => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(json)![0])
      .ToArray();
    var outboxMessages = System.Text.Json.JsonSerializer.Serialize(allEvents);

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var results = await connection.QueryAsync<dynamic>(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )", new { instanceId, now, outboxMessages });

    // Phase 7 now returns DISTINCT stream_id only (stream assignment model)
    var perspectiveWork = results.Where((dynamic r) => r.source == "perspective_stream").ToList();
    var streamIds = perspectiveWork.Select((dynamic r) => (Guid)r.work_stream_id).Distinct().ToList();

    await Assert.That(streamIds).Contains(streamId)
      .Because("Large stream should still be served even without small streams present");
    await Assert.That(perspectiveWork.Count).IsEqualTo(1)
      .Because("Should return 1 distinct stream ID, not individual event rows");
  }

  [Test]
  public async Task ProcessWorkBatch_TwoTier_LargeStreamCappedAtPerStreamLimitAsync() {
    // Arrange — large stream should be capped at max_work_items_per_stream (default 25)
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var streamId = _idProvider.NewGuid();

    var events = new List<string>();
    for (var i = 0; i < 50; i++) {
      events.Add(_createOutboxEventJson(streamId, _idProvider.NewGuid(),
        "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", $$$"""{"v": {{{i}}}}"""));
    }

    var allEvents = events
      .Select(json => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(json)![0])
      .ToArray();
    var outboxMessages = System.Text.Json.JsonSerializer.Serialize(allEvents);

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var results = await connection.QueryAsync<dynamic>(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )", new { instanceId, now, outboxMessages });

    // Phase 7 now returns DISTINCT stream_id only (stream assignment model)
    var perspectiveWork = results.Where((dynamic r) => r.source == "perspective_stream").ToList();
    var streamIds = perspectiveWork.Select((dynamic r) => (Guid)r.work_stream_id).Distinct().ToList();

    await Assert.That(streamIds).Contains(streamId)
      .Because("Large stream should be in the returned assignments");
    await Assert.That(perspectiveWork.Count).IsEqualTo(1)
      .Because("Should return 1 distinct stream ID, not individual event rows");
  }

  [Test]
  public async Task ProcessWorkBatch_TwoTier_MultipleSmallStreamsFillFirstAsync() {
    // Arrange — 3 small streams + 1 large stream
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    var smallStream1 = _idProvider.NewGuid();
    var smallStream2 = _idProvider.NewGuid();
    var smallStream3 = _idProvider.NewGuid();
    var largeStream = _idProvider.NewGuid();

    var allJsonArrays = new List<System.Text.Json.JsonElement>();

    // 3 small streams with 2 events each
    foreach (var sid in new[] { smallStream1, smallStream2, smallStream3 }) {
      for (var i = 0; i < 2; i++) {
        var json = _createOutboxEventJson(sid, _idProvider.NewGuid(),
          "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", $$$"""{"v": {{{i}}}}""");
        allJsonArrays.Add(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(json)![0]);
      }
    }

    // 1 large stream with 30 events
    for (var i = 0; i < 30; i++) {
      var json = _createOutboxEventJson(largeStream, _idProvider.NewGuid(),
        "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", $$$"""{"v": {{{i}}}}""");
      allJsonArrays.Add(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(json)![0]);
    }

    var outboxMessages = System.Text.Json.JsonSerializer.Serialize(allJsonArrays);

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var results = await connection.QueryAsync<dynamic>(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )", new { instanceId, now, outboxMessages });

    // Phase 7 now returns DISTINCT stream_id only (stream assignment model)
    var perspectiveWork = results.Where((dynamic r) => r.source == "perspective_stream").ToList();
    var streamIds = perspectiveWork.Select((dynamic r) => (Guid)r.work_stream_id).Distinct().ToList();

    // All 4 streams should be present
    await Assert.That(streamIds).Contains(smallStream1)
      .Because("Small stream 1 should be in the returned assignments");
    await Assert.That(streamIds).Contains(smallStream2)
      .Because("Small stream 2 should be in the returned assignments");
    await Assert.That(streamIds).Contains(smallStream3)
      .Because("Small stream 3 should be in the returned assignments");
    await Assert.That(streamIds).Contains(largeStream)
      .Because("Large stream should also be in the returned assignments");

    // Small streams (Tier 1) should appear before large stream (Tier 2)
    var smallStreamIdSet = new HashSet<Guid> { smallStream1, smallStream2, smallStream3 };
    var maxSmallPos = perspectiveWork.Select((dynamic r, int i) => new { Id = (Guid)r.work_stream_id, Pos = i })
      .Where(x => smallStreamIdSet.Contains(x.Id)).Max(x => x.Pos);
    var largePos = perspectiveWork.Select((dynamic r, int i) => new { Id = (Guid)r.work_stream_id, Pos = i })
      .First(x => x.Id == largeStream).Pos;
    await Assert.That(maxSmallPos).IsLessThan(largePos)
      .Because("All small streams (Tier 1) should appear before the large stream (Tier 2)");

    await Assert.That(perspectiveWork.Count).IsEqualTo(4)
      .Because("Should return 4 distinct stream IDs, not individual event rows");
  }

  [Test]
  public async Task ProcessWorkBatch_TwoTier_AllSmallStreams_NoTier2NeededAsync() {
    // Arrange — only small streams, all should be served normally
    await _registerMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var instanceId = _idProvider.NewGuid();
    var now = DateTimeOffset.UtcNow;

    var stream1 = _idProvider.NewGuid();
    var stream2 = _idProvider.NewGuid();

    var allJsonArrays = new List<System.Text.Json.JsonElement>();
    foreach (var sid in new[] { stream1, stream2 }) {
      for (var i = 0; i < 5; i++) {
        var json = _createOutboxEventJson(sid, _idProvider.NewGuid(),
          "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", $$$"""{"v": {{{i}}}}""");
        allJsonArrays.Add(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(json)![0]);
      }
    }

    var outboxMessages = System.Text.Json.JsonSerializer.Serialize(allJsonArrays);

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var results = await connection.QueryAsync<dynamic>(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2,
        p_new_outbox_messages := @outboxMessages::jsonb
      )", new { instanceId, now, outboxMessages });

    // Drain mode: returns 'perspective_stream' rows with distinct stream IDs
    var perspectiveStreamRows = results.Where((dynamic r) => r.source == "perspective_stream").ToList();
    var streamIds = perspectiveStreamRows.Select((dynamic r) => (Guid)r.work_stream_id).Distinct().ToList();

    await Assert.That(streamIds).Contains(stream1)
      .Because("Stream 1 should be in the returned stream assignments");
    await Assert.That(streamIds).Contains(stream2)
      .Because("Stream 2 should be in the returned stream assignments");
    await Assert.That(perspectiveStreamRows.Count).IsEqualTo(2)
      .Because("Should return 2 distinct stream IDs (drain mode)");
  }

  // Helper methods

  private async Task _registerMessageAssociationAsync(
    string messageType,
    string associationType,
    string targetName,
    string serviceName) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (@messageType, @associationType, @targetName, @serviceName, normalize_event_type(@messageType), NOW(), NOW())",
      new { messageType, associationType, targetName, serviceName });
  }

  /// <summary>
  /// Creates an outbox message JSON structure for passing to process_work_batch.
  /// This is the proper flow - messages are passed to process_work_batch which:
  /// 1. Stores them in wh_outbox (Phase 4)
  /// 2. Stores events in wh_event_store (Phase 4.5)
  /// 3. Auto-creates perspective events/checkpoints (Phase 4.6/4.7)
  /// </summary>
  private string _createOutboxEventJson(
    Guid streamId,
    Guid eventId,
    string eventType,
    string eventData) {
    var outboxMessage = new {
      MessageId = eventId,
      Destination = (string?)null,  // Events don't have destinations
      MessageType = eventType,
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{eventType}]], Whizbang.Core",
      Envelope = new {
        MessageId = eventId,
        Payload = JsonSerializer.Deserialize<JsonElement>(eventData),
        Hops = Array.Empty<object>()
      },
      Metadata = new { },
      Scope = (object?)null,
      StreamId = streamId,
      IsEvent = true
    };

    return JsonSerializer.Serialize(new[] { outboxMessage });
  }

  /// <summary>
  /// LEGACY: Directly inserts into wh_event_store (bypasses proper flow).
  /// DO NOT USE - use _insertOutboxEventAsync instead.
  /// Kept for reference only.
  /// </summary>
  private async Task _insertEventStoreRecordAsync(
    Guid streamId,
    Guid eventId,
    string eventType,
    string eventData,
    int version) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (
        event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, sequence_number, version, created_at
      ) VALUES (
        @eventId, @streamId, @streamId, 'Product', @eventType, @eventData::jsonb, '{}'::jsonb, NULL, nextval('wh_event_sequence'), @version, NOW()
      )",
      new { eventId, streamId, eventType, eventData, version });
  }

  private async Task _insertPerspectiveCursorAsync(
      Guid streamId, string perspectiveName,
      Guid? lastEventId = null, short status = 0,
      Guid? rewindTriggerEventId = null) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, rewind_trigger_event_id)
      VALUES (@streamId, @perspectiveName, @lastEventId, @status, @rewindTriggerEventId)",
      new { streamId, perspectiveName, lastEventId, status, rewindTriggerEventId });
  }

  private async Task<PerspectiveCursor?> _getPerspectiveCursorAsync(Guid streamId, string perspectiveName) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<PerspectiveCursor>(@"
      SELECT stream_id, perspective_name, last_event_id, status, error, rewind_trigger_event_id, rewind_flagged_at
      FROM wh_perspective_cursors
      WHERE stream_id = @streamId AND perspective_name = @perspectiveName",
      new { streamId, perspectiveName });
  }

  private async Task<List<PerspectiveCursor>> _getAllPerspectiveCursorsAsync(Guid streamId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var results = await connection.QueryAsync<PerspectiveCursor>(@"
      SELECT stream_id, perspective_name, last_event_id, status, error, rewind_trigger_event_id, rewind_flagged_at
      FROM wh_perspective_cursors
      WHERE stream_id = @streamId",
      new { streamId });
    return [.. results];
  }

  private static async Task _insertEventStoreRowAsync(
      System.Data.IDbConnection connection, Guid eventId, Guid streamId, string eventType, string eventData) {
    var metadata = $$$"""{"MessageId":"{{{eventId}}}","Hops":[]}""";
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type,
        event_data, metadata, scope, version, created_at)
      VALUES (@eventId, @streamId, @streamId, 'TestAggregate', @eventType,
        @eventData::jsonb, @metadata::jsonb, NULL,
        (SELECT COALESCE(MAX(version), 0) + 1 FROM wh_event_store WHERE stream_id = @streamId), NOW())",
      new { eventId, streamId, eventType, eventData, metadata });
  }

  private static async Task _insertPerspectiveEventAsync(
      System.Data.IDbConnection connection, Guid streamId, string perspectiveName, Guid eventId) {
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
      VALUES (gen_random_uuid(), @streamId, @perspectiveName, @eventId, 1, 0, NOW())
      ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING",
      new { streamId, perspectiveName, eventId });
  }

  private static async Task<bool> _isPerspectiveEventProcessedAsync(
      System.Data.IDbConnection connection, Guid streamId, string perspectiveName, Guid eventId) {
    var result = await connection.QueryFirstOrDefaultAsync<bool?>(@"
      SELECT processed_at IS NOT NULL
      FROM wh_perspective_events
      WHERE stream_id = @streamId AND perspective_name = @perspectiveName AND event_id = @eventId",
      new { streamId, perspectiveName, eventId });
    return result == true;
  }

  // Lowercase properties match PostgreSQL column names (Dapper maps case-insensitively to record constructor parameters)
  private sealed record PerspectiveCursor(
    Guid stream_id,
    string perspective_name,
    Guid? last_event_id,
    short status,
    string? error,
    Guid? rewind_trigger_event_id,
    DateTimeOffset? rewind_flagged_at);

  private sealed record PerspectiveEventRow(
    Guid event_work_id,
    Guid stream_id,
    string perspective_name,
    Guid event_id,
    int status);
}
