using System.Text.Json;
using Dapper;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Tests for automatic perspective checkpoint creation when events are written to event store.
/// Phase 2 of the perspective materialization bug fix - verifies that process_work_batch()
/// auto-creates checkpoint rows when message associations exist for perspectives.
/// </summary>
public class AutoCheckpointCreationTests : PostgresTestBase {
  private readonly Uuid7IdProvider _idProvider = new Uuid7IdProvider();

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

    // Insert event into outbox (proper flow - will be stored in event store by process_work_batch)
    await _insertOutboxEventAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch to trigger perspective association matching
    var _ = await connection.QueryAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := @now::timestamptz,
        p_lease_duration_seconds := 30,
        p_partition_count := 2
      )",
      new { instanceId, now });

    // Check if perspective events were created (indicates match)
    var perspectiveEvents = await connection.QueryAsync<PerspectiveEventRow>(@"
      SELECT event_work_id, stream_id, perspective_name, event_id, sequence_number, status
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
    var checkpoint = await _getPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
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

    // Insert event into event store
    await _insertOutboxEventAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW()
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - NO checkpoint should be created
    var checkpoints = await _getAllPerspectiveCheckpointsAsync(streamId);
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

    // Insert event into event store
    await _insertOutboxEventAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW()
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - TWO checkpoints should be created
    var checkpoints = await _getAllPerspectiveCheckpointsAsync(streamId);
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
    await _insertPerspectiveCheckpointAsync(streamId, "ProductListPerspective");

    // Insert event into event store
    await _insertOutboxEventAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch (should NOT duplicate)
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW()
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - Still only ONE checkpoint
    var checkpoints = await _getAllPerspectiveCheckpointsAsync(streamId);
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

    // Insert event into event store
    await _insertOutboxEventAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Act - Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW()
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - NO checkpoint should be created (receptors don't use checkpoints)
    var checkpoints = await _getAllPerspectiveCheckpointsAsync(streamId);
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

    // Act - Event has FULL AssemblyQualifiedName with Version/Culture/PublicKeyToken
    await _insertOutboxEventAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW()
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - Checkpoint SHOULD be created despite format difference
    var checkpoint = await _getPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
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

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Act - Event has short form "TypeName, AssemblyName"
    await _insertOutboxEventAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW()
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - Checkpoint SHOULD be created despite format difference
    var checkpoint = await _getPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
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

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Act - Event has version 2.0.0.0 (different!)
    await _insertOutboxEventAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain, Version=2.0.0.0, Culture=neutral, PublicKeyToken=abc123",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW()
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - Checkpoint SHOULD be created despite version difference
    var checkpoint = await _getPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
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

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Act - Event has ONLY TypeName (no assembly - this is too loose!)
    await _insertOutboxEventAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW()
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - NO checkpoint should be created (TypeName alone is not enough)
    var checkpoints = await _getAllPerspectiveCheckpointsAsync(streamId);
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

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Act - Event from DIFFERENT assembly "ECommerce.Domain.V2"
    await _insertOutboxEventAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain.V2",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}");

    // Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW()
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - NO checkpoint (different assemblies = different types)
    var checkpoints = await _getAllPerspectiveCheckpointsAsync(streamId);
    await Assert.That(checkpoints).Count().IsEqualTo(0)
      .Because("AssemblyName mismatch means different types - no match");
  }

  // Checkpoint Update Tests (Phase 3 - verifies that checkpoints are updated when perspective processing completes)

  [Test]
  public async Task ProcessWorkBatch_WithPerspectiveCompletion_UpdatesCheckpointAsync() {
    // Arrange - Create a checkpoint that needs updating
    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();
    var eventId3 = _idProvider.NewGuid();

    await _insertPerspectiveCheckpointAsync(streamId, "ProductListPerspective");

    // Insert some events into outbox (proper flow)
    await _insertOutboxEventAsync(streamId, eventId1, "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");
    await _insertOutboxEventAsync(streamId, eventId2, "ECommerce.Domain.Events.ProductUpdatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");
    await _insertOutboxEventAsync(streamId, eventId3, "ECommerce.Domain.Events.ProductUpdatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");

    // Act - Report perspective completion (processed up to eventId2)
    var perspectiveCompletions = new[] {
      new {
        StreamId = streamId,
        PerspectiveName = "ProductListPerspective",
        LastEventId = eventId2,
        Status = (short)1  // PerspectiveProcessingStatus.Completed
      }
    };

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW(),
        p_perspective_completions := @completions::jsonb
      )",
      new {
        instanceId = _idProvider.NewGuid(),
        completions = System.Text.Json.JsonSerializer.Serialize(perspectiveCompletions)
      });

    // Assert - Checkpoint should be updated with eventId2
    var checkpoint = await _getPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint).IsNotNull();
    await Assert.That(checkpoint!.last_event_id).IsEqualTo(eventId2)
      .Because("Checkpoint should be updated to reflect last processed event from perspective completion");
    await Assert.That(checkpoint.status).IsEqualTo((short)1)  // Completed
      .Because("Status should reflect the completion");
  }

  [Test]
  public async Task ProcessWorkBatch_WithMultiplePerspectiveCompletions_UpdatesAllCheckpointsAsync() {
    // Arrange - Create TWO checkpoints for different perspectives on same stream
    var streamId = _idProvider.NewGuid();
    var eventId1 = _idProvider.NewGuid();
    var eventId2 = _idProvider.NewGuid();

    await _insertPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await _insertPerspectiveCheckpointAsync(streamId, "ProductDetailsPerspective");

    await _insertOutboxEventAsync(streamId, eventId1, "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");
    await _insertOutboxEventAsync(streamId, eventId2, "ECommerce.Domain.Events.ProductUpdatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");

    // Act - Report completions for BOTH perspectives (but at different points)
    var perspectiveCompletions = new[] {
      new {
        StreamId = streamId,
        PerspectiveName = "ProductListPerspective",
        LastEventId = eventId2,  // Processed both events
        Status = (short)1
      },
      new {
        StreamId = streamId,
        PerspectiveName = "ProductDetailsPerspective",
        LastEventId = eventId1,  // Only processed first event
        Status = (short)1
      }
    };

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW(),
        p_perspective_completions := @completions::jsonb
      )",
      new {
        instanceId = _idProvider.NewGuid(),
        completions = System.Text.Json.JsonSerializer.Serialize(perspectiveCompletions)
      });

    // Assert - Both checkpoints should be updated independently
    var checkpoint1 = await _getPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    var checkpoint2 = await _getPerspectiveCheckpointAsync(streamId, "ProductDetailsPerspective");

    await Assert.That(checkpoint1!.last_event_id).IsEqualTo(eventId2);
    await Assert.That(checkpoint2!.last_event_id).IsEqualTo(eventId1);
  }

  [Test]
  public async Task ProcessWorkBatch_WithPerspectiveFailure_UpdatesCheckpointWithErrorAsync() {
    // Arrange
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    await _insertPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await _insertOutboxEventAsync(streamId, eventId, "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");

    // Act - Report perspective FAILURE
    var perspectiveFailures = new[] {
      new {
        StreamId = streamId,
        PerspectiveName = "ProductListPerspective",
        LastEventId = eventId,
        Status = (short)2,  // PerspectiveProcessingStatus.Failed
        Error = "Database connection timeout"
      }
    };

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW(),
        p_perspective_failures := @failures::jsonb
      )",
      new {
        instanceId = _idProvider.NewGuid(),
        failures = System.Text.Json.JsonSerializer.Serialize(perspectiveFailures)
      });

    // Assert - Checkpoint should be updated with failed status AND error message
    var checkpoint = await _getPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint!.status).IsEqualTo((short)2)  // Failed
      .Because("Checkpoint should reflect the failure status");
    await Assert.That(checkpoint.error).IsEqualTo("Database connection timeout")
      .Because("Error message should be persisted to help diagnose failures");
  }

  [Test]
  public async Task ProcessWorkBatch_WithNoCompletions_DoesNotUpdateCheckpointAsync() {
    // Arrange
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    await _insertPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await _insertOutboxEventAsync(streamId, eventId, "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}");

    // Act - Call process_work_batch with NO completions/failures
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_metadata := '{}'::jsonb,
        p_now := NOW()
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - Checkpoint should remain unchanged (still NULL last_event_id)
    var checkpoint = await _getPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint!.last_event_id).IsNull()
      .Because("Without completion report, checkpoint should not be updated");
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
  /// Inserts an event into wh_outbox table (proper flow).
  /// When process_work_batch() is called, Phase 4.5 will store it in wh_event_store
  /// and add it to v_stored_outbox_events array, then Phase 4.6/4.7 will process it.
  /// </summary>
  private async Task _insertOutboxEventAsync(
    Guid streamId,
    Guid eventId,
    string eventType,
    string eventData) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Create MessageEnvelope<TPayload> JSON structure
    var envelopeJson = $@"{{
      ""MessageId"": ""{eventId}"",
      ""Payload"": {eventData},
      ""Hops"": []
    }}";

    // Insert into wh_outbox (Status=0 so process_work_batch can claim it)
    await connection.ExecuteAsync(@"
      INSERT INTO wh_outbox (
        message_id,
        destination,
        event_type,
        envelope_type,
        event_data,
        metadata,
        scope,
        stream_id,
        partition_number,
        is_event,
        status,
        attempts,
        created_at,
        instance_id,
        lease_expiry
      ) VALUES (
        @eventId,
        NULL,                              -- No destination for events
        @eventType,                        -- Event type (payload type)
        'Whizbang.Core.Observability.MessageEnvelope`1[[' || @eventType || ']], Whizbang.Core', -- Envelope type
        @envelopeJson::jsonb,              -- Envelope data
        '{}'::jsonb,                       -- Empty metadata
        'null'::jsonb,                     -- No scope
        @streamId,
        compute_partition(@streamId, 10000), -- Partition for load balancing
        true,                              -- is_event = true
        0,                                 -- Status = 0 (available for claiming)
        0,                                 -- Attempts = 0
        NOW(),
        NULL,                              -- No instance_id yet
        NULL                               -- No lease yet
      )",
      new { eventId, streamId, eventType, envelopeJson });
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

  private async Task _insertPerspectiveCheckpointAsync(Guid streamId, string perspectiveName) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_checkpoints (stream_id, perspective_name, last_event_id, status)
      VALUES (@streamId, @perspectiveName, NULL, 0)",
      new { streamId, perspectiveName });
  }

  private async Task<PerspectiveCheckpoint?> _getPerspectiveCheckpointAsync(Guid streamId, string perspectiveName) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<PerspectiveCheckpoint>(@"
      SELECT stream_id, perspective_name, last_event_id, status, error
      FROM wh_perspective_checkpoints
      WHERE stream_id = @streamId AND perspective_name = @perspectiveName",
      new { streamId, perspectiveName });
  }

  private async Task<List<PerspectiveCheckpoint>> _getAllPerspectiveCheckpointsAsync(Guid streamId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var results = await connection.QueryAsync<PerspectiveCheckpoint>(@"
      SELECT stream_id, perspective_name, last_event_id, status, error
      FROM wh_perspective_checkpoints
      WHERE stream_id = @streamId",
      new { streamId });
    return results.ToList();
  }

  // Lowercase properties match PostgreSQL column names (Dapper maps case-insensitively to record constructor parameters)
  private sealed record PerspectiveCheckpoint(
    Guid stream_id,
    string perspective_name,
    Guid? last_event_id,
    short status,
    string? error);

  private sealed record PerspectiveEventRow(
    Guid event_work_id,
    Guid stream_id,
    string perspective_name,
    Guid event_id,
    long sequence_number,
    int status);
}
