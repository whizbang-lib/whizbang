// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Real bloat on a framework table (<c>wh_settings</c>), for the tests that prove a rewrite reclaims space.
/// </summary>
internal static class TableBloat {
  /// <summary>Real bloat: fill a framework table, delete most of it, keep it over the candidate
  /// floor — the heap stays sized for the dead rows until something rewrites it.</summary>
  public static async Task ManufactureAsync(string connectionString, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync(ct);
    // Issue #671: the carve deletes the heap TAIL, and plain autovacuum can truncate that tail
    // between the request-time measure and the winner's re-measure, collapsing the before-ratio so
    // even a perfect VACUUM FULL cannot beat it and the step reports "ineffective". The product is
    // right to say so; the test must own the heap it measures, so autovacuum is switched off for
    // this table for the test's lifetime (the database is per test).
    await using (var pin = conn.CreateCommand()) {
      pin.CommandText = "ALTER TABLE wh_settings SET (autovacuum_enabled = false)";
      await pin.ExecuteNonQueryAsync(ct);
    }
    await using (var fill = conn.CreateCommand()) {
      fill.CommandText = @"
        INSERT INTO wh_settings (setting_key, setting_value, value_type)
        SELECT 'bloat_' || g, repeat('x', 200), 'string' FROM generate_series(1, 20000) g";
      await fill.ExecuteNonQueryAsync(ct);
    }
    await using (var carve = conn.CreateCommand()) {
      carve.CommandText = @"
        DELETE FROM wh_settings
        WHERE setting_key LIKE 'bloat\_%' AND substring(setting_key FROM 7)::INT > 1500";
      await carve.ExecuteNonQueryAsync(ct);
    }
    await _waitUntilTheDeletedRowsAreRemovableAsync(conn, ct);
    await using var analyze = conn.CreateCommand();
    analyze.CommandText = "ANALYZE wh_settings";
    await analyze.ExecuteNonQueryAsync(ct);
  }

  /// <summary>
  /// Blocks until PostgreSQL will actually let the carved rows go.
  /// </summary>
  /// <remarks>
  /// <para>Deleting rows does not make them removable. A dead tuple survives until no snapshot
  /// anywhere in the cluster could still see it, and that horizon is held by the oldest running
  /// transaction on the whole server -- including transactions in OTHER databases. Until it
  /// advances past this DELETE, <c>VACUUM FULL</c> copies the dead rows into the new heap and the
  /// table comes out exactly the size it went in.</para>
  ///
  /// <para>That is what made this test flaky on a shared container: with another suite running
  /// against its own database on the same server, the rewrite reclaimed nothing and the step
  /// correctly reported it as ineffective. PostgreSQL says as much when asked --
  /// <c>VACUUM (FULL, VERBOSE)</c> reported "0 removable, 20022 nonremovable row versions" -- and a
  /// second rewrite did no better, because the horizon, not the rewrite, was the problem. Nothing
  /// was wrong with the product: a rewrite that cannot reclaim SHOULD stay queued for the next
  /// boot, which is exactly what it did.</para>
  ///
  /// <para>The snapshot's own xmax after the DELETE is a transaction id no older than it, so once
  /// the cluster's oldest running transaction reaches that mark, every transaction that could
  /// still have seen those rows has finished and they are removable by definition. This polls
  /// because a cleanup horizon is external state with nothing to subscribe to; it is a wait on a
  /// real condition rather than a sleep chosen by feel.</para>
  /// </remarks>
  private static async Task _waitUntilTheDeletedRowsAreRemovableAsync(
      NpgsqlConnection conn, CancellationToken ct) {
    long deletedThrough;
    await using (var mark = conn.CreateCommand()) {
      mark.CommandText = "SELECT pg_snapshot_xmax(pg_current_snapshot())::TEXT::BIGINT";
      deletedThrough = (long)(await mark.ExecuteScalarAsync(ct))!;
    }

    var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
    while (true) {
      long horizon;
      await using (var probe = conn.CreateCommand()) {
        probe.CommandText = "SELECT pg_snapshot_xmin(pg_current_snapshot())::TEXT::BIGINT";
        horizon = (long)(await probe.ExecuteScalarAsync(ct))!;
      }
      if (horizon >= deletedThrough) {
        return;
      }
      if (DateTimeOffset.UtcNow > deadline) {
        throw new InvalidOperationException(
          $"The cluster's cleanup horizon never advanced past the carve (xmin {horizon} still "
          + $"below {deletedThrough}) after 60s, so VACUUM FULL cannot reclaim the deleted rows "
          + "and this test cannot demonstrate a rewrite. A long-running transaction on this "
          + "server -- in any database -- is holding the horizon open.");
      }
      await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
    }
  }
}
