using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper coordinator's outstanding-work count, which sizes the claim-outstanding budget.
/// </summary>
/// <remarks>
/// <para>
/// This path had no test when it was written. Dapper binds ValueTuple members by POSITION rather
/// than by name — verified, not assumed: aliasing the columns to PascalCase was tried and the test
/// still passed. So column ORDER is the fragile part. Reordering the SELECT would put counts in the
/// wrong fields and still yield plausible numbers rather than an error, and an inbox count that
/// lands as zero is indistinguishable from a healthy idle instance.
/// </para>
/// <para>
/// The EFCore coordinator is covered separately; both back the same contract, so both are tested.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
public class DapperCountOutstandingWorkTests : PostgresTestBase {

  private async Task _seedInboxAsync(Guid instanceId, int count, string leaseOffset, bool processed) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
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

  [Test]
  public async Task CountOutstandingWork_MapsEveryColumnAndExcludesLapsedAndProcessedAsync() {
    var mine = Guid.CreateVersion7();
    await _seedInboxAsync(mine, 6, "5 minutes", processed: false);   // held   -> counts
    await _seedInboxAsync(mine, 2, "5 minutes", processed: true);    // done   -> excluded
    await _seedInboxAsync(mine, 3, "-5 minutes", processed: false);  // lapsed -> excluded

    var coordinator = new DapperWorkCoordinator(
      ConnectionString, JsonContextRegistry.CreateCombinedOptions());

    var reported = await coordinator.CountOutstandingWorkAsync(mine);

    await Assert.That(reported).IsNotNull()
      .Because("a backend that can measure must return a value — null means 'cannot measure' and "
             + "makes the budget stand down entirely");

    // A non-zero inbox count is what proves the column actually mapped. Zero here would be
    // indistinguishable from an idle instance, which is precisely how a mapping fault on this path
    // would hide: the budget would read no outstanding work and never bound anything.
    await Assert.That(reported!.InboxRows).IsEqualTo(6)
      .Because("only live-leased, unprocessed rows are held work — and a mapped-to-zero column "
             + "would look exactly like an idle instance rather than like a bug");
    await Assert.That(reported.Total).IsEqualTo(6);
  }
}
