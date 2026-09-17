using System.Globalization;
using Npgsql;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What a piece of work cost the database, counted in work done rather than in time taken.
/// </summary>
/// <remarks>
/// <para>
/// Wall clock is not a measurement: it moves with the machine, with the neighbors and with the
/// cache, and a budget expressed in milliseconds either fails on a slow runner or hides a
/// forty-fold regression on a fast one. What a statement makes the database do -- pages touched,
/// tuples examined, rows written -- is a property of the plan and of the data, and it is the same
/// number on a laptop and on a server.
/// </para>
/// <para>
/// Four things will silently corrupt these numbers, and each of them is handled here:
/// </para>
/// <list type="number">
/// <item><description>
/// A backend caches the statistics views for the length of its transaction, so a before and an
/// after read on the connection doing the work return the same number. Every read here opens a
/// connection of its own, after asking the working backend to flush.
/// </description></item>
/// <item><description>
/// <c>pg_stat_statements</c> evicts entries under pressure and recreates them from zero. Diffing two
/// snapshots with an inner join silently drops every statement that was evicted in between, which
/// is exactly the busiest ones. The diff here is a left join with a zero default, so a recreated
/// entry contributes its whole post-snapshot value rather than disappearing.
/// </description></item>
/// <item><description>
/// With nested statement tracking on, a statement is charged its children's blocks as well as its
/// own, so summing top-level and nested rows counts the same work twice. The two are kept apart:
/// top-level rows are the cost, nested rows only explain where inside a call it went.
/// </description></item>
/// <item><description>
/// A queue table under load is never all-visible, so a scan that is index-only on a quiet database
/// fetches a heap page per row on a busy one. A scenario that vacuums before it measures is
/// measuring a plan shape production does not have. Nothing here vacuums; scenarios disable
/// autovacuum on the tables they measure instead, which keeps its reads out of the window without
/// also removing the dead rows.
/// </description></item>
/// </list>
/// </remarks>
public sealed class WorkCostRecorder {
  private readonly string _connectionString;

  public WorkCostRecorder(string connectionString) => _connectionString = connectionString;

  /// <summary>Per-table counters: what the tables gave up.</summary>
  public readonly record struct TableCost(
    long SequentialScans,
    long SequentialTuplesRead,
    long IndexScans,
    long IndexTuplesRead,
    long RowsInserted,
    long RowsUpdated,
    long Blocks) {
    public static TableCost operator -(TableCost a, TableCost b) => new(
      a.SequentialScans - b.SequentialScans,
      a.SequentialTuplesRead - b.SequentialTuplesRead,
      a.IndexScans - b.IndexScans,
      a.IndexTuplesRead - b.IndexTuplesRead,
      a.RowsInserted - b.RowsInserted,
      a.RowsUpdated - b.RowsUpdated,
      a.Blocks - b.Blocks);
  }

  /// <summary>Per-statement counters, for the statements that carry the cost.</summary>
  public readonly record struct StatementCost(string Statement, bool TopLevel, long Calls, long Rows, long Blocks);

  /// <summary>Everything one measured window cost.</summary>
  public sealed record Window(
    IReadOnlyDictionary<string, TableCost> Tables,
    IReadOnlyList<StatementCost> Statements,
    bool StatementTrackingAvailable);

  /// <summary>
  /// Runs <paramref name="work"/> and reports what it cost. <paramref name="worker"/> is the
  /// connection the work runs on; its counters are flushed before each read.
  /// </summary>
  public async Task<Window> MeasureAsync(
      NpgsqlConnection worker, IReadOnlyList<string> tables, Func<Task> work) {
    var tracking = await _statementTrackingAvailableAsync();
    if (tracking) {
      await _resetStatementsAsync();
    }
    var before = await _tablesAsync(worker, tables);

    await work();

    var after = await _tablesAsync(worker, tables);
    var statements = tracking ? await _statementsAsync(worker) : [];
    return new Window(
      tables.ToDictionary(t => t, t => after[t] - before[t]),
      statements,
      tracking);
  }

