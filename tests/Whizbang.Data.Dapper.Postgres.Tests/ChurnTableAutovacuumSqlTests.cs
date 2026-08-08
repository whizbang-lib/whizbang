using Dapper;
using TUnit.Assertions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Structural lock on per-table autovacuum tuning for the delete-churned messaging tables.
/// <para>
/// PostgreSQL's default <c>autovacuum_vacuum_scale_factor</c> of 0.2 means a table is only
/// vacuumed once a fifth of it is dead. On a queue table that is written and deleted continuously
/// that is far too slow: a burst inserts faster than autovacuum reclaims, so the heap grows to
/// cover the peak and never returns. The dead space is reusable but the pages are still real, and
/// a sequential scan reads every one of them — a table can hold a handful of live rows and still
/// take seconds to count, because the scan walks gigabytes of empty pages.
/// </para>
/// <para>
/// Tightening the scale factor keeps space recycling into a bounded steady state instead. Note
/// this is prevention, not cure: plain autovacuum never shrinks a heap that has already grown, so
/// an existing bloated table still needs a rewrite (VACUUM FULL / pg_repack) to hand the space
/// back. These settings are what stop it getting there again.
/// </para>
/// </summary>
public class ChurnTableAutovacuumSqlTests : PostgresTestBase {

  /// <summary>
  /// The tables the framework deletes from on the messaging hot path. Append-mostly tables
  /// (wh_dead_letters is forensic, wh_event_store keeps its pointers) are deliberately excluded —
  /// aggressive autovacuum buys nothing where rows are not being deleted.
  /// </summary>
  public static IEnumerable<Func<string>> ChurnTables() {
    yield return () => "wh_inbox";
    yield return () => "wh_outbox";
    yield return () => "wh_perspective_events";
    yield return () => "wh_message_deduplication";
    yield return () => "wh_active_streams";
    // Already tuned when the ephemeral body reaper landed; included so the guarantee is asserted
    // in one place and cannot silently regress.
    yield return () => "wh_event_body";
  }

  [Test]
  [MethodDataSource(nameof(ChurnTables))]
  public async Task ChurnTable_HasAggressiveAutovacuumSettingsAsync(string tableName) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    var options = (await connection.QueryAsync<string>(@"
      SELECT unnest(c.reloptions)
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
      WHERE c.relname = @tableName AND n.nspname = current_schema()",
      new { tableName })).ToList();

    await Assert.That(options).Contains("autovacuum_vacuum_scale_factor=0.02");
    await Assert.That(options).Contains("autovacuum_analyze_scale_factor=0.02");
  }
}
