using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// v0.685 lock-in — <c>_emit_event_store_chain_for_inbox</c>'s per-row
/// <c>NOT EXISTS in wh_event_store</c> scan is the dominant cost on the
/// work-pump under heavy inbox load (slot-3 2026-06-11 PM: 137 ms mean per
/// call, ~11 % of slot-3 DB time). The scan walks every wh_inbox row owned
/// by the instance that's an unprocessed event with a stream_id and
/// PK-looks-up each against the ~600 k-row wh_event_store. Without a
/// dedicated partial index, PG plans a sequential scan + nested-loop
/// anti-join.
///
/// The lock-in: an index <c>idx_inbox_emit_chain</c> must exist that covers
/// exactly the WHERE shape of emit_chain's outer scan, with <c>message_id</c>
/// in the key so PG can pick a merge anti-join against the wh_event_store PK.
///
/// Without this partial index, a future refactor of migration 057 (or a
/// missed apply on a fresh DB) would silently bring back the 137 ms / call
/// regression.
/// </summary>
/// <docs>fundamentals/work-coordinator/work-pump</docs>
public class EmitChainInboxIndexTests : EFCoreTestBase {

  [Test]
  public async Task EmitChainInboxIndex_ExistsAfterMigrationsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    var npgsql = (NpgsqlConnection)connection;

    var exists = await _indexExistsAsync(npgsql, "idx_inbox_emit_chain");

    await Assert.That(exists).IsTrue()
      .Because("v0.685 migration 057 MUST create idx_inbox_emit_chain — the partial index that backs _emit_event_store_chain_for_inbox's outer scan. Slot-3 2026-06-11 PM measured the unindexed scan at 137 ms mean (~11 % of slot-3 DB time) once wh_event_store grew past ~600 k rows and the inbox handler-delay backlog exceeded ~10 k rows.");
  }

  [Test]
  public async Task EmitChainInboxIndex_HasExpectedPartialPredicateAsync() {
    await using var dbContext = CreateDbContext();
    var connection = dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    var npgsql = (NpgsqlConnection)connection;

    var indexDef = await _indexDefAsync(npgsql, "idx_inbox_emit_chain");

    // The partial predicate must filter to emit_chain's exact outer-scan shape: the
    // instance's unprocessed inbox event rows with a stream_id. If any of these predicates
    // drift (e.g. removing `is_event = true`), the index covers a much larger set of rows
    // and the planner is more likely to pick a sequential scan, losing the v0.685 win.
    await Assert.That(indexDef).IsNotNull()
      .Because("Index must exist; see EmitChainInboxIndex_ExistsAfterMigrationsAsync for the why.");
    await Assert.That(indexDef!).Contains("processed_at IS NULL")
      .Because("emit_chain filters out completed rows; the partial index must too.");
    await Assert.That(indexDef!).Contains("is_event")
      .Because("emit_chain only emits is_event=true rows; non-event commands are out of scope.");
    await Assert.That(indexDef!).Contains("stream_id IS NOT NULL")
      .Because("emit_chain skips unscoped rows (stream_id IS NULL); the partial index excludes them so it stays narrow.");
    await Assert.That(indexDef!).Contains("message_id")
      .Because("message_id MUST be in the index key so PG can plan a merge / hash anti-join against wh_event_store.event_id (the PK on wh_event_store).");
    await Assert.That(indexDef!).Contains("instance_id")
      .Because("instance_id MUST be in the index key — emit_chain's outer scan is bounded by `i.instance_id = p_instance_id`, and without it in the index PG would scan the whole partial-index range across all instances.");
  }

  private static async Task<bool> _indexExistsAsync(NpgsqlConnection conn, string indexName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE tablename = 'wh_inbox' AND indexname = @name
      )
      """;
    cmd.Parameters.AddWithValue("name", indexName);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<string?> _indexDefAsync(NpgsqlConnection conn, string indexName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT indexdef FROM pg_indexes
      WHERE tablename = 'wh_inbox' AND indexname = @name
      """;
    cmd.Parameters.AddWithValue("name", indexName);
    var result = await cmd.ExecuteScalarAsync();
    return result as string;
  }
}
