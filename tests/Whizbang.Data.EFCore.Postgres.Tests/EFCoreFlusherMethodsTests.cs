using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phase B integration tests for the small fire-and-forget flusher methods on
/// <see cref="IWorkCoordinator"/>: CompleteOutboxPublishedAsync, CompletePerspectiveAsync,
/// RenewLeasesAsync. Each is a thin wrapper around its Phase A SQL function.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public class EFCoreFlusherMethodsTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> Coord(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task CompleteOutboxPublishedAsync_DeletesRowsInProductionModeAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
    foreach (var id in ids) {
      await using var ins = conn.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", id);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    var affected = await coordinator.CompleteOutboxPublishedAsync(ids, debugMode: false);

    await Assert.That(affected).IsEqualTo(2);

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids)";
    verify.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
    var remaining = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(remaining).IsEqualTo(0L);
  }

  [Test]
  public async Task CompleteOutboxPublishedAsync_EmptyList_ReturnsZeroAndNoSqlAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var updated = await coordinator.CompleteOutboxPublishedAsync([], debugMode: false);
    await Assert.That(updated).IsEqualTo(0);
  }

  [Test]
  public async Task CompletePerspectiveAsync_DeletesProvidedEventWorkIdsAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var workId = Guid.NewGuid();
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@work, @stream, 'TestPerspective', @eid, 0, 0, NOW())";
      ins.Parameters.AddWithValue("work", workId);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      ins.Parameters.AddWithValue("eid", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    await coordinator.CompletePerspectiveAsync(
      cursors: [],
      eventWorkIds: [workId],
      debugMode: false);

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_perspective_events WHERE event_work_id = @work";
    verify.Parameters.AddWithValue("work", workId);
    var remaining = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(remaining).IsEqualTo(0L);
  }

  [Test]
  public async Task ReportFailuresAsync_OutboxCategory_IncrementsAttemptsAndSetsErrorAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var msgId = Guid.NewGuid();
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", msgId);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    await coordinator.ReportFailuresAsync(WorkCategory.Outbox, [
      new MessageFailure {
        MessageId = msgId,
        CompletedStatus = MessageProcessingStatus.Stored,
        Error = "transport publish exploded",
        Reason = MessageFailureReason.Unknown
      }
    ]);

    // Phase H step 8 — claim_orphaned_* is the sole attempt counter; ReportFailures
    // records the error + releases the lease but doesn't bump attempts. The initial
    // attempts=0 stays 0 until a subsequent claim_orphaned_outbox re-claims this row.
    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT attempts, error FROM wh_outbox WHERE message_id = @msg";
    verify.Parameters.AddWithValue("msg", msgId);
    await using var reader = await verify.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    await Assert.That(reader.GetInt32(0)).IsEqualTo(0);
    await Assert.That(reader.GetString(1)).IsEqualTo("transport publish exploded");
  }

  [Test]
  public async Task RenewLeasesAsync_OutboxCategory_ExtendsLeaseExpiryAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var msgId = Guid.NewGuid();

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(),
                @inst, NOW() + INTERVAL '5 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", msgId);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    DateTime initial;
    await using (var read = conn.CreateCommand()) {
      read.CommandText = "SELECT lease_expiry FROM wh_outbox WHERE message_id = @msg";
      read.Parameters.AddWithValue("msg", msgId);
      initial = Convert.ToDateTime(await read.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    var updated = await coordinator.RenewLeasesAsync(WorkCategory.Outbox, [msgId]);

    await Assert.That(updated).IsEqualTo(1);

    DateTime after;
    await using (var read = conn.CreateCommand()) {
      read.CommandText = "SELECT lease_expiry FROM wh_outbox WHERE message_id = @msg";
      read.Parameters.AddWithValue("msg", msgId);
      after = Convert.ToDateTime(await read.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
    await Assert.That(after).IsGreaterThan(initial);
  }
}
