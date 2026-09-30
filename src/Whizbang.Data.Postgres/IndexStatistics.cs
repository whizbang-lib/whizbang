using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Analyzes tables whose indexes the planner has no statistics for yet (issue #1004).
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL gathers statistics for an index expression only when the table is analyzed, and autovacuum
/// analyzes a table only once about a tenth of its rows have changed. Until then a predicate on the
/// expression is estimated with a fixed default. For <c>IS NOT NULL</c> that default says nearly every
/// row matches, so a selective predicate over a new index is planned as a scan of the whole table and the
/// index goes unused.
/// </para>
/// <para>
/// Three callers: the schema pass, once its transaction has committed, analyzes the tables
/// <c>wh_ensure_index</c> queued (migration 178); the stored-format rewrite analyzes the tables it
/// rewrote, since a mass update skews their statistics too; and the maintenance step analyzes, a bounded
/// number at a time, perspective tables whose expression indexes still have no statistics.
/// </para>
/// <para>
/// <c>ANALYZE</c> samples the table and takes a lock that blocks neither reads nor writes, so running it
/// once per table an index was added to is cheap. Each table is analyzed in its own statement, and a
/// table that fails is reported and left for the maintenance step; nothing here is fatal.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes#statistics-for-a-new-index</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/IndexStatisticsInitializationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/CanonicalTemporalRewritePhaseTests.cs:ARewrittenTableIsAnalyzedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/IndexStatisticsMaintenanceStepTests.cs</tests>
public static partial class IndexStatistics {
  /// <summary>
  /// The transaction-local setting a rewrite statement appends the tables it changed to, one per line.
  /// </summary>
  /// <remarks>
  /// Transaction-local, so a rewrite rolled back to its savepoint takes its entry with it, and nothing
  /// outlives the rewrite phase's transaction.
  /// </remarks>
  public const string REWRITTEN_TABLES_SETTING = "whizbang.rewritten_tables";

  /// <summary>
  /// The PL/pgSQL statement a rewrite runs once it has changed rows of a table, so the rewrite phase
  /// analyzes that table after it commits.
  /// </summary>
  /// <param name="schemaExpression">A SQL expression for the table's schema name, such as a quoted literal.</param>
  /// <param name="tableExpression">A SQL expression for the table's name, such as a quoted literal.</param>
  /// <returns>One <c>PERFORM</c> statement, ending in a semicolon.</returns>
  public static string MarkRewrittenSql(string schemaExpression, string tableExpression) {
    ArgumentException.ThrowIfNullOrWhiteSpace(schemaExpression);
    ArgumentException.ThrowIfNullOrWhiteSpace(tableExpression);
    return $"PERFORM set_config('{REWRITTEN_TABLES_SETTING}', "
      + $"coalesce(current_setting('{REWRITTEN_TABLES_SETTING}', true), '') "
      + $"|| format('%I.%I', {schemaExpression}, {tableExpression}) || E'\\n', true);";
  }

  /// <summary>
  /// The tables the rewrites run so far in <paramref name="transaction"/> have marked as changed.
  /// </summary>
  /// <param name="connection">The connection the transaction is on.</param>
  /// <param name="transaction">The rewrite phase's transaction, still open.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Each table once, qualified.</returns>
  public static async Task<IReadOnlyList<string>> ReadRewrittenTablesAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    await using var command = new NpgsqlCommand(
      $"SELECT current_setting('{REWRITTEN_TABLES_SETTING}', true)", connection, transaction);
    var marked = await command.ExecuteScalarAsync(cancellationToken) as string;
    // Distinct keeps each table's first mark, so the order is the order the rewrites marked them in.
    return [.. (marked ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)];
  }

  /// <summary>
  /// Analyzes the tables the schema pass queued as it created indexes on them, then forgets them.
  /// </summary>
  /// <remarks>
  /// The queue is taken with one <c>DELETE … RETURNING</c>, so two instances draining it at once analyze
  /// each table once between them. A table whose <c>ANALYZE</c> then fails is left to the maintenance
  /// step, which finds it by its missing statistics.
  /// </remarks>
  /// <param name="connection">An open connection, not in a transaction.</param>
  /// <param name="quotedSchema">The schema the queue lives in, quoted as an identifier.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The tables analyzed.</returns>
  public static async Task<IReadOnlyList<string>> AnalyzeQueuedAsync(
      NpgsqlConnection connection, string quotedSchema, ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentException.ThrowIfNullOrWhiteSpace(quotedSchema);
    var queued = await _readTablesAsync(
      connection,
      $"DELETE FROM {quotedSchema}.wh_index_statistics_pending RETURNING table_name",
      limit: null,
      cancellationToken);
    return await AnalyzeAsync(connection, queued, logger, cancellationToken);
  }

  /// <summary>
  /// Analyzes at most <paramref name="limit"/> tables whose index statistics are missing: those still
  /// queued, then perspective tables whose expression indexes have no statistics.
  /// </summary>
  /// <param name="connection">An open connection, not in a transaction.</param>
  /// <param name="quotedSchema">The schema to look in, quoted as an identifier.</param>
  /// <param name="limit">The most tables to analyze in this call.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The tables analyzed.</returns>
  public static async Task<IReadOnlyList<string>> AnalyzeMissingAsync(
      NpgsqlConnection connection, string quotedSchema, int limit, ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentException.ThrowIfNullOrWhiteSpace(quotedSchema);
    var candidates = await _readTablesAsync(
      connection,
      $"SELECT table_name FROM {quotedSchema}.wh_tables_needing_index_statistics($1)",
      limit,
      cancellationToken);
    var analyzed = await AnalyzeAsync(connection, candidates, logger, cancellationToken);
    if (analyzed.Count > 0) {
      await using var forget = new NpgsqlCommand(
        $"DELETE FROM {quotedSchema}.wh_index_statistics_pending WHERE table_name = ANY($1)", connection);
      string[] names = [.. analyzed];
      forget.Parameters.AddWithValue(names);
      await forget.ExecuteNonQueryAsync(cancellationToken);
    }
    return analyzed;
  }

  /// <summary>
  /// Runs <c>ANALYZE</c> on each table, one statement each, reporting a failure and moving on.
  /// </summary>
  /// <param name="connection">An open connection, not in a transaction.</param>
  /// <param name="tables">Qualified table names.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The tables analyzed.</returns>
  public static async Task<IReadOnlyList<string>> AnalyzeAsync(
      NpgsqlConnection connection, IEnumerable<string> tables, ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(tables);
    var log = logger ?? NullLogger.Instance;
    var analyzed = new List<string>();
    foreach (var table in tables) {
      try {
        // The name is resolved by the server before it is used, so only an existing table is ever named
        // in the statement, in the server's own quoting.
        await using var resolve = new NpgsqlCommand("SELECT to_regclass($1)::text", connection);
        resolve.Parameters.AddWithValue(table);
        if (await resolve.ExecuteScalarAsync(cancellationToken) is not string resolved) {
          continue;
        }
        await using var analyze = new NpgsqlCommand("ANALYZE " + resolved, connection);
        await analyze.ExecuteNonQueryAsync(cancellationToken);
        analyzed.Add(table);
        Analyzed(log, table);
      } catch (PostgresException ex) {
        AnalyzeFailed(log, ex, table);
      }
    }
    return analyzed;
  }

  private static async Task<List<string>> _readTablesAsync(
      NpgsqlConnection connection, string sql, int? limit, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(sql, connection);
    if (limit is int bound) {
      command.Parameters.AddWithValue(bound);
    }
    var tables = new List<string>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      tables.Add(reader.GetString(0));
    }
    return tables;
  }

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Analyzed {Table} so the planner has statistics for its indexes")]
  private static partial void Analyzed(ILogger logger, string table);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Could not analyze {Table}; the index-statistics maintenance step tries again")]
  private static partial void AnalyzeFailed(ILogger logger, Exception exception, string table);
}
