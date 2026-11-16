// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2351: the change log behind the held-without-sequence fold stays bounded when nobody asks for an answer. An answer
/// folds only its own origin's changes, so an origin no consumer audits would grow the log for good; the maintenance
/// cycle's compaction folds every origin's oldest changes into the totals and deletes them, a bounded batch at a time,
/// and leaves the totals exactly what a rebuild from the rows computes.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/452_HeldFoldIsMaintainedAsRowsChange.sql</code-under-test>
/// <docs>resilience/stream-integrity#the-change-log-stays-bounded</docs>
[Category("Integration")]
[Category("Shard2")]
public class UnsequencedDigestCompactionSqlTests : EFCoreTestBase {
  private const string TYPE = "Contracts.HeldProbe, Contracts";
  private const int BATCH = 1_000;

  [Test]
  public async Task Compaction_FoldsEveryOriginsChanges_InBoundedBatches_AndLeavesWhatARebuildComputesAsync() {
    await using var conn = await _openAsync();
    var origins = new[] { TrackedGuid.New().Value, TrackedGuid.New().Value, TrackedGuid.New().Value };
    foreach (var origin in origins) {
      await _seedHeldAsync(conn, origin, 1_100);
    }
    await _deleteSomeAsync(conn, origins[0], 150);
    var pending = await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_unsequenced_digest_changes");

    var folds = new List<long>();
    long folded;
    do {
      folded = await _scalarAsync(conn, $"SELECT wh_compact_unsequenced_digest_changes({BATCH})");
      folds.Add(folded);
    } while (folded > 0);

    await Assert.That(folds.Max()).IsLessThanOrEqualTo(BATCH).Because("each run folds at most one batch");
    await Assert.That(folds.Sum()).IsEqualTo(pending).Because("every pending change of every origin is folded once");
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_unsequenced_digest_changes")).IsEqualTo(0L);
    var compacted = await _totalsAsync(conn);
    _ = await _scalarAsync(conn, "SELECT wh_rebuild_unsequenced_digests()");
    await Assert.That(compacted).IsEquivalentTo(await _totalsAsync(conn))
      .Because("the compacted totals are exactly what a rebuild from the rows computes");
  }

  [Test]
  public async Task UnderSteadyWritesWithNoAnswerAsked_TheLogNeverHoldsMoreThanOneCyclesChangesAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var sizes = new List<long>();
    for (var cycle = 0; cycle < 6; cycle++) {
      await _seedHeldAsync(conn, origin, 400);
      sizes.Add(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_unsequenced_digest_changes WHERE origin_service_id = @o", origin));
      _ = await _scalarAsync(conn, $"SELECT wh_compact_unsequenced_digest_changes({BATCH})");
    }

    await Assert.That(sizes.Distinct().ToList()).IsEquivalentTo(new List<long> { 400 })
      .Because("a cycle's compaction takes what the cycle wrote, so the log never accumulates across cycles");
    await Assert.That(await _scalarAsync(conn, "SELECT SUM(event_count) FROM wh_unsequenced_digest_totals WHERE origin_service_id = @o", origin))
      .IsEqualTo(2_400L);
  }

  [Test]
  public async Task Compaction_OfAnEmptyLog_FoldsNothingAsync() {
    await using var conn = await _openAsync();
    _ = await _scalarAsync(conn, $"SELECT wh_compact_unsequenced_digest_changes({BATCH})");

    await Assert.That(await _scalarAsync(conn, $"SELECT wh_compact_unsequenced_digest_changes({BATCH})")).IsEqualTo(0L);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static Task _seedHeldAsync(NpgsqlConnection conn, Guid origin, int rows) => _execAsync(conn, $"""
    INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope,
                                commit_sequence, created_at, origin_service_id)
    SELECT gen_random_uuid(), x.s, x.s, 'TestAggregate', 1, '{TYPE}',
           jsonb_build_object('t', CASE WHEN x.g % 2 = 0 THEN 'tenant-b' ELSE 'tenant-a' END),
           nextval('wh_commit_seq'), NOW() - INTERVAL '2 hours', '{origin}'::uuid
    FROM (SELECT g, gen_random_uuid() AS s FROM generate_series(1, {rows}) AS g) x
    """);

  private static Task _deleteSomeAsync(NpgsqlConnection conn, Guid origin, int rows) => _execAsync(conn, $"""
    DELETE FROM wh_event_store WHERE event_id IN (
      SELECT event_id FROM wh_event_store WHERE origin_service_id = '{origin}'::uuid ORDER BY event_id LIMIT {rows})
    """);

  private static async Task<List<string>> _totalsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT origin_service_id::text || '|' || scope_tenant || '|' || event_type || '|' || digest_lo || '|' || digest_hi
             || '|' || event_count
      FROM wh_unsequenced_digest_totals
      """;
    var rows = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetString(0));
    }
    return rows;
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, Guid? origin = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    if (origin is { } o) {
      cmd.Parameters.AddWithValue("o", o);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }
}
