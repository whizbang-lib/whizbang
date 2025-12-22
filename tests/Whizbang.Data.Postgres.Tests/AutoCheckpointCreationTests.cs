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
  private readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();

  [Test]
  public async Task ProcessWorkBatch_WithEventAndPerspectiveAssociation_CreatesCheckpointAsync() {
    // Arrange - Register a perspective association for "ProductCreatedEvent" -> "ProductListPerspective"
    await RegisterMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Insert event into event store (simulating event being written)
    await InsertEventStoreRecordAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}",
      version: 1);

    // Act - Call process_work_batch (should auto-create checkpoint)
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - Checkpoint row should be created
    var checkpoint = await GetPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint).IsNotNull()
      .Because("process_work_batch should auto-create checkpoint when perspective association exists");
    await Assert.That(checkpoint!.stream_id).IsEqualTo(streamId);
    await Assert.That(checkpoint.perspective_name).IsEqualTo("ProductListPerspective");
    await Assert.That(checkpoint.last_event_id).IsNull()
      .Because("Newly created checkpoint has not processed any events yet");
    await Assert.That(checkpoint.status).IsEqualTo((short)0)  // PerspectiveProcessingStatus.None = 0
      .Because("Newly created checkpoint starts with no status flags");
  }

  [Test]
  public async Task ProcessWorkBatch_WithEventButNoAssociation_DoesNotCreateCheckpointAsync() {
    // Arrange - NO association registered (this is the key difference)
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Insert event into event store
    await InsertEventStoreRecordAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}",
      version: 1);

    // Act - Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - NO checkpoint should be created
    var checkpoints = await GetAllPerspectiveCheckpointsAsync(streamId);
    await Assert.That(checkpoints).HasCount().EqualTo(0)
      .Because("Without message association, no checkpoint should be auto-created");
  }

  [Test]
  public async Task ProcessWorkBatch_WithMultiplePerspectiveAssociations_CreatesMultipleCheckpointsAsync() {
    // Arrange - Register TWO perspective associations for same event type
    await RegisterMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    await RegisterMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductDetailsPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Insert event into event store
    await InsertEventStoreRecordAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}",
      version: 1);

    // Act - Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - TWO checkpoints should be created
    var checkpoints = await GetAllPerspectiveCheckpointsAsync(streamId);
    await Assert.That(checkpoints).HasCount().EqualTo(2)
      .Because("Both perspective associations should result in checkpoint creation");

    var checkpoint1 = checkpoints.FirstOrDefault(c => c.perspective_name == "ProductListPerspective");
    var checkpoint2 = checkpoints.FirstOrDefault(c => c.perspective_name == "ProductDetailsPerspective");

    await Assert.That(checkpoint1).IsNotNull();
    await Assert.That(checkpoint2).IsNotNull();
  }

  [Test]
  public async Task ProcessWorkBatch_WithExistingCheckpoint_DoesNotDuplicateAsync() {
    // Arrange - Register association
    await RegisterMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Manually insert checkpoint (simulating it already exists)
    await InsertPerspectiveCheckpointAsync(streamId, "ProductListPerspective");

    // Insert event into event store
    await InsertEventStoreRecordAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}",
      version: 1);

    // Act - Call process_work_batch (should NOT duplicate)
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - Still only ONE checkpoint
    var checkpoints = await GetAllPerspectiveCheckpointsAsync(streamId);
    await Assert.That(checkpoints).HasCount().EqualTo(1)
      .Because("Existing checkpoint should not be duplicated");
  }

  [Test]
  public async Task ProcessWorkBatch_WithNonPerspectiveAssociation_DoesNotCreateCheckpointAsync() {
    // Arrange - Register RECEPTOR association (not perspective)
    await RegisterMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "receptor",
      targetName: "SendEmailReceptor",
      serviceName: "ECommerce.Notifications");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Insert event into event store
    await InsertEventStoreRecordAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}",
      version: 1);

    // Act - Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - NO checkpoint should be created (receptors don't use checkpoints)
    var checkpoints = await GetAllPerspectiveCheckpointsAsync(streamId);
    await Assert.That(checkpoints).HasCount().EqualTo(0)
      .Because("Receptor associations should not trigger checkpoint creation");
  }

  // Flexible Type Matching Tests (fuzzy matching)

  [Test]
  public async Task ProcessWorkBatch_WithAssemblyQualifiedNameEvent_MatchesShortFormAssociationAsync() {
    // Arrange - Association has short form "TypeName, AssemblyName"
    await RegisterMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Act - Event has FULL AssemblyQualifiedName with Version/Culture/PublicKeyToken
    await InsertEventStoreRecordAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}",
      version: 1);

    // Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - Checkpoint SHOULD be created despite format difference
    var checkpoint = await GetPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint).IsNotNull()
      .Because("Fuzzy matching should match on TypeName + AssemblyName, ignoring Version/Culture/PublicKeyToken");
  }

  [Test]
  public async Task ProcessWorkBatch_WithShortFormEvent_MatchesAssemblyQualifiedAssociationAsync() {
    // Arrange - Association has FULL AssemblyQualifiedName
    await RegisterMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Act - Event has short form "TypeName, AssemblyName"
    await InsertEventStoreRecordAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}",
      version: 1);

    // Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - Checkpoint SHOULD be created despite format difference
    var checkpoint = await GetPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint).IsNotNull()
      .Because("Fuzzy matching should match on TypeName + AssemblyName, ignoring Version/Culture/PublicKeyToken");
  }

  [Test]
  public async Task ProcessWorkBatch_WithDifferentVersions_StillMatchesAsync() {
    // Arrange - Association registered with version 1.0.0.0
    await RegisterMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Act - Event has version 2.0.0.0 (different!)
    await InsertEventStoreRecordAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain, Version=2.0.0.0, Culture=neutral, PublicKeyToken=abc123",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}",
      version: 1);

    // Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - Checkpoint SHOULD be created despite version difference
    var checkpoint = await GetPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint).IsNotNull()
      .Because("Fuzzy matching should ignore version numbers and match on TypeName + AssemblyName");
  }

  [Test]
  public async Task ProcessWorkBatch_WithOnlyTypeNameEvent_DoesNotMatchAsync() {
    // Arrange - Association has "TypeName, AssemblyName"
    await RegisterMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Act - Event has ONLY TypeName (no assembly - this is too loose!)
    await InsertEventStoreRecordAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}",
      version: 1);

    // Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - NO checkpoint should be created (TypeName alone is not enough)
    var checkpoints = await GetAllPerspectiveCheckpointsAsync(streamId);
    await Assert.That(checkpoints).HasCount().EqualTo(0)
      .Because("TypeName alone is insufficient - we need at least TypeName + AssemblyName for safe matching");
  }

  [Test]
  public async Task ProcessWorkBatch_WithDifferentAssemblyNames_DoesNotMatchAsync() {
    // Arrange - Association for "ECommerce.Domain" assembly
    await RegisterMessageAssociationAsync(
      messageType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain",
      associationType: "perspective",
      targetName: "ProductListPerspective",
      serviceName: "ECommerce.ReadModels");

    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    // Act - Event from DIFFERENT assembly "ECommerce.Domain.V2"
    await InsertEventStoreRecordAsync(
      streamId: streamId,
      eventId: eventId,
      eventType: "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain.V2",
      eventData: "{\"productId\":\"123\",\"name\":\"Widget\"}",
      version: 1);

    // Call process_work_batch
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - NO checkpoint (different assemblies = different types)
    var checkpoints = await GetAllPerspectiveCheckpointsAsync(streamId);
    await Assert.That(checkpoints).HasCount().EqualTo(0)
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

    await InsertPerspectiveCheckpointAsync(streamId, "ProductListPerspective");

    // Insert some events
    await InsertEventStoreRecordAsync(streamId, eventId1, "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}", 1);
    await InsertEventStoreRecordAsync(streamId, eventId2, "ECommerce.Domain.Events.ProductUpdatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}", 2);
    await InsertEventStoreRecordAsync(streamId, eventId3, "ECommerce.Domain.Events.ProductUpdatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}", 3);

    // Act - Report perspective completion (processed up to eventId2)
    var perspectiveCompletions = new[] {
      new {
        stream_id = streamId,
        perspective_name = "ProductListPerspective",
        last_event_id = eventId2,
        status = (short)1  // PerspectiveProcessingStatus.Completed
      }
    };

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_perspective_completions := @completions::jsonb
      )",
      new {
        instanceId = _idProvider.NewGuid(),
        completions = System.Text.Json.JsonSerializer.Serialize(perspectiveCompletions)
      });

    // Assert - Checkpoint should be updated with eventId2
    var checkpoint = await GetPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
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

    await InsertPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await InsertPerspectiveCheckpointAsync(streamId, "ProductDetailsPerspective");

    await InsertEventStoreRecordAsync(streamId, eventId1, "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}", 1);
    await InsertEventStoreRecordAsync(streamId, eventId2, "ECommerce.Domain.Events.ProductUpdatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}", 2);

    // Act - Report completions for BOTH perspectives (but at different points)
    var perspectiveCompletions = new[] {
      new {
        stream_id = streamId,
        perspective_name = "ProductListPerspective",
        last_event_id = eventId2,  // Processed both events
        status = (short)1
      },
      new {
        stream_id = streamId,
        perspective_name = "ProductDetailsPerspective",
        last_event_id = eventId1,  // Only processed first event
        status = (short)1
      }
    };

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_perspective_completions := @completions::jsonb
      )",
      new {
        instanceId = _idProvider.NewGuid(),
        completions = System.Text.Json.JsonSerializer.Serialize(perspectiveCompletions)
      });

    // Assert - Both checkpoints should be updated independently
    var checkpoint1 = await GetPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    var checkpoint2 = await GetPerspectiveCheckpointAsync(streamId, "ProductDetailsPerspective");

    await Assert.That(checkpoint1!.last_event_id).IsEqualTo(eventId2);
    await Assert.That(checkpoint2!.last_event_id).IsEqualTo(eventId1);
  }

  [Test]
  public async Task ProcessWorkBatch_WithPerspectiveFailure_UpdatesCheckpointWithErrorAsync() {
    // Arrange
    var streamId = _idProvider.NewGuid();
    var eventId = _idProvider.NewGuid();

    await InsertPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await InsertEventStoreRecordAsync(streamId, eventId, "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}", 1);

    // Act - Report perspective FAILURE
    var perspectiveFailures = new[] {
      new {
        stream_id = streamId,
        perspective_name = "ProductListPerspective",
        last_event_id = eventId,
        status = (short)2,  // PerspectiveProcessingStatus.Failed
        error = "Database connection timeout"
      }
    };

    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345,
        p_perspective_failures := @failures::jsonb
      )",
      new {
        instanceId = _idProvider.NewGuid(),
        failures = System.Text.Json.JsonSerializer.Serialize(perspectiveFailures)
      });

    // Assert - Checkpoint should be updated with failed status AND error message
    var checkpoint = await GetPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
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

    await InsertPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await InsertEventStoreRecordAsync(streamId, eventId, "ECommerce.Domain.Events.ProductCreatedEvent, ECommerce.Domain", "{\"productId\":\"123\"}", 1);

    // Act - Call process_work_batch with NO completions/failures
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      SELECT * FROM process_work_batch(
        p_instance_id := @instanceId::uuid,
        p_service_name := 'TestService',
        p_host_name := 'test-host',
        p_process_id := 12345
      )",
      new { instanceId = _idProvider.NewGuid() });

    // Assert - Checkpoint should remain unchanged (still NULL last_event_id)
    var checkpoint = await GetPerspectiveCheckpointAsync(streamId, "ProductListPerspective");
    await Assert.That(checkpoint!.last_event_id).IsNull()
      .Because("Without completion report, checkpoint should not be updated");
  }

  // Helper methods

  private async Task RegisterMessageAssociationAsync(
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

  private async Task InsertEventStoreRecordAsync(
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

  private async Task InsertPerspectiveCheckpointAsync(Guid streamId, string perspectiveName) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_perspective_checkpoints (stream_id, perspective_name, last_event_id, status)
      VALUES (@streamId, @perspectiveName, NULL, 0)",
      new { streamId, perspectiveName });
  }

  private async Task<PerspectiveCheckpoint?> GetPerspectiveCheckpointAsync(Guid streamId, string perspectiveName) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<PerspectiveCheckpoint>(@"
      SELECT stream_id, perspective_name, last_event_id, status, error
      FROM wh_perspective_checkpoints
      WHERE stream_id = @streamId AND perspective_name = @perspectiveName",
      new { streamId, perspectiveName });
  }

  private async Task<List<PerspectiveCheckpoint>> GetAllPerspectiveCheckpointsAsync(Guid streamId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var results = await connection.QueryAsync<PerspectiveCheckpoint>(@"
      SELECT stream_id, perspective_name, last_event_id, status, error
      FROM wh_perspective_checkpoints
      WHERE stream_id = @streamId",
      new { streamId });
    return results.ToList();
  }

  // Lowercase properties match PostgreSQL column names (Dapper maps case-insensitively to record constructor parameters)
  private record PerspectiveCheckpoint(
    Guid stream_id,
    string perspective_name,
    Guid? last_event_id,
    short status,
    string? error);
}
