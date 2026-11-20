// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// The rows a statement read, counted by the backend that ran it: index entries returned plus rows sequential scans
/// returned, summed over the relations of the connection's own schema, read before and after the statement inside one
/// transaction (#2462).
/// </summary>
/// <remarks>
/// <para>
/// A count that a ceiling or a comparison can rest on. It is the same on every machine and under any load, because it
/// counts what this backend's executor did with the test's own tables and nothing else: no catalog read (a cache that
/// another session invalidated is reloaded from the catalogs, which are not in the schema), no index metapage reread,
/// no page that happened to be cached. Shared buffers count all of those, so two measurements of one statement moved
/// with whatever else ran on the server.
/// </para>
/// <para>
/// The counters are this backend's pending statistics, which span transactions until the backend reports them, so
/// only the difference across the statement means anything; both reads are taken inside the statement's transaction.
/// What the count cannot exclude is the state of the tables themselves: an index scan returns the entries of rows an
/// earlier rolled-back statement left dead, so a caller comparing two measurements vacuums before each.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/PartitionAssignmentClaimCostScenarioTests.cs:TheClaimCost_IsNotMovedByAnotherSessionsWork_Async</tests>
internal static class RowsRead {
  private const string SNAPSHOT = """
    SELECT c.relname::text, pg_stat_get_xact_tuples_returned(c.oid)
    FROM pg_class c
    WHERE c.relnamespace = current_schema()::regnamespace
      AND c.relkind IN ('r', 'i', 'p')
    """;

  /// <summary>
  /// Runs <paramref name="statement"/> in a transaction of its own on <paramref name="connection"/> and returns the rows
  /// it read; the transaction is committed when <paramref name="commit"/> is set and rolled back otherwise.
  /// </summary>
  public static async Task<RowsReadResult> MeasureAsync(
      NpgsqlConnection connection, Func<Task> statement, bool commit, CancellationToken cancellationToken) {
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
    var before = await _snapshotAsync(connection, cancellationToken);
    await statement();
    var after = await _snapshotAsync(connection, cancellationToken);
    if (commit) {
      await transaction.CommitAsync(cancellationToken);
    } else {
      await transaction.RollbackAsync(cancellationToken);
    }
    var byRelation = after
      .Select(entry => (Relation: entry.Key, Rows: entry.Value - before.GetValueOrDefault(entry.Key)))
      .Where(entry => entry.Rows != 0)
      .OrderByDescending(entry => entry.Rows)
      .ThenBy(entry => entry.Relation, StringComparer.Ordinal)
      .ToList();
    return new RowsReadResult(byRelation.Sum(entry => entry.Rows), byRelation);
  }

  private static async Task<Dictionary<string, long>> _snapshotAsync(
      NpgsqlConnection connection, CancellationToken cancellationToken) {
    var counts = new Dictionary<string, long>(StringComparer.Ordinal);
    await using var command = connection.CreateCommand();
    command.CommandText = SNAPSHOT;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      counts[reader.GetString(0)] = reader.GetInt64(1);
    }
    return counts;
  }
}

/// <summary>The rows a statement read, in total and per relation (largest first), as <see cref="RowsRead"/> counts them.</summary>
/// <param name="Total">Index entries and sequentially scanned rows the statement read across the schema.</param>
/// <param name="ByRelation">The relations it read, with the rows read from each.</param>
internal sealed record RowsReadResult(long Total, IReadOnlyList<(string Relation, long Rows)> ByRelation) {
  /// <summary>The total and the relations it came from, for a failure message.</summary>
  public override string ToString() =>
    $"{Total} rows read ({string.Join(", ", ByRelation.Select(entry => $"{entry.Relation} {entry.Rows}"))})";
}
