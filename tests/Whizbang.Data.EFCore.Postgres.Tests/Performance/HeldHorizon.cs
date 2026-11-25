// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// A snapshot a measurement holds in its own database so the horizon is where the test puts it (#2587).
/// </summary>
/// <remarks>
/// <para>
/// Whether a vacuum may remove a version some statement left dead, whether a scan may skip one, and whether a page is
/// marked all-visible (which the planner prices index-only scans by) are decided against the horizon, and any open
/// snapshot holds it back. On a server other test processes share, a count of rows read that rests on a vacuum having
/// removed those versions therefore followed whatever else was running: when they stayed, every index probe also
/// returned the dead entry.
/// </para>
/// <para>
/// Held from before a fill, the snapshot makes every committed version the fill or a later call writes unremovable, by
/// a vacuum or by a scan, on every run, and leaves no page of them all-visible. A version an aborted transaction wrote
/// is dead whatever the horizon, so a vacuum still removes what a rolled-back call left.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimReofferWalkCostSqlTests.cs:ReOfferMeasure_UnderAHorizonHeldElsewhereAndAnInvalidation_ReadsTheSameRowsAsync</tests>
internal static class HeldHorizon {
  /// <summary>
  /// Opens <paramref name="connection"/>, begins a repeatable-read transaction on it and takes its snapshot, which it
  /// holds until the connection closes.
  /// </summary>
  public static async Task HoldAsync(NpgsqlConnection connection, CancellationToken cancellationToken) {
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = "BEGIN ISOLATION LEVEL REPEATABLE READ; SELECT 1;";
    _ = await command.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <summary>Turns autovacuum off on <paramref name="tables"/>, so no analyze rewrites a measurement's statistics.</summary>
  public static async Task DisableAutovacuumAsync(
      NpgsqlConnection connection, IEnumerable<string> tables, CancellationToken cancellationToken) {
    foreach (var table in tables) {
      await using var command = connection.CreateCommand();
      command.CommandText = $"ALTER TABLE {table} SET (autovacuum_enabled = false)";
      _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }
  }
}
