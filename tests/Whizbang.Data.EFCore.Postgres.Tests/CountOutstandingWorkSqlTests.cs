using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <c>count_outstanding_work</c> is what the claim-outstanding budget is sized against, so every row
/// it miscounts moves the bound in one direction or the other.
/// </summary>
/// <remarks>
/// <para>
/// Over-counting holds the budget closed against work the instance is not really holding and
/// throttles a healthy service. Under-counting is how the failure this exists to prevent happened in
/// the first place: the previous implementation took the figure from the claim response, which
/// <c>claim_work</c> truncates with <c>LIMIT p_max_streams</c>, so it could never exceed the limit
/// the budget itself produced.
/// </para>
/// <para>
/// The boundary that matters most here is the lease. An EXPIRED lease is not held work — the store
/// has already made those rows available to every other instance — so counting them would keep an
/// instance throttled against a backlog it no longer owns.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
public class CountOutstandingWorkSqlTests : EFCoreTestBase {

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  /// <summary>Seeds inbox rows with a chosen lease and processed state.</summary>
  /// <param name="conn">Open connection.</param>
  /// <param name="instanceId">Instance the rows are leased to.</param>
  /// <param name="count">How many rows to insert.</param>
  /// <param name="leaseOffset">Interval added to NOW() for lease_expiry; negative means expired.</param>
  /// <param name="processed">Whether the rows are already processed.</param>
  private static async Task _seedInboxAsync(
      NpgsqlConnection conn, Guid instanceId, int count, string leaseOffset, bool processed) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = $@"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         stream_id, partition_number, instance_id, lease_expiry, processed_at, error, failure_reason)
      SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{{}}', '{{}}', 1, 1, NOW(),
             gen_random_uuid(), 0, @inst, NOW() + INTERVAL '{leaseOffset}',
             {(processed ? "NOW()" : "NULL")}, NULL, 99
      FROM generate_series(1, @n)";
    ins.Parameters.AddWithValue("inst", instanceId);
    ins.Parameters.AddWithValue("n", count);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task<(long Inbox, long Outbox, long Perspective)> _countAsync(
      NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT inbox_rows, outbox_rows, perspective_rows FROM count_outstanding_work(@inst)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
  }

  [Test]
  public async Task CountOutstandingWork_CountsOnlyLiveLeasedUnprocessedRowsForThisInstanceAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var mine = Guid.CreateVersion7();
    var theirs = Guid.CreateVersion7();

    await _seedInboxAsync(conn, mine, 7, "5 minutes", processed: false);    // held  -> counts
    await _seedInboxAsync(conn, mine, 4, "5 minutes", processed: true);     // done  -> must not count
    await _seedInboxAsync(conn, mine, 3, "-5 minutes", processed: false);   // lapsed-> must not count
    await _seedInboxAsync(conn, theirs, 9, "5 minutes", processed: false);  // not mine

    var counts = await _countAsync(conn, mine);

    await Assert.That(counts.Inbox).IsEqualTo(7)
      .Because("only rows this instance holds under a LIVE lease and has not finished are "
             + "outstanding — processed rows are done, lapsed leases already belong to whoever "
             + "claims them next, and another instance's work was never this one's to bound against");
  }

  [Test]
  public async Task CountOutstandingWork_ReturnsZeroForAnInstanceHoldingNothingAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var idle = Guid.CreateVersion7();

    var counts = await _countAsync(conn, idle);

    // Zero is a real measurement here and must be reported as such. The worker distinguishes it from
    // "unmeasurable", which is signalled by returning no OutstandingWork at all — conflating the two
    // would either throttle an idle instance or license a full-size claim off an unread figure.
    await Assert.That(counts.Inbox).IsEqualTo(0);
    await Assert.That(counts.Outbox).IsEqualTo(0);
    await Assert.That(counts.Perspective).IsEqualTo(0);
  }

  [Test]
  public async Task EFCoreCoordinator_ReportsTheSameFigureTheFunctionDoesAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var mine = Guid.CreateVersion7();
    await _seedInboxAsync(conn, mine, 5, "5 minutes", processed: false);

    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var reported = await coordinator.CountOutstandingWorkAsync(mine);

    await Assert.That(reported).IsNotNull()
      .Because("a backend that CAN measure must return a value — null is reserved for 'cannot "
             + "measure', and the budget declines to engage when it sees it");
    await Assert.That(reported!.InboxRows).IsEqualTo(5);
    await Assert.That(reported.Total).IsEqualTo(5)
      .Because("Total spans inbox, outbox and perspective because all three are leased and all "
             + "three charge attempts — bounding one column would let the same arithmetic recur "
             + "in another rather than stop");
  }
}
