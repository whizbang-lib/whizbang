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
      INSERT INTO wh_perspective_checkpoints (stream_id, perspective_name, last_event_id, status, last_processed_at, created_at, updated_at)
      VALUES (@streamId, @perspectiveName, NULL, 0, NULL, NOW(), NOW())",
      new { streamId, perspectiveName });
  }

  private async Task<PerspectiveCheckpoint?> GetPerspectiveCheckpointAsync(Guid streamId, string perspectiveName) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<PerspectiveCheckpoint>(@"
      SELECT stream_id AS StreamId, perspective_name AS PerspectiveName, last_event_id AS LastEventId, status AS Status
      FROM wh_perspective_checkpoints
      WHERE stream_id = @streamId AND perspective_name = @perspectiveName",
      new { streamId, perspectiveName });
  }

  private async Task<List<PerspectiveCheckpoint>> GetAllPerspectiveCheckpointsAsync(Guid streamId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var results = await connection.QueryAsync<PerspectiveCheckpoint>(@"
      SELECT stream_id AS StreamId, perspective_name AS PerspectiveName, last_event_id AS LastEventId, status AS Status
      FROM wh_perspective_checkpoints
      WHERE stream_id = @streamId",
      new { streamId });
    return results.ToList();
  }

  // Use lowercase column names to match PostgreSQL convention (Dapper is case-sensitive)
  private record PerspectiveCheckpoint(
    Guid stream_id,
    string perspective_name,
    Guid? last_event_id,
    short status);
}
