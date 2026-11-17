// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2351: the type-level fold of the events a store holds from an origin without the origin's commit sequence costs what
/// changed since the last answer, never the size of that history. The contribution is maintained as the rows change
/// (migration 452), so an answer reads one row per (tenant, type), the changes not yet folded in, and the rows still
/// inside the settle window. These tests hold the cost (a plan shape and a small-store against large-store comparison)
/// and the result: whatever changes the sequence-less set, the maintained answer equals a fold of the rows.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#the-held-fold-is-maintained-as-rows-change</docs>
[Category("Integration")]
[Category("Shard2")]
public class UnsequencedHeldFoldCostSqlTests : EFCoreTestBase {
  private const string TYPE = "Contracts.HeldProbe, Contracts";
  private const string OTHER_TYPE = "Contracts.OtherProbe, Contracts";
  private const string EXEMPT_TYPE = "Whizbang.Core.SystemEvents.EventAudited, Whizbang.Core";

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  // ── cost: proportional to what changed, never to the history ──

  private const int SMALL_HISTORY = 1_000;
  private const int LARGE_HISTORY = 20_000;
  private const int NEW_ROWS = 25;
  private const int UNSETTLED_ROWS = 5;

  [Test]
  public async Task ByType_ALargeHistory_CostsWhatASmallOneDoes_ForTheSameNewWorkAsync() {
    var (smallRows, smallBlocks) = await _measureSteadyStateAsync(SMALL_HISTORY);
    var (largeRows, largeBlocks) = await _measureSteadyStateAsync(LARGE_HISTORY);

    Console.WriteLine($"#2351 held fold, rows read: small {smallRows}, large {largeRows}; "
      + $"shared blocks: small {smallBlocks}, large {largeBlocks}");
    await Assert.That(largeRows).IsLessThanOrEqualTo((smallRows * 2) + 50)
      .Because($"a {LARGE_HISTORY / SMALL_HISTORY}x larger sequence-less history with the same {NEW_ROWS} new rows must "
        + $"cost the same: read {smallRows} rows on the small store and {largeRows} on the large one");
    await Assert.That(largeBlocks).IsLessThanOrEqualTo((smallBlocks * 2) + 50)
      .Because($"buffer reads must not grow with history: {smallBlocks} blocks small, {largeBlocks} large");
  }

  [Test]
  public async Task ByType_UnderAGenericPlan_ReadsOnlyTheUnsettledRowsOfTheEventStore_NeverItsHistoryAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var origin = TrackedGuid.New().Value;
    await _seedHistoryRebuiltAsync(conn, origin, LARGE_HISTORY, sequencedToo: true);
    await _seedHistoryAsync(conn, origin, UNSETTLED_ROWS, sequencedToo: false, age: "1 minute");
    await _execAsync(conn, "ANALYZE wh_event_store");

    var nodes = await _plansOfAsync(conn, origin);

