using Npgsql;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for lease renewal functionality in process_work_batch.
/// Validates that messages can have their leases renewed without completing/failing them.
/// </summary>
public class LeaseRenewalTests : EFCoreTestBase {
  [Test]
  public async Task ProcessWorkBatch_WithRenewOutboxLeaseIds_RenewsLeasesAsync() {
    // Arrange
    await using var dbContext = CreateDbContext();
    var instanceId = Guid.NewGuid();
    var messageId1 = Guid.NewGuid();
    var messageId2 = Guid.NewGuid();
    var streamId = Guid.CreateVersion7();

    // Insert test messages with lease expiring soon
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    var insertSql = @"
      INSERT INTO wh_outbox (message_id, destination, event_type, event_data, metadata, status, attempts, created_at, instance_id, lease_expiry, stream_id, partition_number)
      VALUES
        (@id1, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(), @instance_id, NOW() + INTERVAL '10 seconds', @stream_id, 0),
        (@id2, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(), @instance_id, NOW() + INTERVAL '10 seconds', @stream_id, 0)";

    await using var insertCmd = new NpgsqlCommand(insertSql, connection);
    insertCmd.Parameters.AddWithValue("id1", messageId1);
    insertCmd.Parameters.AddWithValue("id2", messageId2);
    insertCmd.Parameters.AddWithValue("instance_id", instanceId);
    insertCmd.Parameters.AddWithValue("stream_id", streamId);
    await insertCmd.ExecuteNonQueryAsync();

    // Get original lease expiry times
    var selectSql = "SELECT message_id, lease_expiry FROM wh_outbox WHERE message_id = ANY(@ids)";
    await using var selectCmd = new NpgsqlCommand(selectSql, connection);
    selectCmd.Parameters.AddWithValue("ids", new[] { messageId1, messageId2 });
    var originalLeases = new Dictionary<Guid, DateTimeOffset>();
    await using (var reader = await selectCmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        originalLeases[reader.GetGuid(0)] = reader.GetFieldValue<DateTimeOffset>(1);
      }
    }

