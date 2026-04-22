using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// EFCore counterpart to <c>ProcessWorkBatchLeaseRenewalTests</c> in the Dapper
/// project. One test is sufficient per existing convention (same migration is
/// applied either way, and the Dapper suite carries the full coverage). Confirms
/// the lease-renewal guard added to migration 029 also takes effect when
/// <c>process_work_batch</c> is invoked through <c>EFCoreWorkCoordinator</c>.
/// </summary>
public class EFCoreLeaseRenewalTests : EFCoreTestBase {
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
  /// Parallels the Dapper test: a fresh lease (5 minutes from now) must not be
  /// rewritten on an empty publisher tick. The guard is in the Postgres function
  /// body, so any work coordinator invoking <c>process_work_batch</c> benefits.
  /// </summary>
  [Test]
  public async Task WhenLeaseIsFresh_PwbDoesNotRewriteLeaseExpiryAsync() {
    var streamId = _idProvider.NewGuid();
    var fiveMinutes = DateTimeOffset.UtcNow.AddMinutes(5);
    await _seedActiveStreamAsync(streamId, _instanceId, 10_000, fiveMinutes);
    var leaseBefore = await _getLeaseExpiryAsync(streamId);

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

    var leaseAfter = await _getLeaseExpiryAsync(streamId);
    await Assert.That(leaseAfter).IsEqualTo(leaseBefore)
      .Because("Lease-renewal guard (migration 029) must apply regardless of whether "
             + "the orchestrator calling process_work_batch is Dapper or EFCore — it "
             + "lives in the Postgres function body.");
  }

  // Helpers

  private async Task _insertServiceInstanceAsync(Guid instanceId, string serviceName, string hostName, int processId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@instanceId, @serviceName, @hostName, @processId, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()", connection);
    cmd.Parameters.AddWithValue(nameof(instanceId), instanceId);
    cmd.Parameters.AddWithValue(nameof(serviceName), serviceName);
    cmd.Parameters.AddWithValue(nameof(hostName), hostName);
    cmd.Parameters.AddWithValue(nameof(processId), processId);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _seedActiveStreamAsync(Guid streamId, Guid ownerInstanceId, int partitionCount, DateTimeOffset leaseExpiry) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_active_streams (stream_id, assigned_instance_id, lease_expiry, partition_number, last_activity_at)
      VALUES (@streamId, @ownerInstanceId, @leaseExpiry, compute_partition(@streamId, @partitionCount), NOW())
      ON CONFLICT ON CONSTRAINT wh_active_streams_pkey DO UPDATE SET
        assigned_instance_id = EXCLUDED.assigned_instance_id,
        lease_expiry = EXCLUDED.lease_expiry,
        partition_number = EXCLUDED.partition_number,
        last_activity_at = EXCLUDED.last_activity_at", connection);
    cmd.Parameters.AddWithValue(nameof(streamId), streamId);
    cmd.Parameters.AddWithValue(nameof(ownerInstanceId), ownerInstanceId);
    cmd.Parameters.AddWithValue(nameof(leaseExpiry), leaseExpiry);
    cmd.Parameters.AddWithValue(nameof(partitionCount), partitionCount);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<DateTimeOffset> _getLeaseExpiryAsync(Guid streamId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      "SELECT lease_expiry FROM wh_active_streams WHERE stream_id = @streamId", connection);
    cmd.Parameters.AddWithValue(nameof(streamId), streamId);
    var result = await cmd.ExecuteScalarAsync();
    // Raw NpgsqlCommand at this level returns DateTime (UTC kind) for TIMESTAMPTZ
    // even though EFCoreTestBase sets EnableLegacyTimestampBehavior=false — that
    // switch only wires the default reader path used via the EF data source.
    return result switch {
      DateTimeOffset dto => dto,
      DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
      _ => throw new InvalidOperationException($"Unexpected type for lease_expiry: {result?.GetType()}")
    };
  }
}
