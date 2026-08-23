using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the coordinator reports when <c>count_outstanding_work</c> yields no row at all.
/// </summary>
/// <remarks>
/// <para>
/// The shipped function always returns exactly one row — it is a <c>RETURN QUERY SELECT</c> of
/// scalar sub-selects — so this branch is defensive. It is still worth pinning, because the value it
/// must produce is <see langword="null"/> and not zero: zero is a reading that means "holds nothing"
/// and licenses a full-size claim, whereas null means the figure was never obtained and makes the
/// budget stand down. Getting that backwards would disable the bound while looking healthy, which is
/// the exact failure mode this whole area exists to prevent.
/// </para>
/// <para>
/// This lives in its own class deliberately. The setup REPLACES the function, and
/// <c>EFCoreTestBase</c> provisions one database per test class — so a sibling test in the same
/// class would silently inherit the crippled version and pass for the wrong reason.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
[Category("Shard1")]
public class CountOutstandingWorkNoRowSqlTests : EFCoreTestBase {

  [Test]
  public async Task CountOutstandingWork_NoRowReturned_ReportsUnmeasurableRatherThanZeroAsync() {
    await using var ctx = CreateDbContext();
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    var conn = (NpgsqlConnection)connection;

    // Replace the function with one that returns an empty result set.
    await using (var replace = conn.CreateCommand()) {
      replace.CommandText = @"
        CREATE OR REPLACE FUNCTION count_outstanding_work(p_instance_id UUID)
        RETURNS TABLE(inbox_rows BIGINT, outbox_rows BIGINT, perspective_rows BIGINT) AS $$
        BEGIN
          RETURN;  -- no rows
        END;
        $$ LANGUAGE plpgsql;";
      await replace.ExecuteNonQueryAsync();
    }

    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var reported = await coordinator.CountOutstandingWorkAsync(Guid.CreateVersion7());

    await Assert.That(reported).IsNull()
      .Because("no row is an absent measurement, not a measurement of zero — reporting zero would "
             + "tell the budget this instance holds nothing and let it claim at full size on the "
             + "strength of a reading that was never taken");
  }
}
