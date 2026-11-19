// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A type-filtered windowed stream-digest read costs in proportion to the rows it returns (#2107). After #2080 the read
/// was bound by its window, but it found the window's rows through <c>idx_event_store_commit_sequence</c> and filtered
/// the requested types on the heap, so a wide window read every event in it, of any type, to return a few hundred.
/// <para>
/// Each test seeds a lane of 40,000 events of which 400 (one in a hundred) are of the requested type, asks for a window
/// that covers the whole lane, and reads the plan of every statement the read ran (auto_explain, nested statements,
/// actual rows and buffers). The read must find its rows through an index condition on the type, never a heap filter
/// on it, and must fetch at most a few times the rows it returns rather than the window's 40,000.
/// </para>
/// <para>
/// The caller runs under <c>plan_cache_mode = force_generic_plan</c>, the plan a production caller gets once its
/// auto-prepared statement goes generic. A generic plan cannot see how wide the window is or which types are asked for,
/// so it estimates the range as narrow and keeps the commit-sequence index; the read therefore plans each call against
/// its own window and types, and a test that ran custom plans would pass against the defect.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/347_DigestWindowsReadTheirTypes.sql</code-under-test>
/// <docs>resilience/stream-integrity#windowed-manifests</docs>
[Category("Integration")]
[Category("Shard5")]
public class WindowedDigestTypeIndexTests : EFCoreTestBase {
  private const int LANE_ROWS = 40_000;
  private const int WANTED_EVERY = 100;
  private const int WANTED_ROWS = LANE_ROWS / WANTED_EVERY;
  private const long MOST_ROWS_THE_READ_MAY_FETCH = 2_000;
  private const string WANTED = "Contracts.Wanted, Contracts";
  private const string TYPE_INDEX = "idx_event_store_type_commit_sequence";

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task TypeFilteredWindow_ReadsThroughAnIndexConditionOnTheType_UnderAGenericPlanAsync(bool originLane) {
    var origin = await _seedAsync();
    await _buildTypeIndexAsync();

    var (result, scans) = await _readAsync(originLane ? origin : null);

    await Assert.That(result.Digests.Sum(d => d.EventCount)).IsEqualTo(WANTED_ROWS)
      .Because("the read answers exactly the requested type's events in the window");
    await Assert.That(scans).IsNotEmpty().Because("the read scans the event store");
    foreach (var scan in scans) {
      await Assert.That(scan.IndexConditions).Contains("event_type", StringComparison.Ordinal)
        .Because($"the {scan.Node} must find the requested type through an index, not read every event in the window");
      await Assert.That(scan.Filter).DoesNotContain("event_type)::text = ", StringComparison.Ordinal)
        .Because("a heap filter on the type means every event in the window was fetched to find a few");
    }
    var fetched = scans.Sum(s => s.RowsFetched);
    await Assert.That(fetched).IsLessThan(MOST_ROWS_THE_READ_MAY_FETCH)
      .Because($"the read returns {WANTED_ROWS} events and must not fetch the window's {LANE_ROWS}");
  }

  [Test]
  public async Task BeforeTheTypeIndexIsBuilt_TheReadAnswersTheSameAsync() {
    var origin = await _seedAsync();

    var (withoutIndex, _) = await _readAsync(origin);
    await _buildTypeIndexAsync();
    var (withIndex, _) = await _readAsync(origin);

    await Assert.That(withoutIndex.Digests.Sum(d => d.EventCount)).IsEqualTo(WANTED_ROWS)
      .Because("the index is built after the migrations; until it is valid the read answers through another path");
    await Assert.That(withoutIndex.Digests.Select(d => (d.StreamId, d.DigestLo, d.DigestHi)).ToList())
      .IsEquivalentTo(withIndex.Digests.Select(d => (d.StreamId, d.DigestLo, d.DigestHi)).ToList())
      .Because("the plan changes, the answer does not");
  }

