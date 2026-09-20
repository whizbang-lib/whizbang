using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// No outbox index may be partial on a <c>status</c> bitmask, because nothing queries that shape any
/// more and a planner cannot reach one from what does.
/// </summary>
/// <remarks>
/// <para>
/// The outbox once discriminated claimable and completed rows with bits in <c>status</c>; it now uses
/// <c>processed_at</c>. A partial index is considered only when Postgres can prove the query's
/// predicate implies the index's, and that proof is textual: <c>processed_at IS NULL</c> says nothing
/// about <c>(status &amp; 4) &lt;&gt; 4</c>. So every index left behind on the old shape is unreachable
/// by construction -- it cannot be chosen, whatever the data looks like.
/// </para>
/// <para>
/// Unreachable is not free. Each one is still maintained on every insert, update and delete the
/// outbox takes, which on the producer side of a bulk load is the hottest write path in the system.
/// Migration 160 documented this for one of them, naming <c>idx_outbox_stream_pending</c> as an
/// index on a status bit that a planner cannot reach from <c>processed_at IS NULL</c> -- and then
/// added a replacement beside it rather than removing it. A deployed fleet carried four such
/// indexes at zero scans through an entire bulk import.
/// </para>
/// <para>
/// Stated as a rule rather than a list of four names on purpose: the next index written against the
/// retired shape is the same defect, and a list would not catch it.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Shard4")]
public class OutboxIndexesMatchLiveQueriesTests : EFCoreTestBase {

  private static async Task<List<string>> _outboxIndexDefsAsync(DbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT indexname || ' :: ' || indexdef FROM pg_indexes WHERE tablename = 'wh_outbox' ORDER BY indexname";
    var defs = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      defs.Add(reader.GetString(0));
    }
    return defs;
  }

  [Test]
  public async Task NoOutboxIndex_IsPartialOnAStatusBitmaskAsync() {
    await using var ctx = CreateDbContext();
    var defs = await _outboxIndexDefsAsync(ctx);

    var bitmask = defs
      .Where(d => d.Contains("status &", StringComparison.Ordinal))
      .ToList();

    await Assert.That(bitmask).IsEmpty()
      .Because("an index partial on a status bitmask cannot be reached from the processed_at "
        + "predicates the outbox actually queries -- textual implication, not arithmetic -- so it "
        + "is unreachable by construction while still costing every outbox write. Found:\n  "
        + string.Join("\n  ", bitmask));
  }

  [Test]
  public async Task TheOutboxIndexInventory_IsActuallyBeingReadAsync() {
    // A rule derived from a catalog query passes the moment the query stops finding the table, which
    // is exactly the failure this class would otherwise hide.
    await using var ctx = CreateDbContext();
    var defs = await _outboxIndexDefsAsync(ctx);

    await Assert.That(defs.Count).IsGreaterThan(8)
      .Because("the outbox carries a substantial index set; finding almost none means the "
        + "inventory query is not seeing the table rather than the table being clean.");
    await Assert.That(defs.Any(d => d.Contains("processed_at IS NULL", StringComparison.Ordinal))).IsTrue()
      .Because("the predicates the outbox does query must be present, or this is not the schema "
        + "the rule is about.");
  }
}
