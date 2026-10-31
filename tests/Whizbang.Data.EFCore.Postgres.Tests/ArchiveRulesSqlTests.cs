// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The archive rules read-through is built on (#1738, WS-3 S9, migration 271): a prefix archive always removes at least
/// version 1, so an archived stream always shows a gap at its start; it never takes the newest snapshot any perspective
/// holds for the stream; and every archive marks the snapshots it covers with how far it reaches, which a restore clears.
/// </summary>
/// <docs>fundamentals/events/archive-store#the-archive-rules</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/271_ArchiveReadThroughAndAppendPolicy.sql</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class ArchiveRulesSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Probe.Rules.Happened, Probe.Rules";

  private static readonly DateTimeOffset _t0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

  // ===== A prefix always removes version 1 =====

  [Test]
  public async Task Prefix_ThroughAVersionBelowOne_IsRefused_OnAStreamThatStartsAtOneAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _appendDirectlyAsync(conn, stream, count: 3);

    var refused = await _archiveSqlAsync(conn, stream, throughVersion: 0);

    await Assert.That(refused).IsEqualTo("not_past_first_version")
      .Because("a prefix through version 0 archives nothing of a stream that starts at 1, and keeps version 1 hot");
    await Assert.That(await _hotVersionsAsync(conn, stream)).IsEquivalentTo([1L, 2L, 3L]);

    var archived = await _store().WriteAsync(ArchiveBatch.Prefix(stream, 1));

    await Assert.That(archived.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(archived.EventsArchived).IsEqualTo(1L);
    await Assert.That(await _hotVersionsAsync(conn, stream)).IsEquivalentTo([2L, 3L]);
  }

  [Test]
  public async Task Prefix_ThroughAVersionBelowOne_IsRefused_ThroughOneIsArchivedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    // A stream a direct append created before new streams started at 1 (#1913) is numbered from 0.
    await _appendDirectlyAsync(conn, stream, count: 3);
    await _executeAsync(conn, """
      UPDATE wh_event_store SET version = version + 1000 WHERE stream_id = @s;
      UPDATE wh_event_store SET version = version - 1001 WHERE stream_id = @s;
      UPDATE wh_stream_lifecycle SET last_version = last_version - 1 WHERE stream_id = @s;
      """, ("s", stream));

    var refused = await _archiveSqlAsync(conn, stream, throughVersion: 0);

    await Assert.That(refused).IsEqualTo("not_past_first_version")
      .Because("a prefix that keeps version 1 hot would leave a read from the start no gap to see the archive by");
    await Assert.That(await _hotVersionsAsync(conn, stream)).IsEquivalentTo([0L, 1L, 2L]);

    var archived = await _store().WriteAsync(ArchiveBatch.Prefix(stream, 1));

    await Assert.That(archived.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(archived.EventsArchived).IsEqualTo(2L).Because("through version 1 takes versions 0 and 1");
    await Assert.That(await _hotVersionsAsync(conn, stream)).IsEquivalentTo([2L]);
  }

  // ===== A prefix never takes a perspective's newest snapshot =====

  [Test]
  public async Task Prefix_ReachingAPerspectivesNewestSnapshot_IsRefused_StoppingBelowItIsArchivedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _streamAsync(conn, stream, 5);
    await _snapshotAsync(conn, stream, "Perspective.A", ids, version: 3);
    await _snapshotAsync(conn, stream, "Perspective.B", ids, version: 5);
    // A snapshot that records no version cannot be placed in the stream, so it never blocks a cut.
    await _snapshotAsync(conn, stream, "Perspective.C", ids, version: null);

    var atTheSnapshot = await _store().WriteAsync(ArchiveBatch.Prefix(stream, 3));

    await Assert.That(atTheSnapshot.Status).IsEqualTo(ArchiveStatus.SNAPSHOT_IN_RANGE)
      .Because("Perspective.A's newest snapshot is at version 3; archiving it would leave a rewind to it nothing to replay");
    await Assert.That(atTheSnapshot.EventsArchived).IsEqualTo(0L);
    await Assert.That(await _hotVersionsAsync(conn, stream)).IsEquivalentTo([1L, 2L, 3L, 4L, 5L])
      .Because("refused, not shortened: the caller gets the archive it asked for or none");

    var belowIt = await _store().WriteAsync(ArchiveBatch.Prefix(stream, 2));

    await Assert.That(belowIt.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await _hotVersionsAsync(conn, stream)).IsEquivalentTo([3L, 4L, 5L])
      .Because("the snapshot's event and everything after it stay hot");
  }

  [Test]
  public async Task Prefix_AnOlderSnapshotInTheRange_DoesNotBlockTheCut_OnlyTheNewestPerPerspectiveDoesAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _streamAsync(conn, stream, 5);
    await _snapshotAsync(conn, stream, "Perspective.A", ids, version: 2);
    await _snapshotAsync(conn, stream, "Perspective.A", ids, version: 4);

    var result = await _store().WriteAsync(ArchiveBatch.Prefix(stream, 3));

    await Assert.That(result.Status).IsEqualTo(ArchiveStatus.ARCHIVED)
      .Because("Perspective.A's newest snapshot (version 4) is above the cut; its older one may go");
  }

  // ===== Snapshots record how far an archive reaches =====

  [Test]
  public async Task Prefix_MarksTheSnapshotsAtOrBelowItsCut_AndLeavesTheNewestUnmarkedAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _streamAsync(conn, stream, 5);
    await _snapshotAsync(conn, stream, "Perspective.A", ids, version: 3);
    await _snapshotAsync(conn, stream, "Perspective.A", ids, version: 5);
    await _snapshotAsync(conn, stream, "Perspective.B", ids, version: 4);

    var result = await _store().WriteAsync(ArchiveBatch.Prefix(stream, 3));

    await Assert.That(result.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await _marksAsync(conn, stream)).IsEquivalentTo([
      "Perspective.A|3|3", "Perspective.A|5|<none>", "Perspective.B|4|<none>",
    ]).Because("a rewind from the version 3 snapshot replays events the archive now holds; the newer ones replay only hot events");
  }

  [Test]
  public async Task WholeStream_MarksEverySnapshotWithItsLastVersionAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _streamAsync(conn, stream, 4);
    await _snapshotAsync(conn, stream, "Perspective.A", ids, version: 2);
    await _snapshotAsync(conn, stream, "Perspective.B", ids, version: 4);
    await _snapshotAsync(conn, stream, "Perspective.C", ids, version: null);

    var result = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(result.Status).IsEqualTo(ArchiveStatus.ARCHIVED)
      .Because("a whole-stream archive is the stream's end; the snapshot rule applies to a prefix, which leaves a hot tail");
    await Assert.That(await _marksAsync(conn, stream)).IsEquivalentTo([
      "Perspective.A|2|4", "Perspective.B|4|4", "Perspective.C|<none>|4",
    ]);
  }

  [Test]
  public async Task Restore_ClearsEverySnapshotsArchiveMarkAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var ids = await _streamAsync(conn, stream, 3);
    await _snapshotAsync(conn, stream, "Perspective.A", ids, version: 3);
    var other = (Guid)TrackedGuid.New();
    var otherIds = await _streamAsync(conn, other, 2);
    await _snapshotAsync(conn, other, "Perspective.A", otherIds, version: 2);
    _ = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));
    _ = await _store().WriteAsync(ArchiveBatch.WholeStream(other));

    var restored = await _store().RestoreAsync(stream);

    await Assert.That(restored.Status).IsEqualTo(ArchiveStatus.RESTORED);
    await Assert.That(await _marksAsync(conn, stream)).IsEquivalentTo(["Perspective.A|3|<none>"])
      .Because("everything the snapshot needs is hot again");
    await Assert.That(await _marksAsync(conn, other)).IsEquivalentTo(["Perspective.A|2|2"])
      .Because("the control: a stream still archived keeps its mark");
  }

  // =================================================================================================

  private PostgresTableArchiveStore _store() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _appendDirectlyAsync(NpgsqlConnection conn, Guid stream, int count) {
    for (var i = 0; i < count; i++) {
      await _executeAsync(conn,
        "SELECT wh_append_event(gen_random_uuid(), @s, @s, 'Agg', 'T', '{}'::jsonb, '{}'::jsonb, NULL::jsonb, NOW())",
        ("s", stream));
    }
  }

  private static async Task<string> _archiveSqlAsync(NpgsqlConnection conn, Guid stream, long throughVersion) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT archive_status FROM wh_archive_stream(@s, @v, 'postgres-table://wh_event_archive?codec=none&format=1')";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("v", throughVersion);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  // Versions 1..count through the outbox emit chain; returns the event ids, index 0 at version 1.
  private static async Task<Guid[]> _streamAsync(NpgsqlConnection conn, Guid stream, int count) {
    Guid[] ids = [.. Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.New()).Order()];
    var instanceId = (Guid)TrackedGuid.New();
    foreach (var id in ids) {
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'rules-topic', @type, 'env', jsonb_build_object('p', jsonb_build_object('n', 1)),
           '{}'::jsonb, NULL, 1, 0, @inst, @lease, 0, @sid, true, 0, @now)
        """,
        ("id", id), ("type", EVENT_TYPE), ("inst", instanceId), ("lease", _t0.AddMinutes(5)), ("sid", stream), ("now", _t0));
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", _t0.AddMinutes(5));
    emit.Parameters.AddWithValue("now", _t0);
    _ = await emit.ExecuteScalarAsync();
    return ids;
  }

  private static Task _snapshotAsync(NpgsqlConnection conn, Guid stream, string perspective, Guid[] ids, int? version) =>
    _executeAsync(conn, """
      INSERT INTO wh_perspective_snapshots
        (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version)
      VALUES (@s, @p, @e, '{"n": 1}'::jsonb,
              (SELECT COALESCE(MAX(sequence_number), 0) + 1 FROM wh_perspective_snapshots WHERE stream_id = @s AND perspective_name = @p),
              @v)
      """,
      ("s", stream), ("p", perspective), ("e", version is { } v ? ids[v - 1] : ids[^1]),
      ("v", version is { } at ? (object)at : DBNull.Value));

  // Each snapshot as perspective|version|archived_through_version.
  private static async Task<List<string>> _marksAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      "SELECT concat_ws('|', perspective_name, COALESCE(snapshot_version::text, '<none>'), COALESCE(archived_through_version::text, '<none>')) " +
      "FROM wh_perspective_snapshots WHERE stream_id = @s ORDER BY 1";
    cmd.Parameters.AddWithValue("s", stream);
    var rows = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetString(0));
    }
    return rows;
  }

  private static async Task<List<long>> _hotVersionsAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT version FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    cmd.Parameters.AddWithValue("s", stream);
    var rows = new List<long>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetInt64(0));
    }
    return rows;
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
