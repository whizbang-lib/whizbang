using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the coalesced outstanding-count round trip (#635): when the claim request asks for it,
/// the batch carries this instance's UNTRUNCATED held-work counts from the same command and
/// snapshot as the claim, and when it does not ask, the batch carries null — never zero.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard2")]
public class ClaimWorkOutstandingCoalesceTests : EFCoreTestBase {

  private static async Task _seedLeasedInboxAsync(NpgsqlConnection conn, Guid instanceId, int rows) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts,
         received_at, stream_id, partition_number, instance_id, lease_expiry, failure_reason)
      SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}', 1, 1,
             NOW(), gen_random_uuid(), 0, @inst, NOW() + INTERVAL '5 minutes', 99
      FROM generate_series(1, @n)";
    ins.Parameters.AddWithValue("inst", instanceId);
    ins.Parameters.AddWithValue("n", rows);
    await ins.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task IncludeOutstanding_CarriesUntruncatedCountsOnTheBatchAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var instanceId = (Guid)TrackedGuid.NewMedo();
    // More held rows than the claim below may return: the counts must come from the untruncated
    // probe, not from the claim's LIMITed CTEs, or the budget reads its own output.
    await _seedLeasedInboxAsync(conn, instanceId, rows: 7);

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc", "host", 1, MaxStreams: 2, IncludeOutstanding: true));

    await Assert.That(batch.Outstanding).IsNotNull();
    await Assert.That(batch.Outstanding!.InboxRows).IsGreaterThanOrEqualTo(7)
      .Because("the coalesced counts are the SAME untruncated measure count_outstanding_work "
             + "reports, carried on the claim's own round trip");
  }

  [Test]
  public async Task WithoutTheFlag_OutstandingStaysNull_NeverZeroAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _seedLeasedInboxAsync(conn, instanceId, rows: 3);

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc", "host", 1, MaxStreams: 2));

    await Assert.That(batch.Outstanding is null).IsTrue()
      .Because("not asked means not measured; a caller must fall back to the probe, and null is "
             + "the value that forces that — zero would license a full-size claim off nothing");
  }
}
