// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// The database's half of a collective apply's progress record (#1969, #1970): a dead letter marks the apply abandoned (and
/// records that nothing was applied when no batch committed), the reach read tells a replay how far each apply got and
/// whether its collective is still queued, purging a dead letter sweeps its abandoned records, and maintenance sweeps
/// finished records past their horizon once their work row is gone.
/// </summary>
/// <docs>fundamentals/messaging/collective-events#resuming-an-apply</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/279_CollectiveApplyProgress.sql</code-under-test>
[Category("Integration")]
[Category("CollectiveEvents")]
[Category("Shard4")]
public class CollectiveApplyProgressSqlTests : EFCoreTestBase {
  private const string ARCHIVE = "H.Archive:M";
  private const string REVIEW = "H.Review:M";
  private const string WHOLE = "*";

  // ── A dead letter marks the apply abandoned ───────────────────────────────────────────────────

  [Test]
  public async Task DeadLetteringACollectiveSinkRow_MarksItsUnfinishedApplyAbandoned_AndLeavesTheFinishedOneAsync() {
    await using var connection = await _openAsync();
    var eventId = Guid.NewGuid();
    var through = Guid.NewGuid();
    await _recordAsync(connection, eventId, ARCHIVE, Guid.NewGuid(), completed: true);
    await _recordAsync(connection, eventId, REVIEW, through, completed: false);

    await _deadLetterAsync(connection, await _queueAsync(connection, eventId));

    await Assert.That(await _recordOfAsync(connection, eventId, ARCHIVE)).IsEqualTo(("completed", false))
      .Because("a finished apply reached every row, whatever happened to its work row after");
    await Assert.That(await _recordOfAsync(connection, eventId, REVIEW)).IsEqualTo(("partial", true))
      .Because("the unfinished apply stops where it is, and a rewind folds it only into the rows it reached");
    await Assert.That(await _throughOfAsync(connection, eventId, REVIEW)).IsEqualTo(through);
    await Assert.That(await _recordOfAsync(connection, eventId, WHOLE)).IsEqualTo(("none", true))
      .Because("a handler with no record of its own applied nothing before the dead letter");
  }

