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

    var perspectiveWork = results.Where((dynamic r) => r.source == "perspective").ToList();
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

    var perspectiveWork = results.Where((dynamic r) => r.source == "perspective").ToList();
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
        Status = (short)2  // Completed
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

  // Helper methods

  private async Task _registerMessageAssociationAsync(
    string messageType,
    string associationType,
    string targetName,
    string serviceName) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
      VALUES (@messageType, @associationType, @targetName, @serviceName, NOW(), NOW())",
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
