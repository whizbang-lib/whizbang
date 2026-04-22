using System.Text.Json;
using Dapper;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// <para>Write-volume contract tests for the per-tick maintenance UPDATEs inside
/// <c>process_work_batch</c> (migration 029) and <c>register_instance_heartbeat</c>
/// (migration 010).</para>
///
/// <para>The system was observed writing every owned stream row on every tick
/// (JDX dev slot 3 on 2026-04-21: 1.8 billion lifetime UPDATEs on 5,790 rows)
/// because both renewal paths had no near-expiry / freshness guard. This test
/// class locks in conditional behaviour:</para>
///
/// <para>  - <c>wh_active_streams.lease_expiry</c> is refreshed only when the existing
///     lease is within one-third of <c>p_lease_duration_seconds</c> of now.
///   - <c>wh_service_instances.last_heartbeat_at</c> is refreshed only when the
///     last heartbeat is more than 10 seconds stale.</para>
///
/// <para>Evidence + rollback artifacts for the live proof that motivated this change
/// live at <c>JDNext/docs/slot3-vacuum-proof-2026-04-21/</c>.</para>
/// </summary>
[Category("Integration")]
public class ProcessWorkBatchLeaseRenewalTests : PostgresTestBase {
  private DapperWorkCoordinator _sut = null!;
  private Guid _instanceId;
  private readonly Uuid7IdProvider _idProvider = new();
  private static readonly JsonSerializerOptions _jsonOptions;