    // Act - Call process_work_batch with lease renewal
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext);
    await coordinator.ProcessWorkBatchAsync(
      instanceId: instanceId,
      serviceName: "TestService",
      hostName: "localhost",
      processId: 12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [messageId1, messageId2],  // NEW PARAMETER
      renewInboxLeaseIds: [],
      leaseSeconds: 300,  // 5 minutes
      staleThresholdSeconds: 600,
      cancellationToken: default
    );

    // Assert - Verify leases were renewed
    await using var verifyCmd = new NpgsqlCommand(selectSql, connection);
    verifyCmd.Parameters.AddWithValue("ids", new[] { messageId1, messageId2 });
    await using (var reader = await verifyCmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        var msgId = reader.GetGuid(0);
        var newLeaseExpiry = reader.GetFieldValue<DateTimeOffset>(1);
        var originalExpiry = originalLeases[msgId];

        // New lease should be significantly later (at least 4 minutes longer since we renewed with 300 seconds)
        var extensionSeconds = (newLeaseExpiry - originalExpiry).TotalSeconds;
        await Assert.That(extensionSeconds).IsGreaterThan(240);  // At least 4 minutes
      }
    }

    // Cleanup
    await using var deleteCmd = new NpgsqlCommand("DELETE FROM wh_outbox WHERE message_id = ANY(@ids)", connection);
    deleteCmd.Parameters.AddWithValue("ids", new[] { messageId1, messageId2 });
    await deleteCmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task ProcessWorkBatch_WithRenewInboxLeaseIds_RenewsLeasesAsync() {
    // Arrange
    await using var dbContext = CreateDbContext();
    var instanceId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    var streamId = Guid.CreateVersion7();

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    var insertSql = @"
      INSERT INTO wh_inbox (message_id, handler_name, event_type, event_data, metadata, status, attempts, received_at, instance_id, lease_expiry, stream_id, partition_number)
      VALUES (@id, 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, NOW(), @instance_id, NOW() + INTERVAL '10 seconds', @stream_id, 0)";

    await using var insertCmd = new NpgsqlCommand(insertSql, connection);
    insertCmd.Parameters.AddWithValue("id", messageId);
    insertCmd.Parameters.AddWithValue("instance_id", instanceId);
    insertCmd.Parameters.AddWithValue("stream_id", streamId);
    await insertCmd.ExecuteNonQueryAsync();

    var selectSql = "SELECT lease_expiry AT TIME ZONE 'UTC' FROM wh_inbox WHERE message_id = @id";
    await using var selectCmd = new NpgsqlCommand(selectSql, connection);
    selectCmd.Parameters.AddWithValue("id", messageId);
    var originalExpiry = new DateTimeOffset((DateTime)(await selectCmd.ExecuteScalarAsync())!, TimeSpan.Zero);

    // Act
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext);
    await coordinator.ProcessWorkBatchAsync(
      instanceId: instanceId,
      serviceName: "TestService",
      hostName: "localhost",
      processId: 12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [messageId],  // NEW PARAMETER
      leaseSeconds: 300,
      staleThresholdSeconds: 600,
      cancellationToken: default
    );

    // Assert
    await using var verifyCmd = new NpgsqlCommand(selectSql, connection);
    verifyCmd.Parameters.AddWithValue("id", messageId);
    var newExpiry = new DateTimeOffset((DateTime)(await verifyCmd.ExecuteScalarAsync())!, TimeSpan.Zero);

    var extensionSeconds = (newExpiry - originalExpiry).TotalSeconds;
    await Assert.That(extensionSeconds).IsGreaterThan(240);

    // Cleanup
    await using var deleteCmd = new NpgsqlCommand("DELETE FROM wh_inbox WHERE message_id = @id", connection);
    deleteCmd.Parameters.AddWithValue("id", messageId);
    await deleteCmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task ProcessWorkBatch_RenewLease_DoesNotReturnMessageAsWorkAsync() {
    // Arrange - Message with valid lease should NOT be returned as work when lease is just being renewed
    await using var dbContext = CreateDbContext();
    var instanceId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Register service instance first (required for foreign key)
    var registerInstanceSql = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@instance_id, 'TestService', 'localhost', 12345, NOW(), NOW())";
    await using var registerCmd = new NpgsqlCommand(registerInstanceSql, connection);
    registerCmd.Parameters.AddWithValue("instance_id", instanceId);
    await registerCmd.ExecuteNonQueryAsync();

    var insertSql = @"
      INSERT INTO wh_outbox (message_id, destination, event_type, event_data, metadata, status, attempts, created_at, instance_id, lease_expiry, partition_number)
      VALUES (@id, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(), @instance_id, NOW() + INTERVAL '10 seconds', 0)";

    await using var insertCmd = new NpgsqlCommand(insertSql, connection);
    insertCmd.Parameters.AddWithValue("id", messageId);
    insertCmd.Parameters.AddWithValue("instance_id", instanceId);
    await insertCmd.ExecuteNonQueryAsync();

    // Claim partition for this instance
    var claimSql = "INSERT INTO wh_partition_assignments (partition_number, instance_id, assigned_at, last_heartbeat) VALUES (0, @instance_id, NOW(), NOW())";
    await using var claimCmd = new NpgsqlCommand(claimSql, connection);
    claimCmd.Parameters.AddWithValue("instance_id", instanceId);
    await claimCmd.ExecuteNonQueryAsync();

    // Act - Renew lease without marking complete/failed
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext);
    var workBatch = await coordinator.ProcessWorkBatchAsync(
      instanceId: instanceId,
      serviceName: "TestService",
      hostName: "localhost",
      processId: 12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [messageId],
      renewInboxLeaseIds: [],
      leaseSeconds: 300,
      staleThresholdSeconds: 600,
      cancellationToken: default
    );

    // Assert - Message should NOT be returned as work (lease is still valid)
    await Assert.That(workBatch.OutboxWork).IsEmpty();

    // Cleanup
    await using var deleteCmd = new NpgsqlCommand("DELETE FROM wh_outbox WHERE message_id = @id", connection);
    deleteCmd.Parameters.AddWithValue("id", messageId);
    await deleteCmd.ExecuteNonQueryAsync();

    await using var deletePartCmd = new NpgsqlCommand("DELETE FROM wh_partition_assignments WHERE partition_number = 0", connection);
    await deletePartCmd.ExecuteNonQueryAsync();

    await using var deleteInstanceCmd = new NpgsqlCommand("DELETE FROM wh_service_instances WHERE instance_id = @instance_id", connection);
    deleteInstanceCmd.Parameters.AddWithValue("instance_id", instanceId);
    await deleteInstanceCmd.ExecuteNonQueryAsync();
  }
}
