// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>One executed scan of the event store: what it was, the index conditions that found its rows, and the rows
/// it fetched (returned plus removed by its filter or a lossy recheck).</summary>
internal sealed record EventStoreScan(string Node, string IndexConditions, long RowsFetched);

/// <summary>
/// Captures the executed plan of every statement a piece of code runs, nested statements inside functions included,
/// through auto_explain on each connection it opens, under generic plans (what a production caller's prepared statements
/// settle into), and reads every scan of <c>wh_event_store</c> out of them. Used by the tests that hold a type-scoped
/// history walk to its type's own events (#2205, #2206).
/// </summary>
internal sealed class EventStorePlanCapture {
  private readonly List<string> _plans = [];

  /// <summary>A database connection whose every opened connection logs its plans into this capture.</summary>
  public PostgresArchiveConnection Connection(string connectionString) =>
    new(async ct => {
      var connection = new NpgsqlConnection(connectionString);
      await connection.OpenAsync(ct);
      await using (var setup = connection.CreateCommand()) {
        setup.CommandText = """
          LOAD 'auto_explain';
          SET auto_explain.log_min_duration = 0;
          SET auto_explain.log_analyze = on;
          SET auto_explain.log_timing = off;
          SET auto_explain.log_nested_statements = on;
          SET auto_explain.log_format = 'json';
          SET auto_explain.log_level = 'notice';
          SET plan_cache_mode = force_generic_plan;
          """;
        await setup.ExecuteNonQueryAsync(ct);
      }
      connection.Notice += (_, e) => {
        if (e.Notice.MessageText.Contains("\"Plan\"", StringComparison.Ordinal)) {
          lock (_plans) {
            _plans.Add(e.Notice.MessageText);
          }
        }
      };
      return connection;
    }, "public");

  /// <summary>Every executed scan of the event store in the captured plans.</summary>
  public List<EventStoreScan> Scans() {
    var scans = new List<EventStoreScan>();
    lock (_plans) {
      foreach (var plan in _plans) {
        using var document = JsonDocument.Parse(plan[plan.IndexOf('{', StringComparison.Ordinal)..]);
        _collectScans(document.RootElement.GetProperty("Plan"), scans);
      }
    }
    return scans;
  }

  /// <summary>Builds the type index the walk reads (it is built after the migrations, so a test database lacks it).</summary>
  public static async Task BuildTypeIndexAsync(NpgsqlConnection conn) {
    var outcome = await ConcurrentIndexBuild.EnsureAsync(
      conn, "public", ConcurrentIndexBuild.FrameworkIndexes.Single(i => i.Name == TYPE_INDEX), 120);
    if (outcome != ConcurrentIndexOutcome.Built) {
      throw new InvalidOperationException($"the type index was not built: {outcome}");
    }
    await using var analyze = conn.CreateCommand();
    analyze.CommandText = "ANALYZE wh_event_store";
    await analyze.ExecuteNonQueryAsync();
  }

  /// <summary>The index a type's walk reads its window through.</summary>
  public const string TYPE_INDEX = "idx_event_store_type_commit_sequence";

  /// <summary>Events of eight other types, each in a stream of its own, numbered in commit order after every event
  /// stored so far.</summary>
  public static async Task SeedOtherTypesAsync(NpgsqlConnection conn, int rows) {
    await using var seed = conn.CreateCommand();
    seed.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, flags, created_at)
      SELECT x.id, x.id, x.id, 'Probe', normalize_event_type('Contracts.Other' || (g % 8) || ', Contracts'),
             1, nextval('wh_commit_seq'), 0, NOW()
      FROM generate_series(1, @rows) AS g, LATERAL (SELECT gen_random_uuid() AS id, g AS n) x;
      ANALYZE wh_event_store;
      """;
    seed.Parameters.AddWithValue(nameof(rows), rows);
    seed.CommandTimeout = 300;
    await seed.ExecuteNonQueryAsync();
  }

  private static void _collectScans(JsonElement node, List<EventStoreScan> scans) {
    var loops = node.TryGetProperty("Actual Loops", out var l) ? l.GetDouble() : 0;
    if (loops > 0 && node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "wh_event_store") {
      var conditions = new List<string>();
      _indexConditions(node, conditions);
      var fetched = (_number(node, "Actual Rows") + _number(node, "Rows Removed by Filter")
        + _number(node, "Rows Removed by Index Recheck")) * loops;
      scans.Add(new EventStoreScan(
        $"{node.GetProperty("Node Type").GetString()} {(node.TryGetProperty("Index Name", out var i) ? i.GetString() : string.Empty)}",
        string.Join(" | ", conditions),
        (long)fetched));
    }
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        _collectScans(child, scans);
      }
    }
  }

  private static void _indexConditions(JsonElement node, List<string> conditions) {
    if (node.TryGetProperty("Index Cond", out var condition)) {
      conditions.Add(condition.GetString()!);
    }
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        _indexConditions(child, conditions);
      }
    }
  }

  private static double _number(JsonElement node, string name) =>
    node.TryGetProperty(name, out var value) ? value.GetDouble() : 0;
}