  static ProcessWorkBatchLeaseRenewalTests() {
    var baseOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    _jsonOptions = new JsonSerializerOptions(baseOptions) {
      TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
        baseOptions.TypeInfoResolver!,
        TestEnvelopeJsonContext.Default
      )
    };
  }

  [Before(Test)]
  public new async Task SetupAsync() {
    await base.SetupAsync();
    _instanceId = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    _sut = new DapperWorkCoordinator(ConnectionString, _jsonOptions);
  }

  // ================== Lease renewal (migration 029) ==================

  /// <summary>
  /// RED against current migration 029: every owned stream is UPDATE-ed on every
  /// tick regardless of whether the lease is near expiry. With a fresh lease
  /// (well above the refresh threshold), the UPDATE should be skipped entirely.
  /// Uses the <c>lease_expiry</c> value itself as the observable — if the UPDATE
  /// fired, the stored value is bumped forward to <c>now() + lease_duration</c>;
  /// if skipped, the stored value stays exactly where we seeded it.
  /// </summary>
  [Test]
  public async Task WhenLeaseIsFresh_PwbDoesNotRewriteLeaseExpiryAsync() {
    var streamId = _idProvider.NewGuid();

    // Seed a "fresh" lease: expires 5 minutes from now, which is well above the
    // refresh threshold of p_lease_duration_seconds / 3 = 100 s (at default 300 s).
    var fiveMinutes = DateTimeOffset.UtcNow.AddMinutes(5);
    await _seedActiveStreamAsync(streamId, _instanceId, 10_000, fiveMinutes);
    var leaseBefore = await _getLeaseExpiryAsync(streamId);

    await _sut.ProcessWorkBatchAsync(_emptyTickRequest(_instanceId));
    var leaseAfter = await _getLeaseExpiryAsync(streamId);

    await Assert.That(leaseAfter).IsEqualTo(leaseBefore)
      .Because("A lease expiring in 5 min is nowhere near expiry; process_work_batch "
             + "must not rewrite it on every tick. Unconditional refresh generates "
             + "one dead tuple per owned stream per tick and dominated WAL pressure "
             + "on JDX dev slot 3 (315K updates/row across 5,790 rows).");
  }

  /// <summary>
  /// Positive confirmation: if the lease IS near expiry, the UPDATE still fires.
  /// Seed a stream with a lease about to expire in ~10 s (well below the 100 s
  /// refresh threshold at default 300 s lease_duration). After a tick, the
  /// stored lease must have moved forward.
  /// </summary>
  [Test]
  public async Task WhenLeaseIsNearExpiry_PwbRefreshesLeaseExpiryAsync() {
    var streamId = _idProvider.NewGuid();
    var nearExpiry = DateTimeOffset.UtcNow.AddSeconds(10);
    await _seedActiveStreamAsync(streamId, _instanceId, 10_000, nearExpiry);

    await _sut.ProcessWorkBatchAsync(_emptyTickRequest(_instanceId));

    var leaseAfter = await _getLeaseExpiryAsync(streamId);
    await Assert.That(leaseAfter).IsGreaterThan(nearExpiry.AddMinutes(1))
      .Because("A stream with 10 s of lease remaining is inside the refresh "
             + "threshold; process_work_batch must renew it to now() + "
             + "p_lease_duration_seconds (default 300 s), comfortably past the "
             + "one-minute marker used here as a generous sanity check.");
  }

  /// <summary>
  /// SLA regression guard. A dead instance's stream must still be reclaimable by
  /// another instance once the lease expires — the guard on lease renewal must
  /// not extend the effective orphan-detection window.
  /// </summary>
  [Test]
  public async Task WhenOwningInstanceStopsHeartbeating_StreamIsReleasedAsync() {
    var streamId = _idProvider.NewGuid();
    var deadOwner = _idProvider.NewGuid();
    await _insertServiceInstanceAsync(deadOwner, "TestService", "dead-host", 99999);

    // Dead owner: heartbeat is 60 s old, well past the 30 s stale cutoff.
    await _setHeartbeatAsync(deadOwner, DateTimeOffset.UtcNow.AddSeconds(-60));

    // Stream ownership is held by the dead instance with an expired lease.
    await _seedActiveStreamAsync(streamId, deadOwner, 10_000, DateTimeOffset.UtcNow.AddSeconds(-10));

    // A live instance ticks. cleanup_stale_instances should evict the dead owner
    // and release wh_active_streams.assigned_instance_id.
    await _sut.ProcessWorkBatchAsync(_emptyTickRequest(_instanceId));

    var owner = await _getAssignedInstanceIdAsync(streamId);
    await Assert.That(owner).IsNull()
      .Because("When a stream's owner dies, cleanup_stale_instances must release "
             + "the ownership record so another live instance can re-claim. The "
             + "lease-renewal guard only affects fresh-lease refreshes — it must "
             + "not interfere with cleanup of stale instances or expired leases.");
  }

  // ================== Heartbeat (migration 010) ==================

  /// <summary>
  /// RED against current migration 010: ON CONFLICT DO UPDATE fires on every
  /// call. When the instance's last_heartbeat_at is within the 10 s freshness
  /// window, the UPDATE should be skipped.
  /// </summary>
  [Test]
  public async Task WhenInstanceHeartbeatIsFresh_RegisterIsNoOpAsync() {
    // SetupAsync inserted _instanceId with last_heartbeat_at = NOW().
    var heartbeatBefore = await _getHeartbeatAsync(_instanceId);

    // Call register_instance_heartbeat with p_now = heartbeat + 5 s — well inside
    // the 10 s freshness window.
    var pNow = heartbeatBefore.AddSeconds(5);
    await _callRegisterInstanceHeartbeatAsync(_instanceId, pNow);
    var heartbeatAfter = await _getHeartbeatAsync(_instanceId);

    await Assert.That(heartbeatAfter).IsEqualTo(heartbeatBefore)
      .Because("A heartbeat 5 s old is inside the 10 s freshness window. "
             + "Rewriting wh_service_instances on every tick (per-pod, per-second) "
             + "generates unnecessary WAL under load; the ON CONFLICT DO UPDATE "
             + "must skip when last_heartbeat_at is already fresh.");
  }

  /// <summary>
  /// Positive confirmation: if the heartbeat is stale (&gt; 10 s old),
  /// register_instance_heartbeat does refresh.
  /// </summary>
  [Test]
  public async Task WhenInstanceHeartbeatIsStale_RegisterRefreshesAsync() {
    var heartbeatBefore = await _getHeartbeatAsync(_instanceId);

    var pNow = heartbeatBefore.AddSeconds(11);
    await _callRegisterInstanceHeartbeatAsync(_instanceId, pNow);

    var heartbeatAfter = await _getHeartbeatAsync(_instanceId);
    await Assert.That(heartbeatAfter).IsEqualTo(pNow)
      .Because("A heartbeat 11 s old is outside the 10 s freshness window and "
             + "must be refreshed. The 10 s cadence still leaves 20 s of safety "
             + "margin before cleanup_stale_instances (default 30 s cutoff) would "
             + "mark the instance stale.");
  }

  // ================== Helpers ==================

  private ProcessWorkBatchRequest _emptyTickRequest(Guid instanceId) => new() {
    InstanceId = instanceId,
    ServiceName = "TestService",
    HostName = $"test-host-{instanceId:N}",
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
  };

  private async Task _insertServiceInstanceAsync(Guid instanceId, string serviceName, string hostName, int processId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@instanceId, @serviceName, @hostName, @processId, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()",
      new { instanceId, serviceName, hostName, processId });
  }

  private async Task _setHeartbeatAsync(Guid instanceId, DateTimeOffset heartbeat) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      "UPDATE wh_service_instances SET last_heartbeat_at = @heartbeat WHERE instance_id = @instanceId",
      new { instanceId, heartbeat });
  }

  private async Task _seedActiveStreamAsync(Guid streamId, Guid ownerInstanceId, int partitionCount, DateTimeOffset leaseExpiry) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_active_streams (stream_id, assigned_instance_id, lease_expiry, partition_number, last_activity_at)
      VALUES (@streamId, @ownerInstanceId, @leaseExpiry, compute_partition(@streamId, @partitionCount), NOW())
      ON CONFLICT ON CONSTRAINT wh_active_streams_pkey DO UPDATE SET
        assigned_instance_id = EXCLUDED.assigned_instance_id,
        lease_expiry = EXCLUDED.lease_expiry,
        partition_number = EXCLUDED.partition_number,
        last_activity_at = EXCLUDED.last_activity_at",
      new { streamId, ownerInstanceId, partitionCount, leaseExpiry });
  }

  private async Task<DateTimeOffset> _getLeaseExpiryAsync(Guid streamId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return await connection.QuerySingleAsync<DateTimeOffset>(
      "SELECT lease_expiry FROM wh_active_streams WHERE stream_id = @streamId",
      new { streamId });
  }

  private async Task<Guid?> _getAssignedInstanceIdAsync(Guid streamId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return await connection.QueryFirstOrDefaultAsync<Guid?>(
      "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @streamId",
      new { streamId });
  }

  private async Task<DateTimeOffset> _getHeartbeatAsync(Guid instanceId) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return await connection.QuerySingleAsync<DateTimeOffset>(
      "SELECT last_heartbeat_at FROM wh_service_instances WHERE instance_id = @instanceId",
      new { instanceId });
  }

  private async Task _callRegisterInstanceHeartbeatAsync(Guid instanceId, DateTimeOffset pNow) {
    using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(@"
      SELECT register_instance_heartbeat(
        @p_instance_id,
        @p_service_name,
        @p_host_name,
        @p_process_id,
        @p_metadata::jsonb,
        @p_now,
        @p_lease_expiry
      )",
      new {
        p_instance_id = instanceId,
        p_service_name = "TestService",
        p_host_name = "test-host",
        p_process_id = 12345,
        p_metadata = "{}",
        p_now = pNow,
        p_lease_expiry = pNow.AddSeconds(300)
      });
  }
}
