using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 5 of zero-idle-polling — locks the opportunistic heartbeat
/// piggyback behavior inside
/// <see cref="EFCoreWorkCoordinator{TDbContext}.CompleteOutboxPublishedAsync"/>.
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>When the coordinator is constructed with an
/// <see cref="IServiceInstanceProvider"/> AND the heartbeat row is older
/// than 10 s, calling <c>CompleteOutboxPublishedAsync</c> UPDATEs
/// <c>last_heartbeat_at</c> on the calling instance's row.</description></item>
/// <item><description>When the heartbeat row is fresh (≤ 10 s old), the
/// UPDATE is a no-op — the freshness guard inside the SQL predicate
/// returns 0 rows, no WAL pressure.</description></item>
/// <item><description>When no <see cref="IServiceInstanceProvider"/> is
/// wired (the historical contract), the coordinator skips the piggyback
/// entirely — no SQL is emitted.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public class OpportunisticHeartbeatSqlTests : EFCoreTestBase {

  private sealed class StubInstanceProvider(Guid id) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = id;
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  [Test]
  public async Task CompleteOutboxPublished_StaleHeartbeat_RefreshesItAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceId = (Guid)TrackedGuid.NewMedo();
    // Heartbeat row stamped 60 s in the past — older than the 10 s freshness
    // guard inside the piggyback's WHERE clause.
    await _registerInstanceAsync(conn, instanceId, lastHeartbeatOffset: TimeSpan.FromSeconds(-60));
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext,
      jsonOptions: new JsonSerializerOptions(),
      logger: NullLogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>.Instance,
      instanceProvider: new StubInstanceProvider(instanceId));
    var heartbeatBefore = await _readHeartbeatAsync(conn, instanceId);

    // CompleteOutboxPublishedAsync with an empty list still runs the
    // function but returns early before reaching the piggyback — use a
    // dummy ID just to exercise the full path.
    await coordinator.CompleteOutboxPublishedAsync([(Guid)TrackedGuid.NewMedo()], debugMode: false);

    var heartbeatAfter = await _readHeartbeatAsync(conn, instanceId);
    await Assert.That(heartbeatAfter).IsGreaterThan(heartbeatBefore)
      .Because("Slice 5 piggyback must UPDATE last_heartbeat_at when the freshness window has elapsed — that's the entire point of keeping heartbeats fresh during continuous work.");
  }

  [Test]
  public async Task CompleteOutboxPublished_FreshHeartbeat_NoOpAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceId = (Guid)TrackedGuid.NewMedo();
    // Heartbeat row is fresh — inside the 10 s freshness guard.
    await _registerInstanceAsync(conn, instanceId, lastHeartbeatOffset: TimeSpan.FromSeconds(-2));
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext,
      jsonOptions: new JsonSerializerOptions(),
      logger: NullLogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>.Instance,
      instanceProvider: new StubInstanceProvider(instanceId));
    var heartbeatBefore = await _readHeartbeatAsync(conn, instanceId);

    await coordinator.CompleteOutboxPublishedAsync([(Guid)TrackedGuid.NewMedo()], debugMode: false);

    var heartbeatAfter = await _readHeartbeatAsync(conn, instanceId);
    await Assert.That(heartbeatAfter).IsEqualTo(heartbeatBefore)
      .Because("Freshness guard: when last_heartbeat_at is within 10 s of NOW(), the WHERE clause produces 0 rows and no UPDATE is performed — keeps the piggyback's cost negligible during continuous work.");
  }

  [Test]
  public async Task CompleteOutboxPublished_NoInstanceProvider_SkipsPiggybackAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, instanceId, lastHeartbeatOffset: TimeSpan.FromSeconds(-60));
    // Coordinator built without IServiceInstanceProvider — historical
    // construction contract still works, just skips the piggyback.
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext,
      jsonOptions: new JsonSerializerOptions(),
      logger: NullLogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>.Instance);
    var heartbeatBefore = await _readHeartbeatAsync(conn, instanceId);

    await coordinator.CompleteOutboxPublishedAsync([(Guid)TrackedGuid.NewMedo()], debugMode: false);

    var heartbeatAfter = await _readHeartbeatAsync(conn, instanceId);
    await Assert.That(heartbeatAfter).IsEqualTo(heartbeatBefore)
      .Because("Backward-compat path: when the coordinator is constructed without an instance provider (e.g., tests, programs that wire it directly without Whizbang's DI), the piggyback is silently skipped.");
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<DateTimeOffset> _readHeartbeatAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT last_heartbeat_at FROM wh_service_instances WHERE instance_id = @id";
    cmd.Parameters.AddWithValue("id", instanceId);
    var raw = await cmd.ExecuteScalarAsync();
    return raw switch {
      DateTimeOffset dto => dto,
      DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
      _ => throw new InvalidOperationException($"Unexpected type for last_heartbeat_at: {raw?.GetType().Name ?? "null"}"),
    };
  }

  private static async Task _registerInstanceAsync(
      NpgsqlConnection conn, Guid instanceId, TimeSpan? lastHeartbeatOffset = null) {
    var hb = lastHeartbeatOffset.HasValue
      ? DateTimeOffset.UtcNow + lastHeartbeatOffset.Value
      : DateTimeOffset.UtcNow;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, @hb, @hb, '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = EXCLUDED.last_heartbeat_at";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter("hb", NpgsqlDbType.TimestampTz) { Value = hb });
    await cmd.ExecuteNonQueryAsync();
  }
}
