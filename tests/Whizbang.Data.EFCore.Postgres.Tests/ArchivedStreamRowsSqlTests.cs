// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The per-perspective policy for rows of archived streams (#1739, WS-3 S10) against a database the EF Core driver
/// migrated, which carries the <c>fk_perspective_cursors_event</c> RESTRICT key. When a stream is archived whole, each
/// perspective table whose perspectives declare <c>Drop</c> loses the stream's row with the row's companions, each that
/// declares <c>Archive</c> has the row moved beside the archived events, and a restore (or a new generation) brings an
/// archived row back. <c>Keep</c>, the default, changes nothing.
/// </summary>
/// <remarks>
/// The registry is seeded through <c>reconcile_perspective_registry</c> with the payload the generator emits, and the
/// policies are published through the same writer the maintenance worker uses, so both arrive the way they do in an
/// application. Names are nested on purpose: a top-level type hides every CLR-form bug.
/// </remarks>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/299_ArchivedStreamRows.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class ArchivedStreamRowsSqlTests : EFCoreTestBase {
  private const string TABLE = "wh_per_archived_rows";
  private const string SERVICE = "svc-archived-rows";
  private const string MODEL = "TestApp.Archive+OrderModel";
  private const string PERSPECTIVE_A = "TestApp.Archive+OrderPerspectiveA";
  private const string PERSPECTIVE_B = "TestApp.Archive+OrderPerspectiveB";
  private const string OTHER_PERSPECTIVE = "TestApp.Archive+OtherModelPerspective";
  private const string EVENT_TYPE = "TestApp.Archive.OrderPlaced, TestApp.Archive";
  private const string STREAM_TYPE = "TestApp.Archive.OrderPlaced";
  private const string ROW_HOLD_TABLE = "wh_perspective_row_hold";

  private static readonly DateTimeOffset _t0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

  // ===== Keep =====

  [Test]
  [Arguments(null)]
  [Arguments("Keep")]
  public async Task Keep_TheRowStaysInItsTable_WithItsCursorsAndSnapshotsParkedAsBeforeAsync(string? declared) {
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn);
    if (declared is not null) {
      await _publishAsync(conn, (PERSPECTIVE_A, declared), (PERSPECTIVE_B, declared));
    }
    var row = await _rowTextAsync(conn, stream);

    await _archiveWholeAsync(stream);

    await Assert.That(await _rowTextAsync(conn, stream)).IsEqualTo(row)
      .Because("Keep is the default: archiving a stream never removes a read model nobody chose to remove");
    await Assert.That(await _parkedRowCountAsync(conn, stream)).IsEqualTo(0L);
    await Assert.That(await _countAsync(conn, "wh_event_archive_cursors", stream, PERSPECTIVE_A)).IsEqualTo(1L)
      .Because("control: the whole-stream archive still parks the cursors, as it always has");
    await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", stream, PERSPECTIVE_A)).IsEqualTo(3L);
  }

  // ===== Drop =====

  [Test]
  public async Task Drop_RemovesTheRow_ItsParkedCursors_AndAllButTheLatestSnapshot_AndLeavesAnotherModelAloneAsync() {
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn);
    await _publishAsync(conn, (PERSPECTIVE_A, "Drop"), (PERSPECTIVE_B, "Drop"));

    await _archiveWholeAsync(stream);

    await Assert.That(await _rowExistsAsync(conn, stream)).IsFalse();
    await Assert.That(await _parkedRowCountAsync(conn, stream)).IsEqualTo(0L).Because("a dropped row is not kept anywhere");
    foreach (var name in new[] { PERSPECTIVE_A, PERSPECTIVE_B }) {
      await Assert.That(await _countAsync(conn, "wh_event_archive_cursors", stream, name)).IsEqualTo(0L)
        .Because($"{name} folds the dropped model: its parked cursor goes, so the stream's return rebuilds the row");
      await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", stream, name)).IsEqualTo(1L)
        .Because($"{name} keeps its latest snapshot, the floor the rebuild restores from");
      await Assert.That(await _snapshotVersionAsync(conn, stream, name)).IsEqualTo(30);
    }
    await Assert.That(await _countAsync(conn, "wh_event_archive_cursors", stream, OTHER_PERSPECTIVE)).IsEqualTo(1L)
      .Because("a perspective of another model on the same stream keeps its row's companions");
    await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", stream, OTHER_PERSPECTIVE)).IsEqualTo(3L);
  }

  [Test]
  public async Task Drop_ARestore_BringsTheEventsBack_ButNotTheRowAsync() {
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn);
    await _publishAsync(conn, (PERSPECTIVE_A, "Drop"));
    await _archiveWholeAsync(stream);

    var restored = await _store().RestoreAsync(stream);

    await Assert.That(restored.Status).IsEqualTo(ArchiveStatus.RESTORED);
    await Assert.That(await _rowExistsAsync(conn, stream)).IsFalse()
      .Because("the row was dropped; the stream's next event rebuilds it from the latest snapshot and the history");
    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, PERSPECTIVE_A)).IsEqualTo(0L);
  }

  // ===== Archive =====

  [Test]
  public async Task Archive_MovesTheRowBesideTheEvents_AndARestoreBringsItBackExactlyAsync() {
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn);
    await _publishAsync(conn, (PERSPECTIVE_A, "Archive"), (PERSPECTIVE_B, "Archive"));
    var row = await _rowTextAsync(conn, stream);

    await _archiveWholeAsync(stream);

    await Assert.That(await _rowExistsAsync(conn, stream)).IsFalse().Because("the row leaves its table with its stream");
    await Assert.That(await _parkedRowCountAsync(conn, stream)).IsEqualTo(1L);
    await Assert.That(await _countAsync(conn, "wh_event_archive_cursors", stream, PERSPECTIVE_A)).IsEqualTo(1L)
      .Because("the cursors stay parked as for any whole-stream archive, so the restore puts the perspective back exactly");
    await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", stream, PERSPECTIVE_A)).IsEqualTo(3L)
      .Because("archiving a row destroys nothing, so every snapshot stays");

    var restored = await _store().RestoreAsync(stream);

    await Assert.That(restored.Status).IsEqualTo(ArchiveStatus.RESTORED);
    await Assert.That(await _rowTextAsync(conn, stream)).IsEqualTo(row)
      .Because("a restore brings the archived row back exactly as it was, every column included");
    await Assert.That(await _parkedRowCountAsync(conn, stream)).IsEqualTo(0L);
  }

  [Test]
  public async Task Archive_ARowWrittenSinceTheArchive_IsKeptByTheRestore_AndTheArchivedCopyGoesAsync() {
    // A rebuild that opts in to archived data (IncludeAll) writes the row of a stream archived whole back into its table.
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn);
    await _publishAsync(conn, (PERSPECTIVE_A, "Archive"));
    await _archiveWholeAsync(stream);
    await _insertRowAsync(conn, stream, version: 7);

    _ = await _store().RestoreAsync(stream);

    await Assert.That(await _rowVersionAsync(conn, stream)).IsEqualTo(7)
      .Because("the row written since is the newer one, as a cursor a perspective made since is kept over its parked copy");
    await Assert.That(await _parkedRowCountAsync(conn, stream)).IsEqualTo(0L);
  }

  [Test]
  public async Task Archive_ARowWhoseTableIsGoneByTheRestore_IsDiscarded_AndTheRestoreStillRunsAsync() {
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn);
    await _publishAsync(conn, (PERSPECTIVE_A, "Archive"));
    await _archiveWholeAsync(stream);
    await _executeAsync(conn, $"DROP TABLE {TABLE}");

    var restored = await _store().RestoreAsync(stream);

    await Assert.That(restored.Status).IsEqualTo(ArchiveStatus.RESTORED)
      .Because("a model that no longer has a table has nowhere to take its row; that must not keep the events archived");
    await Assert.That(await _parkedRowCountAsync(conn, stream)).IsEqualTo(0L);
  }

  [Test]
  public async Task Archive_ANewGeneration_BringsTheArchivedRowBackAsync() {
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn);
    await _publishAsync(conn, (PERSPECTIVE_A, "Archive"));
    var row = await _rowTextAsync(conn, stream);
    await _executeAsync(conn, "SELECT wh_sync_stream_type_policies(ARRAY[@t], ARRAY['NewGeneration'])", ("t", STREAM_TYPE));
    await _archiveWholeAsync(stream);

    _ = await _emitAsync(conn, _t0.AddMinutes(5), stream);

    await Assert.That(await _rowTextAsync(conn, stream)).IsEqualTo(row)
      .Because("a new generation continues the stream from where its perspectives stopped, so their row is live again");
    await Assert.That(await _parkedRowCountAsync(conn, stream)).IsEqualTo(0L);
  }

  // ===== What else decides =====

  [Test]
  [Arguments("Drop", "Archive", "Archive")]
  [Arguments("Drop", "Keep", "Keep")]
  [Arguments("Archive", "Keep", "Keep")]
  [Arguments("Drop", null, "Drop")]
  public async Task PerspectivesOfOneModelThatDisagree_TheLeastDestructiveWinsAsync(string a, string? b, string expected) {
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn);
    if (b is null) {
      await _publishAsync(conn, (PERSPECTIVE_A, a));
    } else {
      await _publishAsync(conn, (PERSPECTIVE_A, a), (PERSPECTIVE_B, b));
    }

    await _archiveWholeAsync(stream);

    var outcome = (await _rowExistsAsync(conn, stream), await _parkedRowCountAsync(conn, stream)) switch {
      (true, 0) => "Keep",
      (false, 1) => "Archive",
      _ => "Drop",
    };
    await Assert.That(outcome).IsEqualTo(expected)
      .Because("the row belongs to the model, not to one perspective: no perspective loses a row another asked to keep, and a perspective that declares nothing does not vote");
  }

  [Test]
  public async Task AHandEditedSetting_ThatNamesNoPolicy_KeepsTheRowAsync() {
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn);
    await _publishRawAsync(conn, $"garbage\n{PERSPECTIVE_A}=Shred\n=Drop");

    await _archiveWholeAsync(stream);

    await Assert.That(await _rowExistsAsync(conn, stream)).IsTrue()
      .Because("a line that names no policy is not a policy; reading it never fails the archive");
  }

  [Test]
  [Arguments("Drop")]
  [Arguments("Archive")]
  public async Task AnActiveRowHold_KeepsTheRowInItsTable_AndALapsedOneDoesNotAsync(string policy) {
    await using var conn = await _openAsync();
    var held = await _arrangeStreamAsync(conn);
    var lapsed = await _arrangeStreamAsync(conn, createTable: false);
    await _publishAsync(conn, (PERSPECTIVE_A, policy));
    await _holdAsync(conn, held, TimeSpan.FromDays(1));
    await _holdAsync(conn, lapsed, TimeSpan.FromDays(-1));

    await _archiveWholeAsync(held);
    await _archiveWholeAsync(lapsed);

    await Assert.That(await _rowExistsAsync(conn, held)).IsTrue()
      .Because("a hold is a recorded decision to keep that row where it is until its time passes");
    await Assert.That(await _parkedRowCountAsync(conn, held)).IsEqualTo(0L);
    await Assert.That(await _rowExistsAsync(conn, lapsed)).IsFalse().Because("control: a lapsed hold holds nothing");
  }

  [Test]
  public async Task APrefixArchive_LeavesTheRowWhereItIs_WhateverThePolicyAsync() {
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn, snapshots: false);
    await _publishAsync(conn, (PERSPECTIVE_A, "Drop"), (PERSPECTIVE_B, "Drop"));

    var result = await _store().WriteAsync(ArchiveBatch.Prefix(stream, throughVersion: 1));

    await Assert.That(result.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await _rowExistsAsync(conn, stream)).IsTrue()
      .Because("a stream archived through a prefix is still alive: its row is its current read model");
  }

  [Test]
  public async Task DeletingTheArchive_DeletesTheArchivedRowWithItAsync() {
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn);
    await _publishAsync(conn, (PERSPECTIVE_A, "Archive"));
    await _archiveWholeAsync(stream);

    _ = await _store().DeleteAsync(stream);

    await Assert.That(await _parkedRowCountAsync(conn, stream)).IsEqualTo(0L)
      .Because("the archived row is part of the archive, and nothing can bring it back once the events are gone");
  }

  [Test]
  public async Task PurgingTheStream_RemovesTheArchivedRowAsync() {
    await using var conn = await _openAsync();
    var stream = await _arrangeStreamAsync(conn);
    await _publishAsync(conn, (PERSPECTIVE_A, "Archive"));
    await _archiveWholeAsync(stream);

    await _executeAsync(conn, "SELECT count(*) FROM wh_purge_streams(ARRAY[@s]::uuid[], FALSE, gen_random_uuid(), 0, 'operator-1', 'test')", ("s", stream));

    await Assert.That(await _parkedRowCountAsync(conn, stream)).IsEqualTo(0L)
      .Because("a purged stream never existed: its archived row goes with its tombstone");
  }

  // ===== Arrange =====

  private PostgresTableArchiveStore _store() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private async Task _archiveWholeAsync(Guid stream) {
    var result = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));
    await Assert.That(result.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>
  /// A stream of two events, the perspective table and its registry row (two perspectives fold the model), the stream's
  /// row, a cursor on the last event and three snapshots for each perspective of the model and for one of another model.
  /// </summary>
  private static async Task<Guid> _arrangeStreamAsync(NpgsqlConnection conn, bool createTable = true, bool snapshots = true) {
    if (createTable) {
      await _executeAsync(conn, $"""
        CREATE TABLE IF NOT EXISTS {TABLE} (
          id UUID NOT NULL PRIMARY KEY,
          data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
          created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
          sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
          expires_at TIMESTAMPTZ, version INTEGER NOT NULL, order_total NUMERIC(12, 2))
        """);
      var entries = new[] { PERSPECTIVE_A, PERSPECTIVE_B }.Select(p =>
        $$"""{"ClrTypeName":"{{MODEL}}","TableName":"{{TABLE}}","SchemaJson":{},"SchemaHash":"h","ServiceName":"{{SERVICE}}","PerspectiveName":"{{p}}"}""");
      await _executeAsync(conn, "SELECT * FROM reconcile_perspective_registry(@p::jsonb, @svc)",
        ("p", "[" + string.Join(",", entries) + "]"), ("svc", SERVICE));
    }

    var stream = Guid.CreateVersion7();
    _ = await _emitAsync(conn, _t0, stream);
    var ids = await _emitAsync(conn, _t0.AddMinutes(1), stream);
    await _insertRowAsync(conn, stream, version: 2);
    foreach (var name in new[] { PERSPECTIVE_A, PERSPECTIVE_B, OTHER_PERSPECTIVE }) {
      await _executeAsync(conn,
        "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@s, @n, @e, 0, NOW())",
        ("s", stream), ("n", name), ("e", ids[0]));
      if (!snapshots) {
        continue;
      }
      for (var i = 1; i <= 3; i++) {
        await _executeAsync(conn,
          "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) " +
          "VALUES (@s, @n, @e, '{}'::jsonb, @q, @v)",
          ("s", stream), ("n", name), ("e", i == 3 ? ids[0] : Guid.CreateVersion7()), ("q", (long)i), ("v", i * 10));
      }
    }
    return stream;
  }

  private static Task _insertRowAsync(NpgsqlConnection conn, Guid stream, int version) =>
    _executeAsync(conn, $$"""
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, sys_created_at, version, order_total)
      VALUES (@s, '{"total": 12.5, "lines": [1, 2]}'::jsonb, '{"m": 1}'::jsonb, '{"t": "tenant-a"}'::jsonb,
              '2026-11-02T09:00:00Z', '2026-11-02T09:01:00.123456Z', NULL, @v, 12.50)
      ON CONFLICT (id) DO UPDATE SET version = EXCLUDED.version
      """, ("s", stream), ("v", version));

  private static Task _publishAsync(NpgsqlConnection conn, params (string Perspective, string Policy)[] policies) =>
    RetentionSettingsWriter.WriteAsync(conn, "wh_settings", [ArchivedStreamRowPolicies.SettingFor([
      .. policies.Select(p => new KeyValuePair<string, ArchivedStreamRowPolicy>(
        p.Perspective, Enum.Parse<ArchivedStreamRowPolicy>(p.Policy)))])], CancellationToken.None);

  private static Task _publishRawAsync(NpgsqlConnection conn, string value) =>
    RetentionSettingsWriter.WriteAsync(conn, "wh_settings",
      [new RetentionSetting(ArchivedStreamRowPolicies.SETTING_KEY, value, "string", ArchivedStreamRowsOptions.SECTION)], CancellationToken.None);

  private static Task _holdAsync(NpgsqlConnection conn, Guid stream, TimeSpan from) =>
    _executeAsync(conn, $"INSERT INTO {ROW_HOLD_TABLE} (table_name, row_id, hold_until) VALUES (@t, @s, NOW() + @d)",
      ("t", TABLE), ("s", stream), ("d", from));

  // One event on the stream through the outbox emit chain, as store_outbox_messages runs it.
  private static async Task<Guid[]> _emitAsync(NpgsqlConnection conn, DateTimeOffset now, Guid stream) {
    var instanceId = Guid.CreateVersion7();
    Guid[] ids = [Guid.CreateVersion7()];
    await _executeAsync(conn, """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
      VALUES
        (@id, 'archive-topic', @type, 'env', jsonb_build_object('p', jsonb_build_object('n', 1)),
         '{}'::jsonb, NULL, 1, 0, @inst, @lease, 0, @sid, true, 0, @now)
      """,
      ("id", ids[0]), ("type", EVENT_TYPE), ("inst", instanceId), ("lease", now.AddMinutes(5)), ("sid", stream), ("now", now));
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", now.AddMinutes(5));
    emit.Parameters.AddWithValue(nameof(now), now);
    _ = await emit.ExecuteScalarAsync();
    return ids;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<bool> _rowExistsAsync(NpgsqlConnection conn, Guid stream) =>
    await _scalarAsync(conn, $"SELECT count(*) FROM {TABLE} WHERE id = @s", ("s", stream)) > 0;

  private static async Task<string?> _rowTextAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT to_jsonb(t)::text FROM {TABLE} t WHERE t.id = @s";
    cmd.Parameters.AddWithValue("s", stream);
    return (string?)await cmd.ExecuteScalarAsync();
  }

  private static async Task<int> _rowVersionAsync(NpgsqlConnection conn, Guid stream) =>
    (int)await _scalarAsync(conn, $"SELECT version FROM {TABLE} WHERE id = @s", ("s", stream));

  private static Task<long> _parkedRowCountAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync(conn, "SELECT count(*) FROM wh_event_archive_rows WHERE stream_id = @s", ("s", stream));

  private static Task<long> _countAsync(NpgsqlConnection conn, string table, Guid stream, string perspective) =>
    _scalarAsync(conn, $"SELECT count(*) FROM {table} WHERE stream_id = @s AND perspective_name = @n", ("s", stream), ("n", perspective));

  private static async Task<int?> _snapshotVersionAsync(NpgsqlConnection conn, Guid stream, string perspective) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT snapshot_version FROM wh_perspective_snapshots WHERE stream_id = @s AND perspective_name = @n";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("n", perspective);
    return await cmd.ExecuteScalarAsync() is int v ? v : null;
  }
}