  [Test]
  public async Task DeadLetteringACollectiveThatAppliedNothing_RecordsThatNothingWasAppliedAsync() {
    await using var connection = await _openAsync();
    var eventId = Guid.NewGuid();

    await _deadLetterAsync(connection, await _queueAsync(connection, eventId));

    await Assert.That(await connection.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM wh_collective_apply_progress WHERE event_id = @eventId", new { eventId }))
      .IsEqualTo(1L);
    await Assert.That(await _recordOfAsync(connection, eventId, WHOLE)).IsEqualTo(("none", true));
  }

  [Test]
  public async Task DeadLetteringAnotherPerspectivesWorkRow_RecordsNothingAsync() {
    await using var connection = await _openAsync();
    var eventId = Guid.NewGuid();

    await _deadLetterAsync(connection, await _queueAsync(connection, eventId, perspective: "Some.Perspective"));

    await Assert.That(await connection.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM wh_collective_apply_progress WHERE event_id = @eventId", new { eventId }))
      .IsEqualTo(0L);
  }

  // ── The reach read ────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Reach_ReadsEveryRecordOfTheEvents_AndWhichCollectivesAreStillQueuedAsync() {
    await using var connection = await _openAsync();
    var queued = Guid.NewGuid();
    var finished = Guid.NewGuid();
    var through = Guid.NewGuid();
    await _queueAsync(connection, queued);
    await _recordAsync(connection, queued, ARCHIVE, through, completed: false);
    await _recordAsync(connection, finished, ARCHIVE, Guid.NewGuid(), completed: true);
    await _recordAsync(connection, Guid.NewGuid(), ARCHIVE, Guid.NewGuid(), completed: true);

    var rows = await connection.QueryAsync<(Guid EventId, string? EntryKey, Guid? Through, bool Completed, bool Abandoned, bool Queued)>(
      "SELECT out_event_id, out_entry_key, out_through_id, out_completed, out_abandoned, out_queued "
      + "FROM wh_collective_apply_reach(@ids) ORDER BY out_queued, out_completed",
      new { ids = new[] { queued, finished } });

    await Assert.That(rows.ToList()).IsEquivalentTo([
      (queued, (string?)ARCHIVE, (Guid?)through, false, false, false),
      (finished, (string?)ARCHIVE, rows.Single(r => r.EventId == finished).Through, true, false, false),
      (queued, (string?)null, (Guid?)null, false, false, true),
    ]).Because("one row per record of the named events, and one per named collective whose work row is still pending");
  }

  // ── Purging a dead letter sweeps its abandoned records ───────────────────────────────────────

  [Test]
  public async Task PurgingTheDeadLetter_SweepsItsAbandonedRecords_AndKeepsTheFinishedOnesAsync() {
    await using var connection = await _openAsync();
    var eventId = Guid.NewGuid();
    await _recordAsync(connection, eventId, ARCHIVE, Guid.NewGuid(), completed: true);
    await _recordAsync(connection, eventId, REVIEW, Guid.NewGuid(), completed: false);
    var deadLetter = await _deadLetterAsync(connection, await _queueAsync(connection, eventId));

    await connection.ExecuteAsync("DELETE FROM wh_dead_letters WHERE dead_letter_id = @deadLetter", new { deadLetter });

    await Assert.That(await connection.QueryAsync<string>(
        "SELECT entry_key FROM wh_collective_apply_progress WHERE event_id = @eventId", new { eventId }))
      .IsEquivalentTo([ARCHIVE])
      .Because("an abandoned record goes with its dead letter; after that a rewind folds the collective in full again");
  }

  [Test]
  public async Task PurgingADeadLetter_KeepsTheAbandonedRecords_WhileAnotherDeadLetterOfTheCollectiveRemainsAsync() {
    await using var connection = await _openAsync();
    var eventId = Guid.NewGuid();
    await _recordAsync(connection, eventId, REVIEW, Guid.NewGuid(), completed: false);
    var first = await _deadLetterAsync(connection, await _queueAsync(connection, eventId));
    await _deadLetterAsync(connection, await _queueAsync(connection, eventId));

    await connection.ExecuteAsync("DELETE FROM wh_dead_letters WHERE dead_letter_id = @first", new { first });

    await Assert.That(await connection.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM wh_collective_apply_progress WHERE event_id = @eventId AND abandoned", new { eventId }))
      .IsEqualTo(2L)
      .Because("the collective's later dead letter still stands for the rows the apply never reached");
  }

  // ── Maintenance sweeps finished records past their horizon ───────────────────────────────────

  [Test]
  public async Task TheSweep_RemovesAFinishedRecordPastTheHorizon_AndKeepsAYoungerOneAsync() {
    await using var connection = await _openAsync();
    var old = Guid.NewGuid();
    var young = Guid.NewGuid();
    await _recordAsync(connection, old, ARCHIVE, Guid.NewGuid(), completed: true, ageDays: 8);
    await _recordAsync(connection, young, ARCHIVE, Guid.NewGuid(), completed: true, ageDays: 6);

    var results = await _sweepAsync(connection);

    await Assert.That(await _eventsWithRecordsAsync(connection, old, young)).IsEquivalentTo([young])
      .Because("the default horizon is 7 days");
    await Assert.That(results["purge_collective_apply_progress"]).IsEqualTo((1L, "ok"));
  }

  [Test]
  public async Task TheSweep_HonorsTheConfiguredHorizonAsync() {
    await using var connection = await _openAsync();
    await _settingAsync(connection, "collective_apply_progress_retention_days", "1");
    var twoDays = Guid.NewGuid();
    await _recordAsync(connection, twoDays, ARCHIVE, Guid.NewGuid(), completed: true, ageDays: 2);

    await _sweepAsync(connection);

    await Assert.That(await _eventsWithRecordsAsync(connection, twoDays)).IsEmpty();
  }

  [Test]
  public async Task TheSweep_KeepsAFinishedRecord_WhileItsWorkRowIsPendingAsync() {
    await using var connection = await _openAsync();
    var eventId = Guid.NewGuid();
    await _recordAsync(connection, eventId, ARCHIVE, Guid.NewGuid(), completed: true, ageDays: 30);
    await _queueAsync(connection, eventId);

    await _sweepAsync(connection);

    await Assert.That(await _eventsWithRecordsAsync(connection, eventId)).IsEquivalentTo([eventId])
      .Because("a re-delivery of a pending collective must still find its finished apply and skip it");
  }

  [Test]
  public async Task TheSweep_KeepsUnfinishedAndAbandonedRecords_AndTheFinishedRecordsOfADeadLetteredCollectiveAsync() {
    await using var connection = await _openAsync();
    var partial = Guid.NewGuid();
    var deadLettered = Guid.NewGuid();
    await _recordAsync(connection, partial, ARCHIVE, Guid.NewGuid(), completed: false, ageDays: 30);
    await _recordAsync(connection, deadLettered, ARCHIVE, Guid.NewGuid(), completed: true, ageDays: 30);
    await _recordAsync(connection, deadLettered, REVIEW, Guid.NewGuid(), completed: false, ageDays: 30);
    await _deadLetterAsync(connection, await _queueAsync(connection, deadLettered));

    await _sweepAsync(connection);

    await Assert.That(await connection.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM wh_collective_apply_progress WHERE event_id = ANY(@ids)", new { ids = new[] { partial, deadLettered } }))
      .IsEqualTo(4L)
      .Because("an abandoned record goes only with its dead letter, and a rewind reads the collective's records together");
  }

  [Test]
  public async Task TheSweep_UnderDebugMode_KeepsEveryRecordAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(
      "INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES ('debug_mode', 'true', 'boolean', 'test') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = 'true'");
    var old = Guid.NewGuid();
    await _recordAsync(connection, old, ARCHIVE, Guid.NewGuid(), completed: true, ageDays: 30);

    var results = await _sweepAsync(connection);

    await Assert.That(await _eventsWithRecordsAsync(connection, old)).IsEquivalentTo([old]);
    await Assert.That(results["purge_collective_apply_progress"].Status).IsEqualTo("skipped (debug_mode=true)");
  }

  // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static Task _recordAsync(
      NpgsqlConnection connection, Guid eventId, string entryKey, Guid through, bool completed, int ageDays = 0) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_collective_apply_progress (event_id, entry_key, through_id, completed, applied_at) "
      + "VALUES (@eventId, @entryKey, @through, @completed, NOW() - make_interval(days => @ageDays))",
      new { eventId, entryKey, through, completed, ageDays });