  private async Task<bool> _statementTrackingAvailableAsync() {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    // The extension has to be installed AND its library preloaded; the view exists only when both
    // are true, so asking the catalog for the view answers both questions at once.
    cmd.CommandText = "SELECT to_regclass('pg_stat_statements') IS NOT NULL";
    try {
      return await cmd.ExecuteScalarAsync() is true;
    } catch (PostgresException) {
      return false;
    }
  }

  private async Task _resetStatementsAsync() {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT pg_stat_statements_reset()";
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Flushes the working backend's counters, then reads the per-table views over a connection of
  /// its own. Index counters are summed over a table's indexes and folded in beside the heap ones,
  /// because a plan that moves work from the heap to an index has not made it free.
  /// </summary>
  private async Task<Dictionary<string, TableCost>> _tablesAsync(
      NpgsqlConnection worker, IReadOnlyList<string> tables) {
    await using (var flush = worker.CreateCommand()) {
      flush.CommandText = "SELECT pg_stat_force_next_flush()";
      await flush.ExecuteNonQueryAsync();
    }
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT u.relname,
             u.seq_scan, u.seq_tup_read, COALESCE(u.idx_scan, 0), COALESCE(u.idx_tup_fetch, 0),
             u.n_tup_ins, u.n_tup_upd,
             t.heap_blks_read + t.heap_blks_hit + COALESCE(x.idx_blocks, 0)
      FROM pg_stat_user_tables u
      JOIN pg_statio_user_tables t ON t.relid = u.relid
      LEFT JOIN (
        SELECT i.relid, SUM(i.idx_blks_read + i.idx_blks_hit) AS idx_blocks
        FROM pg_statio_user_indexes i GROUP BY i.relid
      ) x ON x.relid = u.relid
      WHERE u.relname = ANY(@tables)";
    cmd.Parameters.AddWithValue("tables", tables.ToArray());
    var costs = new Dictionary<string, TableCost>(StringComparer.Ordinal);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      costs[reader.GetString(0)] = new TableCost(
        reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
        reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }
    // A table nothing has touched yet has no row in the views; absent means zero, not missing.
    foreach (var table in tables) {
      costs.TryAdd(table, default);
    }
    return costs;
  }

  /// <summary>
  /// The statements of this database since the reset, top-level and nested kept apart. The query
  /// text is kept long enough to carry a function name: <c>claim_work</c> reads as
  /// "SELECT source, work_id, work_stream_id, ..." and a statement truncated to its first clause
  /// loses the only thing that identifies it.
  /// </summary>
  private async Task<List<StatementCost>> _statementsAsync(NpgsqlConnection worker) {
    await using (var flush = worker.CreateCommand()) {
      flush.CommandText = "SELECT pg_stat_force_next_flush()";
      await flush.ExecuteNonQueryAsync();
    }
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT left(regexp_replace(query, '\s+', ' ', 'g'), 240) AS statement,
             toplevel, calls, rows, shared_blks_hit + shared_blks_read AS blocks
      FROM pg_stat_statements
      WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
        AND query NOT ILIKE '%pg_stat_statements%'
        AND query NOT ILIKE '%pg_stat_force_next_flush%'
      ORDER BY blocks DESC
      LIMIT 40";
    var rows = new List<StatementCost>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new StatementCost(
        reader.GetString(0), reader.GetBoolean(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
    }
    return rows;
  }

  /// <summary>
  /// A measurement normalized to a unit of work, which is the only form two runs can be compared
  /// in. A raw total says nothing without the work it bought.
  /// </summary>
  public readonly record struct Measure(string Name, double Value, string Unit) {
    public string Format() =>
      $"{Name,-52} {Value.ToString("N1", CultureInfo.InvariantCulture),14}  {Unit}";
  }
}
