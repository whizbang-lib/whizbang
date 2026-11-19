// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The inbox re-offer walk stays on its index after 349 added the fetchable-row predicate (#2117): every read of
/// <c>wh_inbox_state</c> the walk makes is an index scan of <c>idx_inbox_state_held_lanes</c>, the predicate is applied on
/// the event lanes' scans, and nothing in the claim reads the work table sequentially, under the custom plan the function declares and
/// under a generic plan alike.
/// </summary>
/// <remarks>
/// Read from the plans the call's own statements ran (auto_explain, nested statements), which do not depend on the
/// machine. The store is busy: an instance holding many ordinary streams, some streams holding back an unstored event
/// behind an unclaimed predecessor, and a deep unowned backlog, analyzed, so a planner that preferred a scan would show
/// it. The claim is rolled back so every case reads the same store.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/349_ReOfferOnlyFetchableStreams.sql</code-under-test>
/// <docs>fundamentals/work-coordinator/claim-loop#the-re-offer-hands-on-only-what-the-drain-can-fetch</docs>
[Category("Integration")]
[Category("Shard5")]
public class ClaimReofferPlanShapeTests : EFCoreTestBase {
  private const string CLAIM_WORK_SIGNATURE =
    "claim_work(UUID, TEXT, TEXT, INTEGER, INTEGER, INTEGER, INTEGER, DOUBLE PRECISION, INTEGER, BOOLEAN, INTEGER, BOOLEAN, "
    + "INTERVAL, INTEGER, INTERVAL, INTEGER, INTEGER, BIGINT, BIGINT, BOOLEAN, TEXT, BOOLEAN)";
  private const string HELD_LANES_INDEX = "idx_inbox_state_held_lanes";

  [Test]
  [Arguments("force_custom_plan", 1)]
  [Arguments("force_custom_plan", 100)]
  [Arguments("force_generic_plan", 1)]
  [Arguments("force_generic_plan", 100)]
  public async Task TheReOfferWalk_ReadsTheWorkTableOnlyThroughItsHeldLanesIndexAsync(string planMode, int maxStreams) {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var instance = Guid.NewGuid();
    await _seedBusyStoreAsync(conn, instance);
    await _execAsync(conn, $"ALTER FUNCTION {CLAIM_WORK_SIGNATURE} SET plan_cache_mode = {planMode}");

    var (statements, offered) = await _plansOfOneClaimAsync(conn, instance, maxStreams);

    var reoffer = statements.Where(s => s.QueryText.Contains("WITH RECURSIVE lanes", StringComparison.Ordinal)).ToList();
    await Assert.That(reoffer).IsNotEmpty().Because("precondition: the claim ran the inbox re-offer walk");
    await Assert.That(offered).IsEqualTo(maxStreams)
      .Because("precondition: the walk had fetchable held streams to offer and filled the batch with them");
    var walkReads = reoffer.SelectMany(s => s.Nodes).Where(n => n.Relation == "wh_inbox_state").ToList();
    await Assert.That(walkReads).IsNotEmpty();
    await Assert.That(walkReads.All(n => n.Index is not null)).IsTrue()
      .Because("every step of the walk is an index probe; a read without an index prices the poll by the backlog: "
        + string.Join(", ", walkReads.Select(n => $"{n.Type} {n.Index ?? "no index"}")));
    await Assert.That(walkReads.Any(n => n.Index == HELD_LANES_INDEX)).IsTrue();
    // A command lane folds the predicate away (it holds for every command), so it is the event lanes that carry it.
    await Assert.That(walkReads.Any(n => n.Filter?.Contains("chain_emitted_at", StringComparison.Ordinal) == true)).IsTrue()
      .Because("the fetchable-row predicate is evaluated on the index scan that lands on the stream, not afterwards");
  }

