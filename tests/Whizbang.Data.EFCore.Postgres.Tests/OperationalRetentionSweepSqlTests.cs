// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2354: the operational retention sweep removes the rows of its Rolling tables that are past their category's resting
/// period and closed, in bounded batches, and reports what is left. An open row (a restore still pending, restoring or
/// failed) is state and stays whatever its age.
/// </summary>
/// <docs>operations/infrastructure/operational-retention</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/451_OperationalRecordsRollOff.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class OperationalRetentionSweepSqlTests : EFCoreTestBase {
  private const long DAY = 86_400;

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private sealed record Row(string Category, string Table, int Expired, long Remaining, double? OldestAgeSeconds);

  private static async Task<List<Row>> _sweepAsync(NpgsqlConnection conn, string retentionJson, int batch = 1000) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT category, table_name, rows_expired, rows_remaining, oldest_age_seconds
      FROM wh_sweep_operational_retention(NOW(), @r::jsonb, @b)
      """;
    cmd.Parameters.AddWithValue("r", retentionJson);
    cmd.Parameters.AddWithValue("b", batch);
    var rows = new List<Row>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new Row(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetDouble(4)));
    }
    return rows;
  }

  /// <summary>One rewrite-log row per generation, each the given number of days old.</summary>
  private static async Task _seedRewritesAsync(NpgsqlConnection conn, params (long Generation, int DaysOld)[] rows) {
    foreach (var (generation, daysOld) in rows) {
      await _executeAsync(conn, $"""
        INSERT INTO wh_integrity_origin_rewrites (generation, rewritten_from, rewritten_at)
        VALUES ({generation}, 0, NOW() - INTERVAL '{daysOld} days')
        """);
    }
    await _executeAsync(conn, $"""
      UPDATE wh_settings SET setting_value = '{rows.Max(r => r.Generation)}' WHERE setting_key = 'integrity_origin_generation';
      UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'integrity_origin_rewrite_log_from';
      """);
  }

  private static async Task _seedRestoreAsync(NpgsqlConnection conn, string state, int daysOld) {
    var stream = TrackedGuid.New().Value;
    await _executeAsync(conn, $"""
      INSERT INTO wh_stream_lifecycle (stream_id, stream_type, first_appended_at, last_appended_bucket, last_version, state)
      VALUES ('{stream}', 'TestAggregate', NOW(), NOW(), 1, 'Archived');
      INSERT INTO wh_archive_restore_requests (stream_id, state, requested_at, updated_at)
      VALUES ('{stream}', '{state}', NOW() - INTERVAL '{daysOld} days', NOW() - INTERVAL '{daysOld} days');
      """);
  }

  // ── the rewrite log ────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task TheRewriteLog_LosesItsRowsPastTheRetention_AndItsStartMovesWithThemAsync() {
    await using var conn = await _openAsync();
    await _seedRewritesAsync(conn, (1, 40), (2, 20), (3, 5), (4, 1));

    var rows = await _sweepAsync(conn, $$"""{"RewriteLog": {{14 * DAY}}}""");

    var log = rows.Single(r => r.Table == "wh_integrity_origin_rewrites");
    await Assert.That(log.Category).IsEqualTo(nameof(OperationalCategory.RewriteLog));
    await Assert.That(log.Expired).IsEqualTo(2);
    await Assert.That(log.Remaining).IsEqualTo(2L);
    await Assert.That(log.OldestAgeSeconds!.Value).IsGreaterThan(4.9 * DAY).And.IsLessThan(5.1 * DAY);
    await Assert.That(await _scalarAsync(conn,
        "SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_rewrite_log_from'"))
      .IsEqualTo(2L).Because("the log no longer reaches back past generation 2, so it must not vouch for one older");
    await Assert.That(await _scalarAsync(conn, "SELECT CASE WHEN vouched THEN 1 ELSE 0 END FROM wh_integrity_history_rewrite(1)"))
      .IsEqualTo(0L).Because("a consumer behind the log is answered as unknown and re-verifies, never vouched for falsely");
    await Assert.That(await _scalarAsync(conn, "SELECT CASE WHEN vouched THEN 1 ELSE 0 END FROM wh_integrity_history_rewrite(2)"))
      .IsEqualTo(1L);
  }

  [Test]
  public async Task TheRewriteLog_IsRemovedOldestGenerationFirst_InBoundedBatchesAsync() {
    await using var conn = await _openAsync();
    await _seedRewritesAsync(conn, (1, 40), (2, 30), (3, 20), (4, 1));

    var first = (await _sweepAsync(conn, $$"""{"RewriteLog": {{14 * DAY}}}""", batch: 2)).Single(r => r.Table == "wh_integrity_origin_rewrites");

    await Assert.That(first.Expired).IsEqualTo(2).Because("a batch never removes more than its size");
    await Assert.That(await _scalarAsync(conn, "SELECT MIN(generation) FROM wh_integrity_origin_rewrites")).IsEqualTo(3L)
      .Because("the log is removed from its oldest end, so what is left is contiguous and the start is exact");

    var second = (await _sweepAsync(conn, $$"""{"RewriteLog": {{14 * DAY}}}""", batch: 2)).Single(r => r.Table == "wh_integrity_origin_rewrites");
    await Assert.That(second.Expired).IsEqualTo(1);
  }

  // ── finished restore requests ──────────────────────────────────────────────────────────────

  [Test]
  public async Task ARestoredRequest_IsRemovedPastTheRetention_AndAnOpenOrFailedOneStaysAsync() {
    await using var conn = await _openAsync();
    await _seedRestoreAsync(conn, "Restored", 30);
    await _seedRestoreAsync(conn, "Restored", 2);
    await _seedRestoreAsync(conn, "Pending", 30);
    await _seedRestoreAsync(conn, "Restoring", 30);
    await _seedRestoreAsync(conn, "Failed", 30);

    var queues = (await _sweepAsync(conn, $$"""{"Queues": {{14 * DAY}}}""")).Single(r => r.Table == "wh_archive_restore_requests");

    await Assert.That(queues.Category).IsEqualTo(nameof(OperationalCategory.Queues));
    await Assert.That(queues.Expired).IsEqualTo(1);
    await Assert.That(queues.Remaining).IsEqualTo(1L).Because("only finished requests are operational records");
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_archive_restore_requests WHERE state <> 'Restored'"))
      .IsEqualTo(3L).Because("Pending and Restoring are work, and a Failed request holds back a restore storm until a restore succeeds");
  }

  // ── #2359: records nothing else removed ───────────────────────────────────────────────────

  private static async Task _seedDeadLetterAsync(
      NpgsqlConnection conn, string fingerprint, string? stackId = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_dead_letters
        (dead_letter_id, source_table, source_id, message_type, envelope, failure_reason, attempts_when_dlq,
         recovery_status, generation, error_fingerprint, error_fingerprint_version, error_text, stack_id)
      VALUES (@id, 'wh_inbox', @source, 'T.A', '{}'::jsonb, 5, 3, 0, 'seed/1', @fp, 1, 'boom', @sk)
      """;
    cmd.Parameters.AddWithValue("id", TrackedGuid.New().Value);
    cmd.Parameters.AddWithValue("source", TrackedGuid.New().Value);
    cmd.Parameters.AddWithValue("fp", fingerprint);
    cmd.Parameters.AddWithValue("sk", (object?)stackId ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
  }

  private static long _reportsRetention => 14 * DAY;

  /// <summary>
  /// #2359: an advisory finding no scan has touched for the Reports retention goes. A finding still detected keeps its
  /// row, because its row is what keeps a repeat inside the cooldown from being reported again.
  /// </summary>
  /// <docs>operations/infrastructure/operational-retention#what-the-sweep-removes</docs>
  [Test]
  public async Task AnAdvisoryFinding_UntouchedPastTheRetention_GoesAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, """
      INSERT INTO wh_advisory_ledger (finding_key, signature, first_seen_at, last_reported_at, last_touched) VALUES
        ('gone', 's', NOW() - INTERVAL '60 days', NOW() - INTERVAL '60 days', NOW() - INTERVAL '30 days'),
        ('live', 's', NOW() - INTERVAL '60 days', NOW() - INTERVAL '60 days', NOW() - INTERVAL '2 days')
      """);

    var row = (await _sweepAsync(conn, $$"""{"Reports": {{_reportsRetention}}}""")).Single(r => r.Table == "wh_advisory_ledger");

    await Assert.That(row.Category).IsEqualTo(nameof(OperationalCategory.Reports));
    await Assert.That(row.Expired).IsEqualTo(1);
    await Assert.That(row.Remaining).IsEqualTo(1L);
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_advisory_ledger WHERE finding_key = 'live'")).IsEqualTo(1L);
  }

  /// <summary>
  /// #2359: a dead letter summary row goes once it is past the retention AND no dead letter of its cluster is left. While
  /// one is, the next aggregation would only write it back.
  /// </summary>
  /// <docs>operations/infrastructure/operational-retention#what-the-sweep-removes</docs>
  [Test]
  public async Task ADeadLetterSummary_GoesOnlyOnceItsDeadLettersAreGoneAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, """
      INSERT INTO wh_dead_letter_summary
        (error_fingerprint, source_table, message_type, occurrence_count, first_seen_at, last_seen_at, sample_error_text) VALUES
        ('fpgone', 'wh_inbox', 'T.A', 1, NOW() - INTERVAL '40 days', NOW() - INTERVAL '30 days', 'boom'),
        ('fpheld', 'wh_inbox', 'T.A', 1, NOW() - INTERVAL '40 days', NOW() - INTERVAL '30 days', 'boom'),
        ('fpnew',  'wh_inbox', 'T.A', 1, NOW() - INTERVAL '3 days',  NOW() - INTERVAL '2 days',  'boom')
      """);
    await _seedDeadLetterAsync(conn, "fpheld");

    var row = (await _sweepAsync(conn, $$"""{"Reports": {{_reportsRetention}}}""")).Single(r => r.Table == "wh_dead_letter_summary");

    await Assert.That(row.Expired).IsEqualTo(1);
    await Assert.That(row.Remaining).IsEqualTo(2L);
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_dead_letter_summary WHERE error_fingerprint = 'fpgone'"))
      .IsEqualTo(0L);
  }

  /// <summary>
  /// #2359: a finished probe campaign goes once it is past the retention AND its cohort has no dead letter left. A
  /// running campaign is work, and a finished one whose cohort remains is evidence (a failed campaign spends the cohort's
  /// generation budget; a passed one lets its rows be re-driven), so both stay.
  /// </summary>
  /// <docs>operations/infrastructure/operational-retention#what-the-sweep-removes</docs>
  [Test]
  public async Task AFinishedProbeCampaign_GoesOnceItsCohortIsGone_ARunningOneNeverAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, """
      INSERT INTO wh_dlq_probe_campaigns (fingerprint, generation, started_at, probe_ids, verdict, verdict_at) VALUES
        ('cgone',  'g1', NOW() - INTERVAL '40 days', '{}', 2, NOW() - INTERVAL '30 days'),
        ('cheld',  'g1', NOW() - INTERVAL '40 days', '{}', 1, NOW() - INTERVAL '30 days'),
        ('crun',   'g1', NOW() - INTERVAL '40 days', '{}', 0, NULL),
        ('crecent','g1', NOW() - INTERVAL '3 days',  '{}', 3, NOW() - INTERVAL '2 days')
      """);
    await _seedDeadLetterAsync(conn, "cheld");

    var row = (await _sweepAsync(conn, $$"""{"Reports": {{_reportsRetention}}}""")).Single(r => r.Table == "wh_dlq_probe_campaigns");

    await Assert.That(row.Expired).IsEqualTo(1);
    await Assert.That(row.Remaining).IsEqualTo(2L).Because("the finished campaigns left; a running one is work, not a record");
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_dlq_probe_campaigns WHERE fingerprint = 'cgone'")).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_dlq_probe_campaigns WHERE fingerprint = 'crun'")).IsEqualTo(1L);
  }

  /// <summary>
  /// #2359: a stack goes with its links once it is past the retention and neither a dead letter nor the daily history
  /// names it; then every frame old enough that no link names any more goes too. A frame another stack still uses stays.
  /// </summary>
  /// <docs>operations/infrastructure/operational-retention#what-the-sweep-removes</docs>
  [Test]
  public async Task AStack_GoesWithItsLinks_OnceNothingNamesIt_AndThenItsUnusedFramesAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, """
      INSERT INTO wh_stack_frames (frame, first_seen)
        SELECT f, NOW() - INTERVAL '40 days' FROM unnest(ARRAY['only-s1', 'shared', 'orphan']) f;
      INSERT INTO wh_stacks (stack_id, frame_count, is_prose, first_seen, last_seen) VALUES
        ('s1', 2, false, NOW() - INTERVAL '40 days', NOW() - INTERVAL '30 days'),
        ('s2', 1, false, NOW() - INTERVAL '40 days', NOW() - INTERVAL '30 days'),
        ('s3', 1, false, NOW() - INTERVAL '40 days', NOW() - INTERVAL '30 days'),
        ('s4', 1, false, NOW() - INTERVAL '3 days',  NOW() - INTERVAL '2 days');
      INSERT INTO wh_stack_links (stack_id, position, frame_id)
        SELECT v.s, v.p, f.frame_id FROM (VALUES ('s1', 0, 'only-s1'), ('s1', 1, 'shared'), ('s2', 0, 'shared'),
                                                 ('s3', 0, 'shared'), ('s4', 0, 'shared')) v(s, p, fr)
        JOIN wh_stack_frames f ON f.frame = v.fr;
      INSERT INTO wh_stack_daily (stack_id, day, occurrences) VALUES ('s3', CURRENT_DATE - 1, 4);
      """);
    await _seedDeadLetterAsync(conn, "fps2", stackId: "s2");

    var rows = await _sweepAsync(conn, $$"""{"Reports": {{_reportsRetention}}}""");

    var stacks = rows.Single(r => r.Table == "wh_stacks");
    var links = rows.Single(r => r.Table == "wh_stack_links");
    var frames = rows.Single(r => r.Table == "wh_stack_frames");
    await Assert.That(stacks.Expired).IsEqualTo(1).Because("only s1 is old and named by nothing");
    await Assert.That(stacks.Remaining).IsEqualTo(3L);
    await Assert.That(links.Expired).IsEqualTo(2).Because("a stack's links go with it");
    await Assert.That(links.Remaining).IsEqualTo(3L);
    await Assert.That(frames.Expired).IsEqualTo(2).Because("the frame only s1 used, and the frame no link ever named");
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_stack_frames WHERE frame = 'shared'")).IsEqualTo(1L);
  }

  // ── #2360: signal cursors and finished backfills ───────────────────────────────────────────

  /// <summary>
  /// #2360: a signal cursor goes once its instance is no longer registered and the cursor has not moved for the
  /// Deliveries retention. Every pod start adds a cursor and nothing removed one. A live pod's cursor is never removed,
  /// however long it has been idle, because the signal log's retention is held at the lowest live cursor.
  /// </summary>
  /// <docs>operations/infrastructure/operational-retention#what-the-sweep-removes</docs>
  [Test]
  public async Task ASignalCursor_OfAGoneInstance_GoesPastTheRetention_ALivePodsNeverAsync() {
    await using var conn = await _openAsync();
    var live = TrackedGuid.New().Value;
    var gone = TrackedGuid.New().Value;
    var goneRecently = TrackedGuid.New().Value;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@live, 'CursorSvc', 'host', 1, NOW(), NOW() - INTERVAL '60 days', '{}'::jsonb);
        INSERT INTO wh_signal_cursors (instance_id, last_delivered_signal_id, updated_at) VALUES
          (@live, 1, NOW() - INTERVAL '30 days'),
          (@gone, 1, NOW() - INTERVAL '30 days'),
          (@recent, 1, NOW() - INTERVAL '2 days');
        """;
      cmd.Parameters.AddWithValue("live", live);
      cmd.Parameters.AddWithValue("gone", gone);
      cmd.Parameters.AddWithValue("recent", goneRecently);
      await cmd.ExecuteNonQueryAsync();
    }

    var row = (await _sweepAsync(conn, $$"""{"Deliveries": {{14 * DAY}}}""")).Single(r => r.Table == "wh_signal_cursors");

    await Assert.That(row.Category).IsEqualTo(nameof(OperationalCategory.Deliveries));
    await Assert.That(row.Expired).IsEqualTo(1).Because("only the cursor of a gone instance, idle past the retention");
    await Assert.That(await _scalarAsync(conn, $"SELECT COUNT(*) FROM wh_signal_cursors WHERE instance_id = '{live}'"))
      .IsEqualTo(1L).Because("a live pod's cursor holds back the signal log's retention, so it is never removed");
    await Assert.That(await _scalarAsync(conn, $"SELECT COUNT(*) FROM wh_signal_cursors WHERE instance_id = '{goneRecently}'"))
      .IsEqualTo(1L);
  }

  /// <summary>
  /// #2360: a finished subscription backfill is the "already done" marker (a backfill is inserted ON CONFLICT DO
  /// NOTHING), so removing it would start the backfill again. The sweep never touches it, whatever the retention.
  /// </summary>
  /// <docs>operations/infrastructure/operational-retention#every-table-is-classified</docs>
  [Test]
  public async Task AFinishedSubscriptionBackfill_IsNeverSwept_ItIsTheAlreadyDoneMarkerAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, $"""
      INSERT INTO wh_subscription_backfills (backfill_id, owner_service_id, owner_service_name, event_type, started_at, completed_at, outcome)
      VALUES ('{TrackedGuid.New().Value}', '{TrackedGuid.New().Value}', 'owner-svc', 'T.A', NOW() - INTERVAL '400 days', NOW() - INTERVAL '399 days', 1)
      """);
    var everyCategoryAtOneSecond = "{" + string.Join(", ",
      Enum.GetNames<OperationalCategory>().Select(c => $"\"{c}\": 1")) + "}";

    var rows = await _sweepAsync(conn, everyCategoryAtOneSecond);

    await Assert.That(rows.Any(r => r.Table == "wh_subscription_backfills")).IsFalse();
    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_subscription_backfills")).IsEqualTo(1L);
    await Assert.That(OperationalTableRegistry.Find("wh_subscription_backfills")!.Class).IsEqualTo(OperationalTableClass.State);
  }

  // ── no retention: report only ───────────────────────────────────────────────────────────────

  [Test]
  public async Task ACategoryWithNoRetentionGiven_RemovesNothing_ButIsStillReportedAsync() {
    await using var conn = await _openAsync();
    await _seedRewritesAsync(conn, (1, 40));
    await _seedRestoreAsync(conn, "Restored", 40);

    var rows = await _sweepAsync(conn, "{}");

    await Assert.That(rows.Sum(r => r.Expired)).IsEqualTo(0);
    await Assert.That(rows.Single(r => r.Table == "wh_integrity_origin_rewrites").Remaining).IsEqualTo(1L);
    await Assert.That(rows.Single(r => r.Table == "wh_archive_restore_requests").Remaining).IsEqualTo(1L);
  }

  [Test]
  public async Task AnEmptyTable_ReportsNoRows_AndNoAgeAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, "DELETE FROM wh_integrity_origin_rewrites");

    var log = (await _sweepAsync(conn, $$"""{"RewriteLog": {{DAY}}}""")).Single(r => r.Table == "wh_integrity_origin_rewrites");

    await Assert.That(log.Remaining).IsEqualTo(0L);
    await Assert.That(log.OldestAgeSeconds).IsNull();
  }

  // ── the coordinator ────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Coordinator_SweepOperationalRetentionAsync_DrivesTheSqlFunctionAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    await _seedRewritesAsync(conn, (1, 40), (2, 1));
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var results = await coordinator.SweepOperationalRetentionAsync(
      new Dictionary<OperationalCategory, TimeSpan> { [OperationalCategory.RewriteLog] = TimeSpan.FromDays(14) }, batchSize: 100);

    var log = results.Single(r => r.Table == "wh_integrity_origin_rewrites");
    await Assert.That(log.Category).IsEqualTo(OperationalCategory.RewriteLog);
    await Assert.That(log.Expired).IsEqualTo(1);
    await Assert.That(log.Remaining).IsEqualTo(1L);
    await Assert.That(log.OldestAge.HasValue).IsTrue();
  }
}
