using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks the settledness measure to the CLAIM's eligibility predicate. Rows parked with a
/// future <c>scheduled_for</c> (operator quarantine, tag-bound coalescing) are deliberately not
/// claimable — and a backlog counter that still counts them reports a service as busy forever.</para>
/// <para>Observed in production: ~10,000 operator-parked rows made the counter report its 1000-row
/// cap at every housekeeping check, so dead-letter recovery and maintenance deferred on
/// ServiceBusy indefinitely while the service sat genuinely idle — 20,000 due dead letters behind
/// a measurement artifact.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard2")]
public class ServiceBacklogEligibilityTests : EFCoreTestBase {

  [Test]
  public async Task ParkedRows_DoNotCountAsBacklog_AndDoNotAgeAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        WITH m AS (
          INSERT INTO wh_inbox
            (message_id, handler_name, message_type, event_data, metadata,
             received_at, stream_id)
          SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}',
                 NOW() - INTERVAL '2 days', gen_random_uuid()
          FROM generate_series(1, 25)
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state
          (message_id, stream_id, received_at, priority, is_event,
           status, attempts, partition_number, scheduled_for)
        SELECT message_id, stream_id, received_at, priority, is_event,
               1, 3, 0, NOW() + INTERVAL '30 days' FROM m";
      await ins.ExecuteNonQueryAsync();
    }

    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog).IsNotNull();
    await Assert.That(backlog!.UnprocessedInboxRows).IsEqualTo(0)
      .Because("parked rows are not claimable, so they are not busy-ness — counting them held "
             + "recovery and maintenance on ServiceBusy for a full day against an idle service");
    await Assert.That(backlog.OldestUnprocessedAge).IsEqualTo(TimeSpan.Zero)
      .Because("a two-day-old parked row is not lag; it is a deliberate operator decision");
  }

  [Test]
  public async Task DueScheduledRows_CountAgainAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        WITH m AS (
          INSERT INTO wh_inbox
            (message_id, handler_name, message_type, event_data, metadata,
             received_at, stream_id)
          VALUES (gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}',
                  NOW() - INTERVAL '1 minute', gen_random_uuid())
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state
          (message_id, stream_id, received_at, priority, is_event,
           status, attempts, partition_number, scheduled_for)
        SELECT message_id, stream_id, received_at, priority, is_event,
               1, 0, 0, NOW() - INTERVAL '1 second' FROM m";
      await ins.ExecuteNonQueryAsync();
    }

    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog!.UnprocessedInboxRows).IsGreaterThanOrEqualTo(1)
      .Because("a schedule that has come due is claimable work again, and hiding it would let "
             + "housekeeping run over a queue that is about to move");
  }
}
