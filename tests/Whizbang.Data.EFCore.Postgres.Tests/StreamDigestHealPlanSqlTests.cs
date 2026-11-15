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
/// #2337: the statement that heals a batch of stream digests (<c>_wh_heal_stream_digests</c>) must cost time in
/// proportion to the batch, whatever the planner believes about it. The planner cannot estimate the event filters
/// (flag bits, exempt types), so it believes the recomputed set is a single row. Any nested loop that rescans a
/// materialized set on its inner side is then quadratic in practice: a 500-stream batch of about 12 thousand buckets
/// took longer than the 30 second command timeout on every attempt, and the id-lane backfill never moved past it.
/// The statement is therefore written so that no plan can rescan a set per row: the stored and recomputed buckets
/// are compared with one FULL JOIN (which PostgreSQL runs only as a hash or merge join), and the update count is read
/// once, not per row.
/// </summary>
/// <docs>resilience/stream-integrity#the-heal-is-linear-in-its-batch</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/446_StreamDigestHealIsLinearInItsBatch.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StreamDigestHealPlanSqlTests : EFCoreTestBase {
  private const string TYPE = "Whizbang.Tests.HealPlanEvent, Whizbang.Tests";

  [Test]
  public async Task TheHealStatement_NeverRescansASetPerRow_ForANamedBatchAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var streams = await _streamsAsync(conn, 40);

    var violations = await _rescansAsync(conn, named: true, streams);

    await Assert.That(violations).IsEmpty()
      .Because("a nested loop that rescans a materialized set on its inner side is quadratic once the planner misjudges that set");
  }

  [Test]
  public async Task TheHealStatement_NeverRescansASetPerRow_ForTheWholeStoreAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    _ = await _streamsAsync(conn, 40);

    var violations = await _rescansAsync(conn, named: false, streams: null);

    await Assert.That(violations).IsEmpty()
      .Because("the full verification sweep runs the same statement over every stream");
  }

  [Test]
  public async Task TheHealFunction_RunsTheStatementItsHelperReturnsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var streams = await _streamsAsync(conn, 3);
    await using (var strip = conn.CreateCommand()) {
      strip.CommandText = "UPDATE wh_stream_digests SET id_lo = NULL, id_hi = NULL WHERE stream_id = ANY(@s)";
      strip.Parameters.AddWithValue("s", streams);
      await strip.ExecuteNonQueryAsync();
    }

    await using var heal = conn.CreateCommand();
    heal.CommandText = "SELECT kind, stream_id FROM _wh_heal_stream_digests(@s, INTERVAL '0 seconds', true) ORDER BY stream_id";
    heal.Parameters.AddWithValue("s", streams);
    var kinds = new List<(string Kind, Guid Stream)>();
    await using (var r = await heal.ExecuteReaderAsync()) {
      while (await r.ReadAsync()) {
        kinds.Add((r.GetString(0), r.GetGuid(1)));
      }
    }

    await Assert.That(kinds.Select(k => k.Kind).Distinct().ToArray()).IsEquivalentTo(["filled"]);
    await Assert.That(kinds.Select(k => k.Stream).ToArray()).IsEquivalentTo(streams.Order().ToArray());
    await using var unfilled = conn.CreateCommand();
    unfilled.CommandText = "SELECT count(*) FROM wh_stream_digests WHERE stream_id = ANY(@s) AND (id_lo IS NULL OR id_hi IS NULL)";
    unfilled.Parameters.AddWithValue("s", streams);
    await Assert.That((long)(await unfilled.ExecuteScalarAsync())!).IsEqualTo(0L);
  }

  // ===== Helpers =====

  /// <summary>Streams of two events each, written by the append path, which folds their digests.</summary>
  private static async Task<Guid[]> _streamsAsync(NpgsqlConnection conn, int count) {
    var streams = new Guid[count];
    for (var i = 0; i < count; i++) {
      streams[i] = TrackedGuid.New().Value;
      for (var v = 1; v <= 2; v++) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
          SELECT wh_append_event(@id, @s, @s, 'Agg.HealPlan', @type, '{}'::jsonb, '{}'::jsonb, NULL, NOW() - INTERVAL '2 hours')
          """;
        cmd.Parameters.AddWithValue("id", TrackedGuid.New().Value);
        cmd.Parameters.AddWithValue("s", streams[i]);
        cmd.Parameters.AddWithValue("type", TYPE);
        _ = await cmd.ExecuteScalarAsync();
      }
    }
    await using var analyze = conn.CreateCommand();
    analyze.CommandText = "ANALYZE wh_event_store; ANALYZE wh_stream_digests;";
    await analyze.ExecuteNonQueryAsync();
    return streams;
  }

  /// <summary>
  /// Plans the heal statement exactly as the function runs it (the text comes from its helper) and returns every
  /// nested loop whose inner side scans a materialized set, which the loop would rescan once per outer row.
  /// </summary>
  private static async Task<List<string>> _rescansAsync(NpgsqlConnection conn, bool named, Guid[]? streams) {
    await using (var prepare = conn.CreateCommand()) {
      prepare.CommandText = """
        DO $$ BEGIN
          EXECUTE format('PREPARE heal_plan_named(uuid[], interval, boolean) AS %s', _wh_heal_stream_digests_statement(true));
          EXECUTE format('PREPARE heal_plan_all(uuid[], interval, boolean) AS %s', _wh_heal_stream_digests_statement(false));
        END $$;
        """;
      await prepare.ExecuteNonQueryAsync();
    }
    var literal = streams is null ? "NULL::uuid[]" : $"ARRAY['{string.Join("','", streams)}']::uuid[]";
    await using var explain = conn.CreateCommand();
    explain.CommandText =
      $"EXPLAIN (FORMAT JSON) EXECUTE {(named ? "heal_plan_named" : "heal_plan_all")}({literal}, INTERVAL '0 seconds', true)";
    var json = (string)(await explain.ExecuteScalarAsync())!;
    using var document = JsonDocument.Parse(json);
    var violations = new List<string>();
    _walk(document.RootElement[0].GetProperty("Plan"), violations);
    return violations;
  }

  private static void _walk(JsonElement node, List<string> violations) {
    if (!node.TryGetProperty("Plans", out var children)) {
      return;
    }
    var isNestedLoop = node.GetProperty("Node Type").GetString() == "Nested Loop";
    foreach (var child in children.EnumerateArray()) {
      if (isNestedLoop && child.GetProperty("Parent Relationship").GetString() == "Inner") {
        var scanned = new List<string>();
        _cteScans(child, scanned);
        violations.AddRange(scanned.Select(cte => $"a nested loop rescans CTE {cte} per row"));
      }
      _walk(child, violations);
    }
  }

  /// <summary>The CTE scans in this subtree, not counting subplans and init plans, which run on their own.</summary>
  private static void _cteScans(JsonElement node, List<string> found) {
    if (node.GetProperty("Node Type").GetString() == "CTE Scan") {
      found.Add(node.GetProperty("CTE Name").GetString()!);
    }
    if (!node.TryGetProperty("Plans", out var children)) {
      return;
    }
    foreach (var child in children.EnumerateArray()) {
      var relationship = child.GetProperty("Parent Relationship").GetString();
      if (relationship is "InitPlan" or "SubPlan") {
        continue;
      }
      _cteScans(child, found);
    }
  }
}
