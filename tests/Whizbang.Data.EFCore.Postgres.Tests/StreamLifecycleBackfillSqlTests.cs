// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The one-time backfill of the stream lifecycle ledger (#1732): <c>wh_backfill_stream_lifecycle_batch</c> seeds a row
/// for every stream the event store held before the ledger existed. It runs in bounded batches outside the migration,
/// resumes from a cursor, may be run again without changing anything, and is gated on a <c>wh_settings</c> version
/// marker so a store that has finished never reads its event store for it again.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#the-one-time-backfill</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/260_StreamLifecycleLedger.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StreamLifecycleBackfillSqlTests : EFCoreTestBase {
  private const string VERSION_KEY = "stream_lifecycle_backfill_version";
  private const string CURSOR_KEY = "stream_lifecycle_backfill_cursor";
  private const int FLAG_EPHEMERAL = 8;

  private static readonly DateTimeOffset _t0 = new(2026, 3, 2, 8, 15, 0, TimeSpan.Zero);

  [Test]
  public async Task Backfill_SeedsEveryStreamFromItsEvents_AndRecordsThatItIsDoneAsync() {
    await using var conn = await _openAsync();
    var streams = _orderedStreams(4);
    var (a, b, mixed, ephemeral) = (streams[0], streams[1], streams[2], streams[3]);
    await _seedAsync(conn, a, 1, "Agg.A", _t0, tenant: "tenant-a");
    await _seedAsync(conn, a, 2, "Agg.Later", _t0.AddHours(3), tenant: "tenant-z");
    await _seedAsync(conn, a, 3, "Agg.Later", _t0.AddHours(5).AddMinutes(40));
    await _seedAsync(conn, b, 1, "Agg.B", _t0.AddDays(1));
    // A stream from before streams were kept to one kind: its ephemeral events are left out of its row.
    await _seedAsync(conn, mixed, 1, "Agg.Typing", _t0, flags: FLAG_EPHEMERAL);
    await _seedAsync(conn, mixed, 2, "Agg.Mixed", _t0.AddMinutes(1));
    await _seedAsync(conn, mixed, 3, "Agg.Typing", _t0.AddMinutes(2), flags: FLAG_EPHEMERAL);
    await _seedAsync(conn, ephemeral, 1, "Agg.Typing", _t0, flags: FLAG_EPHEMERAL);
    await _seedAsync(conn, Guid.Empty, 1, "Agg.Streamless", _t0);
    await _restartBackfillAsync(conn);

    var batches = await _runToCompletionAsync(conn, limit: 2);

    await Assert.That(batches).IsEqualTo(3).Because("four streams at two a batch is two batches, and a third finds nothing and finishes");
    await Assert.That(await _settingAsync(conn, VERSION_KEY)).IsEqualTo("1");
    var rowA = (await _rowAsync(conn, a))!;
    await Assert.That(rowA.StreamType).IsEqualTo("Agg.A").Because("the stream type is its first event's aggregate_type");
    await Assert.That(rowA.Tenant).IsEqualTo("tenant-a");
    await Assert.That(rowA.FirstAppendedAt).IsEqualTo(_t0);
    await Assert.That(rowA.LastAppendedBucket).IsEqualTo(new DateTimeOffset(2026, 3, 2, 13, 0, 0, TimeSpan.Zero))
      .Because("the bucket is the UTC hour of the newest event");
    await Assert.That(rowA.LastVersion).IsEqualTo(3L);
    await Assert.That(rowA.ApproxBytes).IsEqualTo(await _bodyBytesAsync(conn, a, includeEphemeral: true));
    await Assert.That(rowA.State).IsEqualTo("Active");
    await Assert.That((await _rowAsync(conn, b))!.LastVersion).IsEqualTo(1L);
    var rowMixed = (await _rowAsync(conn, mixed))!;
    await Assert.That(rowMixed.StreamType).IsEqualTo("Agg.Mixed");
    await Assert.That(rowMixed.FirstAppendedAt).IsEqualTo(_t0.AddMinutes(1));
    await Assert.That(rowMixed.LastVersion).IsEqualTo(2L).Because("the row describes the stream's Sourced events, as the live paths write it");
    await Assert.That(rowMixed.ApproxBytes).IsEqualTo(await _bodyBytesAsync(conn, mixed, includeEphemeral: false));
    await Assert.That(await _rowAsync(conn, ephemeral)).IsNull().Because("an ephemeral stream gets no row");
    await Assert.That(await _rowAsync(conn, Guid.Empty)).IsNull().Because("events without a stream get no row");
  }

  [Test]
  public async Task Backfill_StoppedAfterOneBatch_ResumesAfterItsCursor_AndFinishesTheRestAsync() {
    await using var conn = await _openAsync();
    var streams = _orderedStreams(5);
    foreach (var stream in streams) {
      await _seedAsync(conn, stream, 1, "Agg.Resume", _t0);
    }
    await _restartBackfillAsync(conn);

    var first = await _batchAsync(conn, limit: 2);
    var rowsAfterFirst = await _ledgerCountAsync(conn);
    var cursor = await _settingAsync(conn, CURSOR_KEY);
    var rest = await _runToCompletionAsync(conn, limit: 2);

    await Assert.That(first).IsEqualTo(2).Because("a batch visits at most its limit of streams");
    await Assert.That(rowsAfterFirst).IsEqualTo(2L);
    await Assert.That(await _rowAsync(conn, streams[0])).IsNotNull().Because("streams are visited in stream id order");
    await Assert.That(cursor).IsEqualTo(streams[1].ToString()).Because("the cursor records the last stream the batch committed");
    await Assert.That(rest).IsEqualTo(3).Because("the next caller starts after the cursor: two more batches and the one that finishes");
    await Assert.That(await _ledgerCountAsync(conn)).IsEqualTo(5L);
    await Assert.That(await _settingAsync(conn, VERSION_KEY)).IsEqualTo("1");
  }

  [Test]
  public async Task Backfill_RunAgainFromTheStart_WritesTheSameRowsAsync() {
    await using var conn = await _openAsync();
    var streams = _orderedStreams(3);
    for (var i = 0; i < streams.Length; i++) {
      await _seedAsync(conn, streams[i], 1, "Agg.Again", _t0.AddMinutes(i), tenant: "tenant-" + i);
      await _seedAsync(conn, streams[i], 2, "Agg.Again", _t0.AddHours(i + 1));
    }
    await _restartBackfillAsync(conn);
    await _runToCompletionAsync(conn, limit: 2);
    var first = await _ledgerSnapshotAsync(conn);

    await _restartBackfillAsync(conn);
    await _runToCompletionAsync(conn, limit: 1);
    var second = await _ledgerSnapshotAsync(conn);

    await Assert.That(second).IsEquivalentTo(first).Because("a row is computed from the stream's events, so computing it again changes nothing");
    await Assert.That(second.Count).IsEqualTo(3);
  }

  [Test]
  public async Task Backfill_OnceDone_ReadsNothingAndChangesNothingAsync() {
    await using var conn = await _openAsync();
    var stream = _orderedStreams(1)[0];
    await _seedAsync(conn, stream, 1, "Agg.Done", _t0);
    await _restartBackfillAsync(conn);
    await _runToCompletionAsync(conn, limit: 10);
    await _executeAsync(conn, "UPDATE wh_stream_lifecycle SET approx_bytes = 7 WHERE stream_id = @s", ("s", stream));
    var late = _orderedStreams(1)[0];
    await _seedAsync(conn, late, 1, "Agg.Late", _t0);

    var result = await _batchAsync(conn, limit: 10);

    await Assert.That(result).IsEqualTo(0).Because("the version marker says the backfill is done");
    await Assert.That((await _rowAsync(conn, stream))!.ApproxBytes).IsEqualTo(7L)
      .Because("a finished backfill recomputes nothing; the control value it would have overwritten is still there");
    await Assert.That(await _rowAsync(conn, late)).IsNull();
  }

  [Test]
  public async Task Backfill_CorrectsARowALiveAppendCreatedBeforeItArrivedAsync() {
    await using var conn = await _openAsync();
    var stream = _orderedStreams(1)[0];
    await _seedAsync(conn, stream, 1, "Agg.Old", _t0, tenant: "tenant-old");
    await _seedAsync(conn, stream, 2, "Agg.Old", _t0.AddMinutes(5));
    await _restartBackfillAsync(conn);
    // An append after the migration and before the backfill reached the stream: its row knows only that append.
    await _executeAsync(conn, """
      SELECT wh_append_event(@id, @s, @s, 'Agg.New', 'Agg.New, Tests', '{"n": 1}'::jsonb, '{}'::jsonb, '{"t":"tenant-new"}'::jsonb, @at)
      """, ("id", (Guid)TrackedGuid.New()), ("s", stream), ("at", _t0.AddDays(2)));
    var partial = (await _rowAsync(conn, stream))!;

    await _runToCompletionAsync(conn, limit: 10);
    var corrected = (await _rowAsync(conn, stream))!;

    await Assert.That(partial.FirstAppendedAt).IsEqualTo(_t0.AddDays(2)).Because("the live path saw only its own append");
    await Assert.That(corrected.StreamType).IsEqualTo("Agg.Old");
    await Assert.That(corrected.Tenant).IsEqualTo("tenant-old");
    await Assert.That(corrected.FirstAppendedAt).IsEqualTo(_t0);
    await Assert.That(corrected.LastVersion).IsEqualTo(3L);
    await Assert.That(corrected.LastAppendedBucket).IsEqualTo(new DateTimeOffset(2026, 3, 4, 8, 0, 0, TimeSpan.Zero));
    await Assert.That(corrected.ApproxBytes).IsEqualTo(await _bodyBytesAsync(conn, stream, includeEphemeral: true));
  }

  [Test]
  public async Task Backfill_WhileAnotherCallerHoldsTheCursor_ReportsBusyAndWritesNothingAsync() {
    await using var conn = await _openAsync();
    await using var other = await _openAsync();
    await _seedAsync(conn, _orderedStreams(1)[0], 1, "Agg.Busy", _t0);
    await _restartBackfillAsync(conn);

    await using var tx = await other.BeginTransactionAsync();
    await _executeAsync(other, $"SELECT 1 FROM wh_settings WHERE setting_key = '{CURSOR_KEY}' FOR UPDATE");
    var result = await _batchAsync(conn, limit: 10);
    await tx.RollbackAsync();

    await Assert.That(result).IsEqualTo(-1).Because("one caller walks the cursor at a time; the other steps aside rather than waiting");
    await Assert.That(await _ledgerCountAsync(conn)).IsEqualTo(0L);
  }

  [Test]
  public async Task Backfill_TakesEachStreamsAppendLock_SoAnAppendInFlightIsNeverMissedAsync() {
    await using var conn = await _openAsync();
    await using var appender = await _openAsync();
    var stream = _orderedStreams(1)[0];
    await _seedAsync(conn, stream, 1, "Agg.Locked", _t0);
    await _restartBackfillAsync(conn);

    await using var tx = await appender.BeginTransactionAsync();
    await _executeAsync(appender, "SELECT pg_advisory_xact_lock(hashtext('wh_event_store:' || @s::text))", ("s", stream));
    await _executeAsync(conn, "SET lock_timeout = '300ms'");
    var waited = await Assert.That(async () => await _batchAsync(conn, limit: 10)).Throws<PostgresException>();
    await tx.RollbackAsync();
    await _executeAsync(conn, "RESET lock_timeout");

    await Assert.That(waited!.SqlState).IsEqualTo(PostgresErrorCodes.LockNotAvailable)
      .Because("the batch waits for the stream's append lock, so it reads the stream after any append in flight commits");
    await Assert.That(await _ledgerCountAsync(conn)).IsEqualTo(0L).Because("a batch that failed committed nothing, cursor included");
    await Assert.That(await _settingAsync(conn, CURSOR_KEY)).IsEqualTo(Guid.Empty.ToString());
  }

  [Test]
  public async Task Backfill_WithItsCursorRowMissing_StartsFromTheBeginningAsync() {
    await using var conn = await _openAsync();
    var streams = _orderedStreams(2);
    foreach (var stream in streams) {
      await _seedAsync(conn, stream, 1, "Agg.NoCursor", _t0);
    }
    await _restartBackfillAsync(conn);
    await _executeAsync(conn, $"DELETE FROM wh_settings WHERE setting_key = '{CURSOR_KEY}'");

    await _runToCompletionAsync(conn, limit: 10);

    await Assert.That(await _ledgerCountAsync(conn)).IsEqualTo(2L)
      .Because("a missing cursor is recreated at the start: every row is recomputed whole, so starting over is safe");
    await Assert.That(await _settingAsync(conn, CURSOR_KEY)).IsEqualTo(streams[1].ToString());
  }

  [Test]
  public async Task Migration_OnAnEmptyStore_RecordsTheBackfillAsDoneAsync() {
    await using var conn = await _openAsync();

    await Assert.That(await _settingAsync(conn, VERSION_KEY)).IsEqualTo("1")
      .Because("a store with no events has nothing to backfill, so the step never runs on a new database");
    await Assert.That(await _settingAsync(conn, CURSOR_KEY)).IsEqualTo(Guid.Empty.ToString());
  }

  [Test]
  public async Task Migration_RunAgain_LeavesTheMarkerAsItFoundItAsync() {
    await using var conn = await _openAsync();
    await _seedAsync(conn, _orderedStreams(1)[0], 1, "Agg.Rerun", _t0);
    await _executeAsync(conn, $"DELETE FROM wh_settings WHERE setting_key IN ('{VERSION_KEY}', '{CURSOR_KEY}')");
    await _rerunMigrationAsync(conn);
    var pending = await _settingAsync(conn, VERSION_KEY);
    await _executeAsync(conn, $"UPDATE wh_settings SET setting_value = '1' WHERE setting_key = '{VERSION_KEY}'");
    await _rerunMigrationAsync(conn);

    await Assert.That(pending).IsEqualTo("0").Because("a store that holds events is left to the backfill");
    await Assert.That(await _settingAsync(conn, VERSION_KEY)).IsEqualTo("1")
      .Because("a re-run (the redefinition closure re-runs this file) never restarts a finished backfill");
  }

  // ===== Helpers =====

  private sealed record LedgerRow(
    string StreamType, string? Tenant, DateTimeOffset FirstAppendedAt, DateTimeOffset LastAppendedBucket,
    long LastVersion, long ApproxBytes, string State);

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>Stream ids in ascending uuid order, which is the order the backfill walks.</summary>
  private static Guid[] _orderedStreams(int count) =>
    [.. Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.New()).OrderBy(g => g.ToString(), StringComparer.Ordinal)];

  /// <summary>An event written straight into the store, as a release before the ledger stored it: no ledger row.</summary>
  private static async Task _seedAsync(
      NpgsqlConnection conn, Guid stream, int version, string aggregateType, DateTimeOffset at,
      int flags = 0, string? tenant = null) {
    var id = (Guid)TrackedGuid.New();
    await _executeAsync(conn, """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at, flags)
      VALUES (@id, @s, @s, @agg, @agg || ', Tests',
              CASE WHEN @tenant::text IS NULL THEN NULL ELSE jsonb_build_object('t', @tenant::text) END, @v, @at, @flags);
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      VALUES (@id, jsonb_build_object('v', @v, 'pad', repeat('p', @v * 11)), '{"MessageId": "m"}'::jsonb);
      """,
      ("id", id), ("s", stream), ("agg", aggregateType), ("tenant", (object?)tenant ?? DBNull.Value), ("v", version),
      ("at", at), ("flags", flags));
  }

  /// <summary>Puts the backfill back at its start, as a store upgraded from before the ledger has it.</summary>
  private static Task _restartBackfillAsync(NpgsqlConnection conn) =>
    _executeAsync(conn, $"""
      UPDATE wh_settings SET setting_value = '0' WHERE setting_key = '{VERSION_KEY}';
      UPDATE wh_settings SET setting_value = @empty WHERE setting_key = '{CURSOR_KEY}';
      """, ("empty", Guid.Empty.ToString()));

  private static async Task<int> _batchAsync(NpgsqlConnection conn, int limit) =>
    (int)await _scalarAsync(conn, "SELECT wh_backfill_stream_lifecycle_batch(@limit)", ("limit", limit));

  /// <summary>Calls the batch until it reports done; returns how many calls that took. Bounded by the test's own seed.</summary>
  private static async Task<int> _runToCompletionAsync(NpgsqlConnection conn, int limit) {
    for (var calls = 1; calls <= 100; calls++) {
      if (await _batchAsync(conn, limit) == 0) {
        return calls;
      }
    }
    throw new InvalidOperationException("the backfill did not finish in 100 batches");
  }

  private static async Task _rerunMigrationAsync(NpgsqlConnection conn) {
    var migration = new PostgresMigrationProvider().GetMigrations().Single(m => m.Name.StartsWith("260_", StringComparison.Ordinal));
    await _executeAsync(conn, migration.Sql);
  }

  private static async Task<string?> _settingAsync(NpgsqlConnection conn, string key) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT setting_value FROM wh_settings WHERE setting_key = @k";
    cmd.Parameters.AddWithValue("k", key);
    return (string?)await cmd.ExecuteScalarAsync();
  }

  private static Task<long> _ledgerCountAsync(NpgsqlConnection conn) => _scalarAsync(conn, "SELECT count(*) FROM wh_stream_lifecycle");

  private static Task<long> _bodyBytesAsync(NpgsqlConnection conn, Guid stream, bool includeEphemeral) =>
    _scalarAsync(conn, $"""
      SELECT COALESCE(SUM(pg_column_size(b.event_data) + pg_column_size(b.metadata)), 0)
      FROM wh_event_store es JOIN wh_event_body b ON b.event_id = es.event_id
      WHERE es.stream_id = @s {(includeEphemeral ? "" : $"AND es.flags & {FLAG_EPHEMERAL} = 0")}
      """, ("s", stream));

  private static async Task<LedgerRow?> _rowAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes, state
      FROM wh_stream_lifecycle WHERE stream_id = @s
      """;
    cmd.Parameters.AddWithValue("s", stream);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) {
      return null;
    }
    return new LedgerRow(
      r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetFieldValue<DateTimeOffset>(2),
      r.GetFieldValue<DateTimeOffset>(3), Convert.ToInt64(r.GetValue(4), CultureInfo.InvariantCulture), r.GetInt64(5),
      r.GetString(6));
  }

  private static async Task<List<string>> _ledgerSnapshotAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT row_to_json(l)::text FROM wh_stream_lifecycle l ORDER BY stream_id";
    var rows = new List<string>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) {
      rows.Add(r.GetString(0));
    }
    return rows;
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
