using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <c>CountServiceBacklogAsync</c> answers "has this SERVICE settled", which auto-repair is gated on.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <c>count_outstanding_work</c>, which is scoped to one INSTANCE. A service runs many
/// instances against one shared inbox, and an instance that has finished its own claimed streams
/// reads zero locally while peers are still draining — so an instance-scoped figure cannot answer
/// this question, and repairing off it re-requests events the caller's own siblings are processing.
/// </para>
/// <para>
/// Getting it wrong in either direction is costly. Reporting settled while work is queued re-enables
/// the storm this gate exists to stop: a consumer that is merely BEHIND confirms false gaps, and the
/// redelivery lengthens the very queue that produced them. Reporting unsettled forever would
/// silently disable self-healing.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
[Category("Shard1")]
public class CountServiceBacklogSqlTests : EFCoreTestBase {

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _seedAsync(
      NpgsqlConnection conn, Guid? instanceId, int count, string leaseOffset, bool processed) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = $@"
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
        SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{{}}', '{{}}', NOW(),
               gen_random_uuid()
        FROM generate_series(1, @n)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts,
         partition_number, instance_id, lease_expiry, processed_at, error, failure_reason)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 1,
             0, @inst, {(instanceId is null ? "NULL" : $"NOW() + INTERVAL '{leaseOffset}'")},
             {(processed ? "NOW()" : "NULL")}, NULL, 99
      FROM m";
    ins.Parameters.AddWithValue("inst", (object?)instanceId ?? DBNull.Value);
    ins.Parameters.AddWithValue("n", count);
    await ins.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task AnEmptyServiceReportsSettledAsync() {
    await using var ctx = CreateDbContext();
    await _openAsync(ctx);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog).IsNotNull()
      .Because("an implemented backend must report a MEASUREMENT; returning null would read as "
             + "unmeasurable and gate repair closed on a service that is genuinely quiet");
    await Assert.That(backlog!.IsSettled).IsTrue();
  }

  [Test]
  public async Task QueuedWorkMakesTheServiceUnsettledAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _seedAsync(conn, instanceId: null, count: 5, leaseOffset: "0 minutes", processed: false);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog!.UnprocessedInboxRows).IsGreaterThan(0);
    await Assert.That(backlog.IsSettled).IsFalse()
      .Because("those rows may be exactly the ones a checkpoint counted as missing — repairing now "
             + "re-delivers work that is already queued and lengthens the queue that caused it");
  }

  [Test]
  public async Task APeersLiveLeaseMakesTheServiceUnsettledAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var peer = Guid.CreateVersion7();
    // Leased by another instance and unprocessed: from THIS instance's view there is nothing to do,
    // but the service is mid-drain.
    await _seedAsync(conn, peer, count: 3, leaseOffset: "5 minutes", processed: false);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog!.ActiveLeasedRows).IsGreaterThan(0)
      .Because("this is the case an instance-scoped count cannot see: a sibling holds the rows "
             + "mid-dispatch, and repairing off a local view re-requests what that peer is already "
             + "working");
    await Assert.That(backlog.IsSettled).IsFalse();
  }

  [Test]
  public async Task QueuedOutboxWorkMakesTheServiceUnsettledAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        SELECT gen_random_uuid(), 'topic', 'TestEvent', '{}', '{}', 0, 0, NOW(), gen_random_uuid(), 0
        FROM generate_series(1, 4)";
      await ins.ExecuteNonQueryAsync();
    }
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog!.PendingOutboxRows).IsEqualTo(4)
      .Because("a producer's load sits in its outbox; a gate that reads only the inbox sees it as idle "
             + "and runs the sweep at the peak");
    await Assert.That(backlog.IsSettled).IsFalse();
  }

  [Test]
  public async Task QueuedPerspectiveEventsMakeTheServiceUnsettledAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (stream_id, perspective_name, event_id, status, attempts, created_at)
        SELECT gen_random_uuid(), 'TestPerspective', gen_random_uuid(), 0, 0, NOW()
        FROM generate_series(1, 3)";
      await ins.ExecuteNonQueryAsync();
    }
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog!.PendingPerspectiveRows).IsEqualTo(3)
      .Because("a consumer mid-drain has stored its inbox and queued everything as perspective events");
    await Assert.That(backlog.IsSettled).IsFalse();
  }

  [Test]
  public async Task ProcessedRowsDoNotHoldTheServiceUnsettledAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _seedAsync(conn, instanceId: null, count: 6, leaseOffset: "0 minutes", processed: true);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog!.UnprocessedInboxRows).IsEqualTo(0)
      .Because("finished work is not a backlog; counting it would keep a drained service pinned "
             + "unsettled and disable self-healing permanently");
    await Assert.That(backlog.IsSettled).IsTrue();
  }

  [Test]
  public async Task AnExpiredLeaseIsNotAnActiveLeaseAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var peer = Guid.CreateVersion7();
    await _seedAsync(conn, peer, count: 4, leaseOffset: "-5 minutes", processed: true);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog!.ActiveLeasedRows).IsEqualTo(0)
      .Because("a lapsed lease means nobody is dispatching those rows — treating it as active would "
             + "keep the service unsettled on the strength of an instance that may be long gone");
  }

  [Test]
  public async Task OldestUnprocessedRowSetsTheLagMeasureAsync() {
    // The lag signal IntegrityRepairPolicy evaluates: depth alone cannot distinguish a small queue
    // that is flowing from a small queue holding a row that has been stuck for an hour. Without
    // this measure the receptor would pass Zero, and a signal wired as a constant is the same
    // silent half-wiring that left the policy itself dormant.
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        WITH m AS (
          INSERT INTO wh_inbox
            (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
          VALUES (gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}', NOW() - INTERVAL '90 minutes',
                  gen_random_uuid()),
                 (gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}', NOW() - INTERVAL '5 minutes',
                  gen_random_uuid())
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state
          (message_id, stream_id, received_at, priority, is_event, status, attempts,
           partition_number, failure_reason)
        SELECT message_id, stream_id, received_at, priority, is_event, 1, 1, 0, 99 FROM m";
      await ins.ExecuteNonQueryAsync();
    }
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog!.OldestUnprocessedAge >= TimeSpan.FromMinutes(80)).IsTrue()
      .Because("the OLDEST unprocessed row defines the lag, not the newest");
  }

  [Test]
  public async Task AnEmptyServiceReportsZeroLagAsync() {
    await using var ctx = CreateDbContext();
    await _openAsync(ctx);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog!.OldestUnprocessedAge).IsEqualTo(TimeSpan.Zero)
      .Because("no queue means no lag; anything else would read an idle service as behind");
  }

}
