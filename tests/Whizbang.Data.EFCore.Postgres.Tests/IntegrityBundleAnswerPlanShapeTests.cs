// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Recording a redelivery bundle's answer costs in proportion to the bundle, not the ledger (#2093). Under the generic
/// plan a long-lived connection settles on, the call finds the ledger rows it covers (the bucket and its type lane) by
/// primary key, never by reading the ledger; it does not rewrite a row that is already answered, which every bundle of
/// a type would otherwise queue on; and it locks the rows it writes in primary key order, so two bundles answered at
/// once queue rather than deadlock.
/// </summary>
/// <remarks>
/// Read from the plans the call's own statements ran (auto_explain, nested statements, actual row counts), which do not
/// depend on the machine. The ledger is seeded wide for one (origin, tenant, type), so a scan of that prefix is
/// visible as thousands of rows where the call needs at most two.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/333_BundleAnswerReadsOnlyItsBuckets.sql</code-under-test>
/// <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityBundleAnswerPlanShapeTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Upstream.Contracts.OrderPlaced, Upstream.Contracts";
  private const int LEDGER_STREAMS = 20_000;

  // Fixed instants, not the host clock: the function takes the caller's clock.
  private static readonly DateTimeOffset _t0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task RecordBundleAnswer_UnderAGenericPlan_FindsTheCoveredLedgerRowsByPrimaryKey_NotALedgerScanAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var (origin, _, eventId) = await _seedWideLedgerAsync(conn);
    await _execAsync(conn, "SET plan_cache_mode = force_generic_plan");

    var nodes = await _plansOfOneAnswerAsync(conn, origin, eventId);

    var ledgerReads = nodes.Where(n => n.Relation == "wh_integrity_ledger").ToList();
    await Assert.That(ledgerReads).IsNotEmpty().Because("precondition: the call reads the ledger rows it answers");
    await Assert.That(ledgerReads.Any(n => n.Type == "Seq Scan")).IsFalse()
      .Because("a covered row is found by its key; a sequential scan reads the whole ledger on every bundle");
    var widest = ledgerReads.MaxBy(n => n.RowsReturned)!;
    await Assert.That(widest.RowsReturned).IsLessThanOrEqualTo(2)
      .Because($"a one-event bundle covers its bucket and its type lane, yet one {widest.Type} "
        + $"({widest.Index ?? "no index"}) read {widest.RowsReturned} ledger rows of {LEDGER_STREAMS + 1}: matching the "
        + "stream with an OR cannot be an index condition, so the read widens to every stream of the type");
    await Assert.That(ledgerReads.Where(n => n.Index is not null).All(n => n.Index == "wh_integrity_ledger_pkey")).IsTrue();
  }

  [Test]
  public async Task RecordBundleAnswer_LocksTheRowsItWrites_InPrimaryKeyOrderAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var (origin, _, eventId) = await _seedWideLedgerAsync(conn);
    await _execAsync(conn, "SET plan_cache_mode = force_generic_plan");

    var nodes = await _plansOfOneAnswerAsync(conn, origin, eventId);

    var locks = nodes.Where(n => n.Type == "LockRows").ToList();
    await Assert.That(locks).Count().IsEqualTo(1)
      .Because("the rows the call may write are locked once, before either update, so concurrent bundles queue on "
        + "them in one order; updates that lock as their joins visit rows can each hold a row the other wants");
    await Assert.That(locks[0].ChildType).IsEqualTo("Sort")
      .Because("the lock is taken in the order the sort returns the rows, the ledger's primary key order");
  }

  [Test]
  public async Task RecordBundleAnswer_ARowAlreadyAnswered_IsNotRewrittenAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var (origin, _, eventId) = await _seedWideLedgerAsync(conn);
    await _execAsync(conn, $"""
      UPDATE wh_integrity_ledger SET answered_at = '{_t0:O}', answer_pending_until = NULL
      WHERE origin_service_id = '{origin}' AND stream_id = '00000000-0000-0000-0000-000000000000'
      """);
    var versionBefore = await _scalarAsync<string>(conn,
      $"SELECT xmin::text FROM wh_integrity_ledger WHERE origin_service_id = '{origin}' AND stream_id = '00000000-0000-0000-0000-000000000000'");

    await _answerAsync(conn, origin, eventId, _t0.AddMinutes(5));

    await Assert.That(await _scalarAsync<string>(conn,
        $"SELECT xmin::text FROM wh_integrity_ledger WHERE origin_service_id = '{origin}' AND stream_id = '00000000-0000-0000-0000-000000000000'"))
      .IsEqualTo(versionBefore)
      .Because("the type lane was answered with no answer pending: rewriting it changes nothing anyone reads, and every "
        + "bundle of the type would queue on its lock");
    await Assert.That(await _scalarAsync<DateTime>(conn,
        $"SELECT answered_at FROM wh_integrity_ledger WHERE origin_service_id = '{origin}' AND stream_id = '00000000-0000-0000-0000-000000000000'"))
      .IsEqualTo(_t0.UtcDateTime);
  }

  [Test]
  public async Task RecordBundleAnswer_ARowWithItsAnswerPending_IsStillAnsweredAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var (origin, stream, eventId) = await _seedWideLedgerAsync(conn);
    // Answered once before, and asked again since: the second answer must still clear the wait.
    await _execAsync(conn, $"""
      UPDATE wh_integrity_ledger SET answered_at = '{_t0:O}', answer_pending_until = '{_t0.AddHours(3):O}'
      WHERE origin_service_id = '{origin}' AND stream_id IN ('{stream}', '00000000-0000-0000-0000-000000000000')
      """);

    await _answerAsync(conn, origin, eventId, _t0.AddMinutes(5));

    await Assert.That(await _scalarAsync<long>(conn, $"""
      SELECT count(*) FROM wh_integrity_ledger
      WHERE origin_service_id = '{origin}' AND stream_id IN ('{stream}', '00000000-0000-0000-0000-000000000000')
        AND answer_pending_until IS NULL AND answered_at = '{_t0.AddMinutes(5):O}'
      """)).IsEqualTo(2)
      .Because("the bucket and its type lane were waiting for an answer, so both stop waiting and record this one");
  }

  /// <summary>
  /// <see cref="LEDGER_STREAMS"/> per-stream rows and the type lane for one (origin, tenant, type), analyzed, and one
  /// stored event on the first of those streams: the bundle the calls answer.
  /// </summary>
  private static async Task<(Guid Origin, Guid Stream, Guid EventId)> _seedWideLedgerAsync(NpgsqlConnection conn) {
    var origin = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    await _execAsync(conn, $"""
      INSERT INTO wh_integrity_ledger
        (origin_service_id, tenant_scope, event_type, stream_id, origin_lo, origin_hi, local_lo, local_hi, is_deficit)
      SELECT '{origin}', '', '{EVENT_TYPE}', gen_random_uuid(), 1, 1, 0, 0, true FROM generate_series(1, {LEDGER_STREAMS})
      UNION ALL
      SELECT '{origin}', '', '{EVENT_TYPE}', '00000000-0000-0000-0000-000000000000', 1, 1, 0, 0, true
      """);
    await _execAsync(conn, "ANALYZE wh_integrity_ledger");
    var stream = await _scalarAsync<Guid>(conn,
      $"SELECT stream_id FROM wh_integrity_ledger WHERE origin_service_id = '{origin}' AND stream_id <> '00000000-0000-0000-0000-000000000000' ORDER BY stream_id LIMIT 1");
    await _execAsync(conn, $"""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at,
                                  origin_service_id, origin_commit_sequence)
      VALUES ('{eventId}', '{stream}', '{stream}', 'TestAggregate', 1, '{EVENT_TYPE}', NULL, NOW(), '{origin}', 7)
      """);
    return (origin, stream, eventId);
  }

  private static async Task _answerAsync(NpgsqlConnection conn, Guid origin, Guid eventId, DateTimeOffset now) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_integrity_record_bundle_answer($1, $2, $3, $4)";
    cmd.Parameters.AddWithValue(origin);
    cmd.Parameters.AddWithValue(new[] { eventId });
    cmd.Parameters.AddWithValue(new long[] { 7 });
    cmd.Parameters.AddWithValue(now);
    _ = await cmd.ExecuteScalarAsync();
  }

  /// <summary>
  /// One answer, rolled back, with the plan of every statement it ran: auto_explain logs each nested statement's plan,
  /// with actual row counts, as a notice on this connection.
  /// </summary>
  private static async Task<IReadOnlyList<PlanNode>> _plansOfOneAnswerAsync(NpgsqlConnection conn, Guid origin, Guid eventId) {
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
    await _answerAsync(conn, origin, eventId, _t0.AddMinutes(5));
    await _execAsync(conn, "ROLLBACK");
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
    string? childType = null;
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        childType ??= child.GetProperty("Node Type").GetString();
        _flatten(child, nodes);
      }
    }
    nodes.Add(new PlanNode(
      node.GetProperty("Node Type").GetString()!,
      childType,
      node.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
      node.TryGetProperty("Index Name", out var index) ? index.GetString() : null,
      (long)(node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble())));
  }

  /// <summary>One plan node, with the type of its first child (for LockRows, what feeds it).</summary>
  private sealed record PlanNode(string Type, string? ChildType, string? Relation, string? Index, long RowsReturned);

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)(await cmd.ExecuteScalarAsync())!;
  }
}