  /// <summary>
  /// 500 held stored event streams and 200 held command streams, 100 streams holding back an interactive unstored event
  /// behind an unleased predecessor that is not due yet, and 5,000 unowned pending events, analyzed.
  /// </summary>
  private static async Task _seedBusyStoreAsync(NpgsqlConnection conn, Guid instance) {
    await _execAsync(conn, $$$"""
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ('{{{instance}}}', 'PlanShapeSvc', 'host', 1, NOW(), NOW(), '{}'::jsonb);
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        SELECT gen_random_uuid(), 'H', 'Plan.Evt, Plan', '{"p": {}}', '{}', NOW() - INTERVAL '10 minutes', gen_random_uuid(), TRUE, 250
        FROM generate_series(1, 500)
        RETURNING message_id, stream_id, received_at, priority, is_event)
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry, chain_emitted_at)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 1, 0, '{{{instance}}}', NOW() + INTERVAL '5 minutes', NOW() FROM m;
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        SELECT gen_random_uuid(), 'H', 'Plan.Cmd, Plan', '{"p": {}}', '{}', NOW() - INTERVAL '10 minutes', gen_random_uuid(), FALSE, 250
        FROM generate_series(1, 200)
        RETURNING message_id, stream_id, received_at, priority, is_event)
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 1, 0, '{{{instance}}}', NOW() + INTERVAL '5 minutes' FROM m;
      CREATE TEMP TABLE held_back AS
      SELECT gen_random_uuid() AS sid,
             ('00000000' || substr(md5(g::text), 9, 24))::uuid AS predecessor,
             ('ffffffff' || substr(md5(g::text), 9, 24))::uuid AS held
      FROM generate_series(1, 100) g;
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        SELECT predecessor, 'H', 'Plan.Evt, Plan', '{"p": {}}', '{}', NOW() - INTERVAL '20 minutes', sid, TRUE, 250 FROM held_back
        RETURNING message_id, stream_id, received_at, priority, is_event)
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, scheduled_for)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, 0, NOW() + INTERVAL '1 day' FROM m;
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        SELECT held, 'H', 'Plan.Evt, Plan', '{"p": {}}', '{}', NOW() - INTERVAL '5 minutes', sid, TRUE, 50 FROM held_back
        RETURNING message_id, stream_id, received_at, priority, is_event)
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 5, 0, '{{{instance}}}', NOW() + INTERVAL '5 minutes' FROM m;
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        SELECT gen_random_uuid(), 'H', 'Plan.Evt, Plan', '{"p": {}}', '{}', NOW() - INTERVAL '30 minutes', gen_random_uuid(), TRUE, 250
        FROM generate_series(1, 5000)
        RETURNING message_id, stream_id, received_at, priority, is_event)
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, 0 FROM m;
      ANALYZE wh_inbox;
      ANALYZE wh_inbox_state;
      """);
  }

  /// <summary>
  /// One claim, rolled back, with the plan of every statement it ran (auto_explain notices on this connection), and the
  /// number of inbox streams it re-offered.
  /// </summary>
  private static async Task<(IReadOnlyList<Statement> Statements, int Offered)> _plansOfOneClaimAsync(
      NpgsqlConnection conn, Guid instance, int maxStreams) {
    var notices = new List<string>();
    void Collect(object? _, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    await _execAsync(conn, """
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = on;
      SET auto_explain.log_timing = off;
      SET auto_explain.log_nested_statements = on;
      SET auto_explain.log_format = 'json';
      SET auto_explain.log_level = 'notice';
      """);
    conn.Notice += Collect;
    await _execAsync(conn, "BEGIN");
    int offered;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = $"""
        SELECT count(*)::int FROM claim_work(
          p_instance_id => '{instance}', p_service_name => 'PlanShapeSvc', p_host_name => 'host', p_process_id => 1,
          p_max_streams => {maxStreams}, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => 0)
        WHERE source = 'inbox'
        """;
      cmd.CommandTimeout = 300;
      offered = (int)(await cmd.ExecuteScalarAsync())!;
    }
    await _execAsync(conn, "ROLLBACK");
    conn.Notice -= Collect;
    await _execAsync(conn, "SET auto_explain.log_min_duration = -1");
    var statements = new List<Statement>();
    foreach (var notice in notices.Where(n => n.Contains("\"Plan\"", StringComparison.Ordinal))) {
      using var document = JsonDocument.Parse(notice[notice.IndexOf('{', StringComparison.Ordinal)..]);
      var text = document.RootElement.TryGetProperty("Query Text", out var query) ? query.GetString() ?? string.Empty : string.Empty;
      var nodes = new List<PlanNode>();
      _flatten(document.RootElement.GetProperty("Plan"), nodes);
      statements.Add(new Statement(text, nodes));
    }
    return (statements, offered);
  }

  private static void _flatten(JsonElement node, List<PlanNode> nodes) {
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        _flatten(child, nodes);
      }
    }
    nodes.Add(new PlanNode(
      node.GetProperty("Node Type").GetString()!,
      node.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
      node.TryGetProperty("Index Name", out var index) ? index.GetString() : null,
      node.TryGetProperty("Filter", out var filter) ? filter.GetString() : null));
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 300;
    await cmd.ExecuteNonQueryAsync();
  }

  private sealed record Statement(string QueryText, IReadOnlyList<PlanNode> Nodes);

  private sealed record PlanNode(string Type, string? Relation, string? Index, string? Filter);
}
