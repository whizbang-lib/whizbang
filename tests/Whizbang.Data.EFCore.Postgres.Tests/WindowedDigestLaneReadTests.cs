// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A windowed digest reads the rows of its window, not the whole lane (#2080). The lane's sequence was a CASE expression
/// (<c>commit_sequence</c> for the local lane, <c>origin_commit_sequence</c> for an origin's) and the window filtered it
/// afterwards; no index can serve a range on a CASE, so every call read the whole lane, and the cost grew with the store
/// rather than the window. Each test seeds a store of 40,000 events (20,000 local, 20,000 from one origin), asks for a
/// 100-event window, and counts the event-store rows the statement read in its own transaction
/// (<c>pg_stat_xact_user_tables</c>): a statement that reads the window reads about 100, one that reads the lane reads
/// 20,000 or more.
/// <para>
/// Each statement runs under <c>plan_cache_mode = force_generic_plan</c>, the plan a production caller gets: a function's
/// queries and an auto-prepared statement switch to the generic plan after a few calls. A custom plan sees the lane
/// parameter's value and folds the CASE down to one column, so it reads only the window even on the old shape, and a
/// test that ran custom plans passed against the defect.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/332_DigestWindowsReadOnlyTheirWindow.sql</code-under-test>
/// <docs>resilience/stream-integrity#windowed-manifests</docs>
[Category("Integration")]
[Category("Shard5")]
public class WindowedDigestLaneReadTests : EFCoreTestBase {
  private const int LANE_ROWS = 20_000;
  private const long SINCE = 5_000;
  private const long UNTIL = 5_100;
  private const long MOST_ROWS_A_WINDOW_READ_MAY_TOUCH = 2_000;

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task ManifestStreamWindow_ReadsTheWindow_NotTheLaneAsync(bool originLane) {
    var origin = await _seedAsync();

    var read = await _rowsReadAsync(cmd => {
      cmd.CommandText = EFCoreWorkCoordinator<WorkCoordinationDbContext>.WindowedStreamDigestSql("public");
      EFCoreWorkCoordinator<WorkCoordinationDbContext>.AddDigestFilterParams(cmd, originLane ? origin : null, null);
      cmd.Parameters.AddWithValue("p_since", SINCE);
      cmd.Parameters.AddWithValue("p_until", UNTIL);
      cmd.Parameters.Add(new NpgsqlParameter("p_resume", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = DBNull.Value });
      cmd.Parameters.AddWithValue("p_limit", 501);
      cmd.Parameters.AddWithValue("p_settle", 0);
    });

    await Assert.That(read).IsLessThan(MOST_ROWS_A_WINDOW_READ_MAY_TOUCH)
      .Because($"a {UNTIL - SINCE}-event window must not read the {LANE_ROWS}-event lane it sits in");
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task TypeDigestWindow_ReadsTheWindow_NotTheLaneAsync(bool originLane) {
    var origin = await _seedAsync();

    var read = await _rowsReadAsync(cmd => {
      cmd.CommandText = "SELECT count(*) FROM compute_type_digests_epoch_window(@p_origin, NULL, @p_since, @p_until, NOW(), 0)";
      cmd.Parameters.Add(new NpgsqlParameter("p_origin", NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = originLane ? origin : DBNull.Value,
      });
      cmd.Parameters.AddWithValue("p_since", SINCE);
      cmd.Parameters.AddWithValue("p_until", UNTIL);
    });

    await Assert.That(read).IsLessThan(MOST_ROWS_A_WINDOW_READ_MAY_TOUCH)
      .Because("the type-level window folds live through the same lane, so it reads only its window too");
  }

  /// <summary>Seeds 20,000 local events and 20,000 received from one origin, then analyzes the table. Returns the origin.</summary>
  private async Task<Guid> _seedAsync() {
    var origin = TrackedGuid.New().Value;
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var seed = connection.CreateCommand();
    seed.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, flags, created_at,
         origin_service_id, origin_commit_sequence)
      SELECT x.id, x.id, x.id, 'Probe', normalize_event_type('Contracts.Probe, Contracts'), 1, g, 0, NOW() - interval '1 day',
             NULL, NULL
      FROM generate_series(1, @rows) AS g, LATERAL (SELECT gen_random_uuid() AS id, g AS n) x;
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, flags, created_at,
         origin_service_id, origin_commit_sequence)
      SELECT x.id, x.id, x.id, 'Probe', normalize_event_type('Contracts.Probe, Contracts'), 1, @rows + g, 0,
             NOW() - interval '1 day', @origin, g
      FROM generate_series(1, @rows) AS g, LATERAL (SELECT gen_random_uuid() AS id, g AS n) x;
      ANALYZE wh_event_store;
      """;
    seed.Parameters.AddWithValue("rows", LANE_ROWS);
    seed.Parameters.AddWithValue("origin", origin);
    seed.CommandTimeout = 300;
    await seed.ExecuteNonQueryAsync();
    return origin;
  }

  /// <summary>
  /// Runs one statement in its own transaction, under the generic plan, and returns how many event-store rows it read:
  /// sequentially scanned plus fetched through an index, from the transaction's own statistics.
  /// </summary>
  private async Task<long> _rowsReadAsync(Action<NpgsqlCommand> prepare) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await using (var generic = connection.CreateCommand()) {
      generic.Transaction = transaction;
      generic.CommandText = "SET LOCAL plan_cache_mode = force_generic_plan";
      await generic.ExecuteNonQueryAsync();
    }
    await using (var statement = connection.CreateCommand()) {
      statement.Transaction = transaction;
      prepare(statement);
      await using var reader = await statement.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // drain
      }
    }
    await using var stats = connection.CreateCommand();
    stats.Transaction = transaction;
    stats.CommandText = """
      SELECT COALESCE(seq_tup_read, 0) + COALESCE(idx_tup_fetch, 0)
      FROM pg_stat_xact_user_tables WHERE relname = 'wh_event_store'
      """;
    var read = (long)(await stats.ExecuteScalarAsync() ?? 0L);
    await transaction.RollbackAsync();
    return read;
  }
}