  [Test]
  public async Task TheRead_PlansEachCallAgainstItsOwnWindow_WhateverTheCallersPlanModeAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT array_to_string(p.proconfig, ',')
      FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
      WHERE n.nspname = 'public' AND p.proname = 'wh_compute_stream_digests_window'
      """;
    var config = (string?)await command.ExecuteScalarAsync();

    await Assert.That(config).IsNotNull().Because("the windowed stream-digest read is a function of its own");
    await Assert.That(config).Contains("plan_cache_mode=force_custom_plan", StringComparison.Ordinal)
      .Because("a redefinition that drops the setting lets the read go generic and read every event in the window again");
  }

  /// <summary>
  /// Seeds 40,000 local events and 40,000 received from one origin; one in a hundred of each lane is the requested type,
  /// each in its own stream, and the rest are spread over eight other types. Returns the origin.
  /// </summary>
  private async Task<Guid> _seedAsync() {
    var origin = TrackedGuid.New().Value;
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var seed = connection.CreateCommand();
    seed.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, flags, created_at,
         origin_service_id, origin_commit_sequence)
      SELECT x.id, x.id, x.id, 'Probe',
             CASE WHEN g % @every = 0 THEN normalize_event_type(@wanted)
                  ELSE normalize_event_type('Contracts.Other' || (g % 8) || ', Contracts') END,
             1, g, 0, NOW() - interval '1 day', NULL, NULL
      FROM generate_series(1, @rows) AS g, LATERAL (SELECT gen_random_uuid() AS id, g AS n) x;
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, flags, created_at,
         origin_service_id, origin_commit_sequence)
      SELECT x.id, x.id, x.id, 'Probe',
             CASE WHEN g % @every = 0 THEN normalize_event_type(@wanted)
                  ELSE normalize_event_type('Contracts.Other' || (g % 8) || ', Contracts') END,
             1, @rows + g, 0, NOW() - interval '1 day', @origin, g
      FROM generate_series(1, @rows) AS g, LATERAL (SELECT gen_random_uuid() AS id, g AS n) x;
      ANALYZE wh_event_store;
      """;
    seed.Parameters.AddWithValue("rows", LANE_ROWS);
    seed.Parameters.AddWithValue("every", WANTED_EVERY);
    seed.Parameters.AddWithValue("wanted", WANTED);
    seed.Parameters.AddWithValue("origin", origin);
    seed.CommandTimeout = 300;
    await seed.ExecuteNonQueryAsync();
    return origin;
  }

  private async Task _buildTypeIndexAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var outcome = await ConcurrentIndexBuild.EnsureAsync(
      connection, "public", ConcurrentIndexBuild.FrameworkIndexes.Single(i => i.Name == TYPE_INDEX), 120);
    if (outcome != ConcurrentIndexOutcome.Built) {
      throw new InvalidOperationException($"the type index was not built: {outcome}");
    }
    await using var analyze = connection.CreateCommand();
    analyze.CommandText = "ANALYZE wh_event_store";
    await analyze.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Asks for the requested type over the whole lane through the coordinator, on a connection whose statements run
  /// generic plans, and returns the answer with every event-store scan the digest fold ran.
  /// </summary>
  private async Task<(Whizbang.Core.Messaging.WindowedDigestResult Result, IReadOnlyList<EventStoreScan> Scans)> _readAsync(
      Guid? originLane) {
    await using var ctx = CreateDbContext();
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    await connection.OpenAsync();
    await _execAsync(connection, """
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = on;
      SET auto_explain.log_buffers = on;
      SET auto_explain.log_timing = off;
      SET auto_explain.log_nested_statements = on;
      SET auto_explain.log_format = 'json';
      SET auto_explain.log_level = 'notice';
      SET plan_cache_mode = force_generic_plan;
      """);
    var notices = new List<string>();
    void Collect(object? _, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    connection.Notice += Collect;
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var result = await coordinator.ComputeStreamDigestsWindowedAsync(
      originLane, [WANTED], sinceSequence: 0, untilSequence: null, resumeAfterStreamId: null,
      maxDigests: 1_000, TimeSpan.FromHours(1));
    connection.Notice -= Collect;

    var scans = new List<EventStoreScan>();
    foreach (var notice in notices.Where(n => n.Contains("\"Plan\"", StringComparison.Ordinal))) {
      using var document = JsonDocument.Parse(notice[notice.IndexOf('{', StringComparison.Ordinal)..]);
      var root = document.RootElement;
      // The digest fold's statements, not the settled-max probe that clamps the window first.
      if (!root.GetProperty("Query Text").GetString()!.Contains("_wh_digest_lane", StringComparison.Ordinal)) {
        continue;
      }
      _collectScans(root.GetProperty("Plan"), scans);
    }
    return (result!, scans);
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
        node.TryGetProperty("Filter", out var filter) ? filter.GetString()! : string.Empty,
        (long)fetched));
    }
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        _collectScans(child, scans);
      }
    }
  }

  /// <summary>The index conditions that find a scan's rows: its own, or those of the bitmap index scans beneath it.</summary>
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

  private static async Task _execAsync(NpgsqlConnection connection, string sql) {
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>One executed scan of the event store: what it was, the index conditions that found its rows, its heap
  /// filter, and the rows it fetched (returned plus removed by its filter or a lossy recheck).</summary>
  private sealed record EventStoreScan(string Node, string IndexConditions, string Filter, long RowsFetched);
}
