using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// EFCore counterpart to PartitionConsistencyTests in the Dapper test project.
/// Same invariant: for any given stream_id, partition_number must agree across
/// wh_inbox and wh_active_streams. The bug also affects EFCoreWorkCoordinator
/// because IWorkCoordinator.StoreInboxMessagesAsync has the same partitionCount=2
/// default that the Dapper implementation does.
/// </summary>
public class EFCorePartitionConsistencyTests : EFCoreTestBase {
  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _sut = null!;
  private Guid _instanceId;
  private readonly Uuid7IdProvider _idProvider = new();

  [Before(Test)]
  public async Task TestSetupAsync() {
    _instanceId = _idProvider.NewGuid();
    var dbContext = CreateDbContext();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    _sut = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, jsonOptions);
    await _insertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
  }

  /// <summary>
  /// RED. Same shape as Dapper test 1: store via fast path (partition_count=2 default),
  /// then publisher tick (partition_count=10_000 default). Assert both tables agree.
  /// </summary>
  [Test]
  public async Task WhenStoreInboxAndProcessBatchUseDefaults_PartitionNumbersMustMatchAsync() {
    var streamId = _idProvider.NewGuid();
    var messageId = _idProvider.NewGuid();
    var inboxMessage = new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = streamId,
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };

    // Reproduce dev wedge: store via OLD shipped default (partitionCount=2) while
    // ProcessWorkBatch below uses the canonical PartitionCount=10_000.
    await _sut.StoreInboxMessagesAsync([inboxMessage], partitionCount: 2);

    await _sut.ProcessWorkBatchAsync(new ProcessWorkBatchRequest {
      InstanceId = _instanceId,
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
      RenewInboxLeaseIds = []
    });

    // Simulate a worker restart: production self-heals stale partition_number rows
    // via WorkCoordinatorPublisherWorker._recomputePartitionsOnStartupAsync, which
    // calls recompute_partition_numbers(). This test exercises the same API.
    await _sut.RecomputePartitionNumbersAsync(partitionCount: 10_000);

    var inboxPartition = await _getInboxPartitionNumberAsync(messageId);
    var activeStreamPartition = await _getActiveStreamPartitionNumberAsync(streamId);
    await Assert.That(inboxPartition).IsEqualTo(activeStreamPartition)
      .Because("EFCoreWorkCoordinator suffers the same partition_count default mismatch as Dapper — partition_number must agree across wh_inbox and wh_active_streams for the same stream_id");
  }

  // Helpers — minimal local copies to avoid coupling to the larger EFCoreWorkCoordinatorTests fixture.

  private async Task _insertServiceInstanceAsync(Guid instanceId, string serviceName, string hostName, int processId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@instanceId, @serviceName, @hostName, @processId, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()", connection);
    cmd.Parameters.AddWithValue("instanceId", instanceId);
    cmd.Parameters.AddWithValue("serviceName", serviceName);
    cmd.Parameters.AddWithValue("hostName", hostName);
    cmd.Parameters.AddWithValue("processId", processId);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<int?> _getInboxPartitionNumberAsync(Guid messageId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      "SELECT partition_number FROM wh_inbox WHERE message_id = @messageId", connection);
    cmd.Parameters.AddWithValue("messageId", messageId);
    var result = await cmd.ExecuteScalarAsync();
    return result is int i ? i : null;
  }

  private async Task<int?> _getActiveStreamPartitionNumberAsync(Guid streamId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      "SELECT partition_number FROM wh_active_streams WHERE stream_id = @streamId", connection);
    cmd.Parameters.AddWithValue("streamId", streamId);
    var result = await cmd.ExecuteScalarAsync();
    return result is int i ? i : null;
  }
}