  // A pending __collective__ work row (or another perspective's) for the event, on the event's sink stream: a collective's
  // work rows are always on its own stream, here named after the event.
  private static async Task<Guid> _queueAsync(NpgsqlConnection connection, Guid eventId, string perspective = "__collective__") {
    var workId = Guid.NewGuid();
    await connection.ExecuteAsync(
      "INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at) "
      + "VALUES (@workId, @stream, @perspective, @eventId, 0, 1, 0, now())",
      new { workId, stream = eventId, perspective, eventId });
    return workId;
  }

  private static async Task<Guid> _deadLetterAsync(NpgsqlConnection connection, Guid workId) {
    var deadLetter = Guid.NewGuid();
    await connection.ExecuteAsync(
      "SELECT move_to_dead_letters(@deadLetter, 'wh_perspective_events', @workId, 1, 'failed', @instance, 'test')",
      new { deadLetter, workId, instance = Guid.NewGuid() });
    return deadLetter;
  }

  // ("completed" | "partial" | "none", abandoned), or ("missing", false) when there is no record.
  private static async Task<(string State, bool Abandoned)> _recordOfAsync(NpgsqlConnection connection, Guid eventId, string entryKey) {
    var rows = (await connection.QueryAsync<(Guid? Through, bool Completed, bool Abandoned)>(
      "SELECT through_id, completed, abandoned FROM wh_collective_apply_progress WHERE event_id = @eventId AND entry_key = @entryKey",
      new { eventId, entryKey })).ToList();
    if (rows.Count == 0) {
      return ("missing", false);
    }
    var row = rows[0];
    if (row.Completed) {
      return ("completed", row.Abandoned);
    }
    return (row.Through is null ? "none" : "partial", row.Abandoned);
  }

  private static Task<Guid?> _throughOfAsync(NpgsqlConnection connection, Guid eventId, string entryKey) =>
    connection.ExecuteScalarAsync<Guid?>(
      "SELECT through_id FROM wh_collective_apply_progress WHERE event_id = @eventId AND entry_key = @entryKey",
      new { eventId, entryKey });

  private static async Task<List<Guid>> _eventsWithRecordsAsync(NpgsqlConnection connection, params Guid[] ids) =>
    [.. await connection.QueryAsync<Guid>(
      "SELECT DISTINCT event_id FROM wh_collective_apply_progress WHERE event_id = ANY(@ids)", new { ids })];

  private static Task _settingAsync(NpgsqlConnection connection, string key, string value) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES (@key, @value, 'integer', 'test') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value", new { key, value });

  private static async Task<Dictionary<string, (long Rows, string Status)>> _sweepAsync(NpgsqlConnection connection) {
    var results = new Dictionary<string, (long Rows, string Status)>(StringComparer.Ordinal);
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT task_name, rows_affected, status FROM reap_unbounded_ledgers()";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      results[reader.GetString(0)] = (reader.GetInt64(1), reader.GetString(2));
    }
    return results;
  }
}
