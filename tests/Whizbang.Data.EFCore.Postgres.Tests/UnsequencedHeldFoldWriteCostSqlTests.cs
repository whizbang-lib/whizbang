// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2351: what the held-without-sequence row triggers add to the write path, per inserted event. The same insert runs
/// with the triggers and without. An event stored with its origin's sequence, or with no origin, is filtered out by the
/// trigger's WHEN clause and must write nothing more; a held event writes exactly one change row. The WAL and the
/// execution time each adds are measured and reported.
/// </summary>
/// <remarks>
/// The rows written are exact and asserted. WAL is read from the server's insert position, which every session on a
/// shared server moves, and time depends on the server's load, so both are reported, not asserted; on a quiet server
/// they are the triggers' cost.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/452_HeldFoldIsMaintainedAsRowsChange.sql</code-under-test>
/// <docs>resilience/stream-integrity#what-the-triggers-cost-a-write</docs>
[Category("Integration")]
[Category("Shard2")]
public class UnsequencedHeldFoldWriteCostSqlTests : EFCoreTestBase {
  private const int EVENTS = 2_000;

  [Test]
  public async Task AnOrdinaryEvent_WritesNothingMore_AndAHeldEvent_WritesOneChangeRowAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var ordinary = await _extraPerEventAsync(conn, held: false);
    var held = await _extraPerEventAsync(conn, held: true);
    Console.WriteLine(
      $"#2351 write path, per event: ordinary +{ordinary.ChangeRows:F2} change rows, +{ordinary.WalBytes:F0} WAL bytes, "
      + $"+{ordinary.Milliseconds * 1000:F1} us; held +{held.ChangeRows:F2} change rows, +{held.WalBytes:F0} WAL bytes, "
      + $"+{held.Milliseconds * 1000:F1} us");

    await Assert.That(ordinary.ChangeRows).IsEqualTo(0d)
      .Because("the trigger's WHEN clause filters an event that is not held, so it writes nothing");
    await Assert.That(held.ChangeRows).IsEqualTo(1d).Because("a held event writes exactly one change row");
  }

  /// <summary>What one event costs with the triggers, over the same insert without them.</summary>
  private static async Task<Cost> _extraPerEventAsync(NpgsqlConnection conn, bool held) {
    var withTriggers = await _insertAsync(conn, held);
    await _execAsync(conn, "ALTER TABLE wh_event_store DISABLE TRIGGER USER");
    Cost without;
    try {
      without = await _insertAsync(conn, held);
    } finally {
      await _execAsync(conn, "ALTER TABLE wh_event_store ENABLE TRIGGER USER");
    }
    return new Cost(
      (withTriggers.ChangeRows - without.ChangeRows) / EVENTS,
      (withTriggers.WalBytes - without.WalBytes) / EVENTS,
      (withTriggers.Milliseconds - without.Milliseconds) / EVENTS);
  }

  /// <summary>Inserts the events (held: from an origin, without its sequence; otherwise with it) and measures it.</summary>
  private static async Task<Cost> _insertAsync(NpgsqlConnection conn, bool held) {
    var origin = TrackedGuid.New().Value;
    var changesBefore = await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_unsequenced_digest_changes");
    var walBefore = await _scalarAsync(conn, "SELECT pg_current_wal_insert_lsn() - '0/0'::pg_lsn");
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      EXPLAIN (ANALYZE, FORMAT JSON)
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope,
                                  commit_sequence, created_at, origin_service_id, origin_commit_sequence)
      SELECT gen_random_uuid(), x.s, x.s, 'TestAggregate', 1, 'Contracts.HeldProbe, Contracts',
             jsonb_build_object('t', 'tenant-a'), nextval('wh_commit_seq'), NOW(), '{origin}'::uuid,
             {(held ? "NULL" : "x.g")}
      FROM (SELECT g, gen_random_uuid() AS s FROM generate_series(1, {EVENTS}) AS g) x
      """;
    using var plan = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync())!);
    var walAfter = await _scalarAsync(conn, "SELECT pg_current_wal_insert_lsn() - '0/0'::pg_lsn");
    var changesAfter = await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_unsequenced_digest_changes");
    return new Cost(changesAfter - changesBefore, walAfter - walBefore,
      plan.RootElement[0].GetProperty("Execution Time").GetDouble());
  }

  private static async Task<double> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToDouble(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private sealed record Cost(double ChangeRows, double WalBytes, double Milliseconds);
}
