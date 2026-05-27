using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phase B integration tests for <see cref="IWorkCoordinator.ClaimWorkAsync"/>.
/// Polling-side method; non-empty mapping lands in Phase C worker integration.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
public class EFCoreClaimWorkTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> Coord(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task ClaimWorkAsync_EmptyQueues_ReturnsEmptyBatchAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
      InstanceId: TrackedGuid.NewMedo(),
      ServiceName: "test-svc",
      HostName: "test-host",
      ProcessId: 1));

    await Assert.That(batch.OutboxWork.Count).IsEqualTo(0);
    await Assert.That(batch.InboxWork.Count).IsEqualTo(0);
    await Assert.That(batch.PerspectiveWork.Count).IsEqualTo(0);
    await Assert.That(batch.PerspectiveStreamIds.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimWorkAsync_PerspectiveStreamPresent_ReturnsStreamIdAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = TrackedGuid.NewMedo();
    var streamId = TrackedGuid.NewMedo();

    // Heartbeat.
    await using (var hb = conn.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", (Guid)instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    // Insert a perspective_event row that claim_work will discover.
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@stream, 'TestPerspective', @eid, 0, 0, NOW())";
      ins.Parameters.AddWithValue("stream", (Guid)streamId);
      ins.Parameters.AddWithValue("eid", (Guid)TrackedGuid.NewMedo());
      await ins.ExecuteNonQueryAsync();
    }

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
      InstanceId: instanceId,
      ServiceName: "test",
      HostName: "test-host",
      ProcessId: 1));

    await Assert.That(batch.PerspectiveStreamIds).Contains((Guid)streamId);
  }
}