    var reads = nodes.Where(n => n.Relation == "wh_event_store").ToList();
    Console.WriteLine($"#2351 held fold plan: wh_event_store rows read {reads.Sum(n => n.RowsRead)}, "
      + $"all relations {nodes.Where(n => n.Relation is not null).Sum(n => n.RowsRead)}");
    await Assert.That(reads.Any(n => n.Type == "Seq Scan")).IsFalse();
    await Assert.That(reads.Sum(n => n.RowsRead)).IsLessThanOrEqualTo(UNSETTLED_ROWS + 2)
      .Because($"the event store is read only for the {UNSETTLED_ROWS} rows inside the settle window, never for the "
        + $"{LARGE_HISTORY} settled ones");
  }

  // ── result: the maintained answer equals a fold of the rows, whatever changes them ──

  [Test]
  public async Task EveryKindOfChange_LeavesTheAnswerEqualToAFoldOfTheRowsAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var other = TrackedGuid.New().Value;
    var ids = new List<Guid>();
    async Task<Guid> SeedAsync(Guid? o, string type, long? seq = null, int flags = 0, string tenant = "tenant-a",
        string age = "2 hours") {
      var id = TrackedGuid.New().Value;
      ids.Add(id);
      await _seedOneAsync(conn, o, id, type, seq, flags, tenant, age);
      return id;
    }

    var adopted = await SeedAsync(origin, TYPE);
    var flagged = await SeedAsync(origin, TYPE);
    var unflagged = await SeedAsync(origin, TYPE, flags: 8);
    var reclassified = await SeedAsync(origin, TYPE);
    var rescoped = await SeedAsync(origin, TYPE);
    var reattributed = await SeedAsync(null, TYPE);
    var moved = await SeedAsync(origin, TYPE);
    var deleted = await SeedAsync(origin, OTHER_TYPE, tenant: "tenant-b");
    _ = await SeedAsync(origin, TYPE);
    _ = await SeedAsync(origin, OTHER_TYPE, tenant: "tenant-b");
    _ = await SeedAsync(origin, EXEMPT_TYPE);
    _ = await SeedAsync(origin, TYPE, flags: (int)EventFlags.AtMostOnce);
    _ = await SeedAsync(origin, TYPE, seq: 9);
    _ = await SeedAsync(origin, TYPE, age: "1 minute");
    await _assertEqualsTheRowsAsync(conn, origin, "after the inserts");

    await _execAsync(conn, $"UPDATE wh_event_store SET origin_commit_sequence = 41 WHERE event_id = '{adopted}'");
    await _execAsync(conn, $"UPDATE wh_event_store SET flags = flags | {(int)EventFlags.AtMostOnce} WHERE event_id = '{flagged}'");
    await _execAsync(conn, $"UPDATE wh_event_store SET flags = 0 WHERE event_id = '{unflagged}'");
    await _assertEqualsTheRowsAsync(conn, origin, "after adoption and flag changes");

    await _execAsync(conn, $"UPDATE wh_event_store SET event_type = '{OTHER_TYPE}' WHERE event_id = '{reclassified}'");
    await _execAsync(conn, $"UPDATE wh_event_store SET scope = jsonb_build_object('t', 'tenant-c') WHERE event_id = '{rescoped}'");
    await _execAsync(conn, $"UPDATE wh_event_store SET origin_service_id = '{origin}' WHERE event_id = '{reattributed}'");
    await _execAsync(conn, $"UPDATE wh_event_store SET origin_service_id = '{other}' WHERE event_id = '{moved}'");
    await _execAsync(conn, $"DELETE FROM wh_event_store WHERE event_id = '{deleted}'");
    await _execAsync(conn, $"UPDATE wh_event_store SET created_at = NOW() - INTERVAL '3 hours' WHERE event_id = ANY('{{{string.Join(',', ids)}}}'::uuid[]) AND created_at > NOW() - INTERVAL '5 minutes'");
    await _assertEqualsTheRowsAsync(conn, origin, "after reclassify, rescope, re-attribution, a move, a delete and an age change");
    await _assertEqualsTheRowsAsync(conn, other, "the origin the moved event went to");
  }

  [Test]
  public async Task ABacklogLargerThanOneFold_IsFoldedOverSeveralAnswers_AndEveryAnswerIsExactAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _seedHistoryAsync(conn, origin, 25_000, sequencedToo: false);

    await _assertEqualsTheRowsAsync(conn, origin, "while most changes are still pending");
    await Assert.That(await _pendingAsync(conn, origin)).IsGreaterThan(0)
      .Because("one answer folds a bounded batch, so a 25,000-change backlog is not folded in two calls");

    await _foldTheHistoryAsync(conn, origin);
    await Assert.That(await _pendingAsync(conn, origin)).IsEqualTo(0);
    await _assertEqualsTheRowsAsync(conn, origin, "once every change is folded");
  }

  [Test]
  public async Task ARebuild_FoldsTheRowsTheTrackingNeverSaw_AndAnswersAsTheRowsDoAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _execAsync(conn, "ALTER TABLE wh_event_store DISABLE TRIGGER USER");
    await _seedHistoryAsync(conn, origin, 40, sequencedToo: true);
    await _execAsync(conn, "ALTER TABLE wh_event_store ENABLE TRIGGER USER");

    await _execAsync(conn, "SELECT wh_rebuild_unsequenced_digests()");

    await _assertEqualsTheRowsAsync(conn, origin, "after a rebuild over rows inserted untracked");
  }

  [Test]
  public async Task ATruncatedEventStore_HoldsNothingWithoutASequenceAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _seedHistoryAsync(conn, origin, 10, sequencedToo: false);
    _ = await _answerAsync(origin);

    await _execAsync(conn, "TRUNCATE wh_event_store CASCADE");

    await Assert.That(await _answerAsync(origin)).IsEmpty();
    await _assertEqualsTheRowsAsync(conn, origin, "after a truncate");
  }

  // ── helpers ──

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>A store with <paramref name="history"/> settled sequence-less rows, answered once (the history folded in),
  /// then the same new work; returns the cost of the answer that follows.</summary>
  private async Task<(long RowsRead, long Blocks)> _measureSteadyStateAsync(int history) {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var origin = TrackedGuid.New().Value;
    await _seedHistoryRebuiltAsync(conn, origin, history, sequencedToo: false);
    await _seedHistoryAsync(conn, origin, NEW_ROWS, sequencedToo: false);
    await _seedHistoryAsync(conn, origin, UNSETTLED_ROWS, sequencedToo: false, age: "1 minute");
    await _execAsync(conn, "ANALYZE");
    var nodes = await _plansOfAsync(conn, origin);
    var scans = nodes.Where(n => n.Relation is not null).ToList();
    Console.WriteLine($"#2351 held fold, history {history}: " + string.Join("; ", scans.GroupBy(n => n.Relation)
      .Select(g => $"{g.Key} {g.Sum(n => n.RowsRead)} rows {g.Sum(n => n.Blocks)} blocks")));
    return (scans.Sum(n => n.RowsRead), scans.Sum(n => n.Blocks));
  }

  /// <summary>Answers until no change of the origin is left to fold: each answer folds a bounded batch, so a history
  /// written as changes takes more than one. What follows measures the steady state, not the catching up.</summary>
  private async Task _foldTheHistoryAsync(NpgsqlConnection conn, Guid origin) {
    for (var answers = 0; answers < 10; answers++) {
      _ = await _answerAsync(origin);
      if (await _pendingAsync(conn, origin) == 0) {
        return;
      }
    }
    throw new InvalidOperationException("the history's changes were never all folded in ten answers");
  }

  private static async Task<long> _pendingAsync(NpgsqlConnection conn, Guid origin) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_unsequenced_digest_changes WHERE origin_service_id = @origin";
    cmd.Parameters.AddWithValue(nameof(origin), origin);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>
  /// The held history as a service has it once migration 452 ran: rows written before the tracking existed, folded by
  /// the rebuild, with no change log behind them. Folded changes are deleted rows until vacuum reclaims them, and whether
  /// a VACUUM here can reclaim them depends on every snapshot open on the server, so a history written through the log
  /// would measure the cluster, not the answer. A burst of changes in production leaves at most the change log's
  /// vacuum threshold of dead rows (452).
  /// </summary>
  private static async Task _seedHistoryRebuiltAsync(NpgsqlConnection conn, Guid origin, int rows, bool sequencedToo) {
    await _execAsync(conn, "ALTER TABLE wh_event_store DISABLE TRIGGER USER");
    await _seedHistoryAsync(conn, origin, rows, sequencedToo);
    await _execAsync(conn, "ALTER TABLE wh_event_store ENABLE TRIGGER USER");
    await _execAsync(conn, "SELECT wh_rebuild_unsequenced_digests()");
  }

  /// <summary>Sequence-less rows of the origin over two tenants and two types, and as many sequenced ones.</summary>
  private static Task _seedHistoryAsync(NpgsqlConnection conn, Guid origin, int rows, bool sequencedToo,
      string age = "2 hours") =>
    _execAsync(conn, $"""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope,
                                  commit_sequence, created_at, origin_service_id, origin_commit_sequence)
      SELECT gen_random_uuid(), x.s, x.s, 'TestAggregate', 1,
             CASE WHEN x.g % 2 = 0 THEN '{TYPE}' ELSE '{OTHER_TYPE}' END,
             jsonb_build_object('t', CASE WHEN x.g % 3 = 0 THEN 'tenant-b' ELSE 'tenant-a' END),
             nextval('wh_commit_seq'), NOW() - INTERVAL '{age}', '{origin}'::uuid,
             CASE WHEN x.g > {rows} THEN x.g END
      FROM (SELECT g, gen_random_uuid() AS s FROM generate_series(1, {(sequencedToo ? rows * 2 : rows)}) AS g) x
      """);

  private static async Task _seedOneAsync(NpgsqlConnection conn, Guid? origin, Guid eventId, string type, long? originSeq,
      int flags, string tenant, string age) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
      VALUES (@event, @event, @event, 'TestAggregate', @type, jsonb_build_object('t', @tenant), 1,
              nextval('wh_commit_seq'), @flags, NOW() - INTERVAL '{age}', @origin, @oseq)
      """;
    cmd.Parameters.AddWithValue("event", eventId);
    cmd.Parameters.AddWithValue(nameof(type), type);
    cmd.Parameters.AddWithValue(nameof(tenant), tenant);
    cmd.Parameters.AddWithValue(nameof(flags), flags);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(origin), NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)origin ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("oseq", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)originSeq ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>The answer through the coordinator, for both types, with the given settle window.</summary>
  private async Task<IReadOnlyList<StreamDigest>> _answerAsync(Guid origin, int settleSeconds = 3600) {
    await using var ctx = CreateDbContext();
    return await _coordinator(ctx).ComputeUnsequencedReceivedDigestsAsync(
      origin, [TYPE, OTHER_TYPE, EXEMPT_TYPE], streamIds: null, TimeSpan.FromSeconds(settleSeconds));
  }

  /// <summary>The answer, for a settled window and for none, equals a fold of the rows written here independently.</summary>
  private async Task _assertEqualsTheRowsAsync(NpgsqlConnection conn, Guid origin, string because) {
    foreach (var settle in new[] { 3600, 0 }) {
      var answer = (await _answerAsync(origin, settle))
        .Select(d => $"{d.TenantScope}|{d.EventType}|{d.StreamId}|{d.DigestLo}|{d.DigestHi}|{d.EventCount}")
        .OrderBy(s => s, StringComparer.Ordinal).ToList();
      var rows = await _foldOfTheRowsAsync(conn, origin, settle);
      await Assert.That(answer).IsEquivalentTo(rows).Because($"{because}, settle {settle}s");
    }
  }

  private static async Task<List<string>> _foldOfTheRowsAsync(NpgsqlConnection conn, Guid origin, int settleSeconds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT COALESCE(scope->>'t', ''), event_type,
             bit_xor(hashtextextended(event_id::text, 0)), bit_xor(hashtextextended(event_id::text, 1)), COUNT(*)
      FROM wh_event_store
      WHERE origin_service_id = @origin AND origin_commit_sequence IS NULL
        AND event_type = ANY(@types) AND COALESCE(flags, 0) & 8 = 0 AND COALESCE(flags, 0) & 32 = 0
        AND created_at < NOW() - make_interval(secs => @settle)
      GROUP BY 1, 2
      """;
    cmd.Parameters.AddWithValue(nameof(origin), origin);
    cmd.Parameters.AddWithValue("types", new[] { TYPE, OTHER_TYPE });
    cmd.Parameters.AddWithValue("settle", settleSeconds);
    var rows = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      var tenant = reader.GetString(0);
      rows.Add($"{(tenant.Length == 0 ? null : tenant)}|{reader.GetString(1)}|{Guid.Empty}|{reader.GetInt64(2)}|{reader.GetInt64(3)}|{reader.GetInt64(4)}");
    }
    return [.. rows.OrderBy(s => s, StringComparer.Ordinal)];
  }

  /// <summary>Runs the coordinator's type-level statement under the generic plan with auto_explain (nested statements
  /// included), and returns every plan node it ran with the rows and buffers it read.</summary>
  private static async Task<IReadOnlyList<PlanNode>> _plansOfAsync(NpgsqlConnection conn, Guid origin) {
    var notices = new List<string>();
    void Collect(object? _, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    await _execAsync(conn, """
      SET plan_cache_mode = force_generic_plan;
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = on;
      SET auto_explain.log_buffers = on;
      SET auto_explain.log_timing = off;
      SET auto_explain.log_nested_statements = on;
      SET auto_explain.log_format = 'json';
      SET auto_explain.log_level = 'notice';
      """);
    conn.Notice += Collect;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = EFCoreWorkCoordinator<WorkCoordinationDbContext>.UnsequencedByTypeSql("public");
      cmd.Parameters.Add(new NpgsqlParameter("p_origin", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = origin });
      cmd.Parameters.Add(new NpgsqlParameter("p_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = new[] { TYPE, OTHER_TYPE }
      });
      cmd.Parameters.Add(new NpgsqlParameter("p_settle", "3600 seconds"));
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // drained: the plan is logged when the statement completes
      }
    }
    conn.Notice -= Collect;
    await _execAsync(conn, "SET auto_explain.log_min_duration = -1");
    var nodes = new List<PlanNode>();
    foreach (var notice in notices.Where(n => n.Contains("\"Plan\"", StringComparison.Ordinal))) {
      using var document = JsonDocument.Parse(notice[notice.IndexOf('{', StringComparison.Ordinal)..]);
      _flatten(document.RootElement.GetProperty("Plan"), nodes);
    }
    return nodes;
  }

  private static void _flatten(JsonElement node, List<PlanNode> nodes) {
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        _flatten(child, nodes);
      }
    }
    var loops = node.GetProperty("Actual Loops").GetDouble();
    nodes.Add(new PlanNode(
      node.GetProperty("Node Type").GetString()!,
      node.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
      // Rows read, not rows kept: a filter applied after an index range still paid for every row it removed.
      (long)((node.GetProperty("Actual Rows").GetDouble()
        + (node.TryGetProperty("Rows Removed by Filter", out var removed) ? removed.GetDouble() : 0)) * loops),
      (long)((node.TryGetProperty("Shared Hit Blocks", out var hit) ? hit.GetDouble() : 0)
        + (node.TryGetProperty("Shared Read Blocks", out var read) ? read.GetDouble() : 0))));
  }

  private sealed record PlanNode(string Type, string? Relation, long RowsRead, long Blocks);

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }
}
