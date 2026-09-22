using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>
/// v0.685 lock-in — <c>_emit_event_store_chain_for_inbox</c>'s per-row
/// <c>NOT EXISTS in wh_event_store</c> scan is the dominant cost on the
/// work-pump under heavy inbox load (a production PM measurement: 137 ms mean per
/// call, ~11 % of its DB time). The scan walks every wh_inbox row owned
/// by the instance that's an unprocessed event with a stream_id and
/// PK-looks-up each against the ~600 k-row wh_event_store. Without a
/// dedicated partial index, PG plans a sequential scan + nested-loop
/// anti-join.
/// </para>
/// <para>
/// The lock-in: an index must exist that covers exactly the WHERE shape of emit_chain's outer
/// scan, with <c>message_id</c> reachable from it so PG can pick a merge anti-join against the
/// wh_event_store PK rather than a nested loop over heap tuples.
/// </para>
/// <para>
/// Without this partial index, a future refactor (or a missed apply on a fresh DB) would silently
/// bring back the 137 ms / call regression.
/// </para>
/// <para>
/// <strong>Migration 162 moved the scan, and this test moved with it.</strong> Every column the
/// outer scan filters on -- instance_id, lease_expiry, processed_at, chain_emitted_at -- is work
/// state and now lives on <c>wh_inbox_state</c>, so the scan never touches the wide message row and
/// <c>idx_inbox_emit_chain</c> on <c>wh_inbox</c> is correctly gone: 057 creates it only while the
/// column it names still exists. The replacement is <c>idx_inbox_state_chain_pending</c>, and the
/// property being locked in is unchanged rather than relaxed. Two things did change for the better:
/// migration 158 added <c>chain_emitted_at IS NULL</c> so the pass reads only rows it has never
/// read before, and the replacement INCLUDEs message_id, stream_id and lease_expiry so the pass is
/// index-only. message_id is the primary key of the state table, which does NOT place it in a
/// secondary index, so it is named explicitly.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/work-pump</docs>
[Category("Shard4")]
public class EmitChainInboxIndexTests : EFCoreTestBase {
  /// <summary>The post-162 replacement for idx_inbox_emit_chain, on the work-state table.</summary>
  private const string CHAIN_INDEX = "idx_inbox_state_chain_pending";

  [Test]
  public async Task EmitChainInboxIndex_ExistsAfterMigrationsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    var npgsql = (NpgsqlConnection)connection;

    var exists = await _indexExistsAsync(npgsql, CHAIN_INDEX);

    await Assert.That(exists).IsTrue()
      .Because("v0.685's lock-in MUST survive the work-state split: the partial index backing _emit_event_store_chain_for_inbox's outer scan now lives on wh_inbox_state as idx_inbox_state_chain_pending. A production PM measurement showed the unindexed scan at 137 ms mean (~11 % of its DB time) once wh_event_store grew past ~600 k rows and the inbox handler-delay backlog exceeded ~10 k rows.");
  }

  [Test]
  public async Task EmitChainInboxIndex_HasExpectedPartialPredicateAsync() {
    await using var dbContext = CreateDbContext();
    var connection = dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    var npgsql = (NpgsqlConnection)connection;

    var indexDef = await _indexDefAsync(npgsql, CHAIN_INDEX);

    // The partial predicate must filter to emit_chain's exact outer-scan shape: the
    // instance's unprocessed inbox event rows with a stream_id. If any of these predicates
    // drift (e.g. removing `is_event = true`), the index covers a much larger set of rows
    // and the planner is more likely to pick a sequential scan, losing the v0.685 win.
    await Assert.That(indexDef).IsNotNull()
      .Because("Index must exist; see EmitChainInboxIndex_ExistsAfterMigrationsAsync for the why.");
    await Assert.That(indexDef).Contains("processed_at IS NULL")
      .Because("emit_chain filters out completed rows; the partial index must too.");
    await Assert.That(indexDef).Contains("is_event")
      .Because("emit_chain only emits is_event=true rows; non-event commands are out of scope.");
    await Assert.That(indexDef).Contains("stream_id IS NOT NULL")
      .Because("emit_chain skips unscoped rows (stream_id IS NULL); the partial index excludes them so it stays narrow.");
    await Assert.That(indexDef).Contains("chain_emitted_at IS NULL")
      .Because("migration 158 bounds the pass to rows it has never read; without it every held row is re-checked against the event store on every poll.");
    await Assert.That(indexDef).Contains("message_id")
      .Because("message_id MUST be reachable from the index so PG can plan a merge / hash anti-join against wh_event_store.event_id rather than a nested loop. It is the state table's PRIMARY KEY, which does not put it in a secondary index, so it is carried by INCLUDE.");
    await Assert.That(indexDef).Contains("instance_id")
      .Because("instance_id MUST be in the index key — emit_chain's outer scan is bounded by `i.instance_id = p_instance_id`, and without it in the index PG would scan the whole partial-index range across all instances.");
  }

  private static async Task<bool> _indexExistsAsync(NpgsqlConnection conn, string indexName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE tablename = 'wh_inbox_state' AND indexname = @name
      )
      """;
    cmd.Parameters.AddWithValue("name", indexName);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<string?> _indexDefAsync(NpgsqlConnection conn, string indexName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT indexdef FROM pg_indexes
      WHERE tablename = 'wh_inbox_state' AND indexname = @name
      """;
    cmd.Parameters.AddWithValue("name", indexName);
    var result = await cmd.ExecuteScalarAsync();
    return result as string;
  }
}
