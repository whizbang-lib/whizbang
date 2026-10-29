// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The Postgres-table archive store (#1736, WS-3 S7) against a database the EF Core driver migrated, which carries the
/// <c>fk_perspective_cursors_event</c> RESTRICT key. Archive then read returns the events byte-identical in version
/// order; a failure between the copy and the truncate leaves the stream fully hot; every archive bumps the origin
/// generation; a whole-stream archive leaves a tombstone that refuses any append at or below its last version; a restore
/// puts the stream back exactly (versions, commit positions, digests, cursors).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/262_ArchiveStoreKeepsATombstone.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresTableArchiveStore.cs</code-under-test>
/// <docs>fundamentals/events/archive-store</docs>
[Category("Integration")]
[Category("Shard1")]
public class ArchiveStoreSqlTests : EFCoreTestBase {
  private const string LEDGER = "Whizbang.Tests.LedgerEntry";
  private const string PERSPECTIVE = "Whizbang.Tests.LedgerPerspective";
  private const string ARCHIVED_VERSION_REFUSED = "WHA01";

  // ── Arrange helpers ────────────────────────────────────────────────────────────────────────────────

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private PostgresTableArchiveStore _store(ILogger<PostgresTableArchiveStore>? logger = null) =>
    new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, logger: logger);

  // One Sourced event through the emit chain; consecutive calls on a stream get versions 1, 2, 3...
  private static async Task _commitAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType = LEDGER,
      string scope = "null", int flags = 0) {
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{eventType}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42, "Lines": [3, 1, 2], "Note": "zßé"}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {"z": 1, "a": "two"}, "Scope": {{scope}}, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": {{flags}}
        }]
      }
      """;
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", request);
    _ = await call.ExecuteScalarAsync();
  }

  private static async Task<Guid[]> _streamAsync(NpgsqlConnection connection, Guid streamId, int count) {
    var ids = new Guid[count];
    for (var i = 0; i < count; i++) {
      ids[i] = Guid.CreateVersion7();
    }
    Array.Sort(ids);
    for (var i = 0; i < count; i++) {
      // The middle event carries a tenant scope, so the scope text round-trip is checked on a real value.
      await _commitAsync(connection, ids[i], streamId, scope: i == 1 ? """{"t":"tenant-a","u":"user-1"}""" : "null");
    }
    return ids;
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection connection, string sql, Guid id) {
    await using var c = connection.CreateCommand();
    c.CommandText = sql;
    c.Parameters.AddWithValue(nameof(id), id);
    return Convert.ToInt64(await c.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _executeAsync(NpgsqlConnection connection, string sql, Guid? id = null) {
    await using var c = connection.CreateCommand();
    c.CommandText = sql;
    if (id is { } value) {
      c.Parameters.AddWithValue(nameof(id), value);
    }
    await c.ExecuteNonQueryAsync();
  }

  private static Task<long> _hotCountAsync(NpgsqlConnection connection, Guid streamId) =>
    _scalarAsync(connection, "SELECT count(*) FROM wh_event_store WHERE stream_id = @id", streamId);

  private static Task<long> _archivedCountAsync(NpgsqlConnection connection, Guid streamId) =>
    _scalarAsync(connection, "SELECT count(*) FROM wh_event_archive WHERE stream_id = @id", streamId);

  private static async Task<long> _generationAsync(NpgsqlConnection connection) {
    await using var c = connection.CreateCommand();
    c.CommandText = "SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_generation'";
    return (long)(await c.ExecuteScalarAsync())!;
  }

  // The hot events as a reader sees them: identity, version and the stored text of body, metadata and scope.
  private static async Task<List<string>> _hotRowsAsync(NpgsqlConnection connection, Guid streamId) {
    await using var c = connection.CreateCommand();
    c.CommandText =
      "SELECT es.event_id::text || '|' || es.version || '|' || es.event_type || '|' || COALESCE(eb.event_data::text, '<null>') " +
      "  || '|' || COALESCE(eb.metadata::text, '<null>') || '|' || COALESCE(es.scope::text, '<null>') " +
      "FROM wh_event_store es LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id WHERE es.stream_id = @id ORDER BY es.version";
    c.Parameters.AddWithValue("id", streamId);
    return await _readStringsAsync(c);
  }

  // Every stored column of the hot rows, positions included, for an exact before/after comparison.
  private static async Task<List<string>> _fullHotRowsAsync(NpgsqlConnection connection, Guid streamId) {
    await using var c = connection.CreateCommand();
    c.CommandText =
      "SELECT concat_ws('|', es.event_id, es.stream_id, es.aggregate_id, es.aggregate_type, es.event_type, es.scope::text, " +
      "  es.version, es.created_at, es.flags, es.commit_sequence, es.origin_service_id, es.origin_commit_sequence, " +
      "  es.insert_order, eb.event_data::text, eb.metadata::text) " +
      "FROM wh_event_store es LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id WHERE es.stream_id = @id ORDER BY es.version";
    c.Parameters.AddWithValue("id", streamId);
    return await _readStringsAsync(c);
  }

  private static async Task<List<string>> _digestRowsAsync(NpgsqlConnection connection, Guid streamId) {
    await using var c = connection.CreateCommand();
    c.CommandText = "SELECT concat_ws('|', origin_service_id, scope_tenant, event_type, digest_lo, digest_hi, event_count) " +
      "FROM wh_stream_digests WHERE stream_id = @id ORDER BY 1";
    c.Parameters.AddWithValue("id", streamId);
    return await _readStringsAsync(c);
  }

  private static async Task<List<string>> _readStringsAsync(NpgsqlCommand command) {
    var rows = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetString(0));
    }
    return rows;
  }

  private static async Task<(string? State, long? LastVersion, string? Locator, long? Generation)?> _tombstoneAsync(
      NpgsqlConnection connection, Guid streamId) {
    await using var c = connection.CreateCommand();
    c.CommandText = "SELECT state, last_version, archive_locator, origin_generation FROM wh_stream_lifecycle WHERE stream_id = @id";
    c.Parameters.AddWithValue("id", streamId);
    await using var reader = await c.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return null;
    }
    return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1),
      reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3));
  }

  private static async Task<List<ArchivedEvent>> _readAllAsync(IArchiveStore store, Guid streamId, long fromVersion = 0) {
    var events = new List<ArchivedEvent>();
    await foreach (var archived in store.ReadAsync(streamId, fromVersion)) {
      events.Add(archived);
    }
    return events;
  }

  private static string _asHotRow(ArchivedEvent e) =>
    $"{e.EventId}|{e.Version}|{e.EventType}|{e.EventDataJson ?? "<null>"}|{e.MetadataJson ?? "<null>"}|{e.ScopeJson ?? "<null>"}";

  // ── Archive then read ─────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Write_ThenRead_ReturnsTheEventsByteIdenticalInVersionOrderAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    var ids = await _streamAsync(connection, stream, 3);
    var hot = await _hotRowsAsync(connection, stream);
    var store = _store();

    var written = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    var read = await _readAllAsync(store, stream);

    await Assert.That(written.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(written.EventsArchived).IsEqualTo(3L);
    await Assert.That(await _hotCountAsync(connection, stream)).IsEqualTo(0L).Because("the archive removes the hot copy");
    await Assert.That(read.ConvertAll(_asHotRow)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a read returns each event exactly as the hot store held it (body, metadata and scope text), in version order");
    await Assert.That(read.ConvertAll(e => e.EventId)).IsEquivalentTo(ids, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(read[1].ScopeJson).IsNotNull().Because("the scope is part of the event, and the middle one had one");
    await Assert.That(read.All(e => e.AggregateId == stream && e.Flags == 0 && e.CreatedAt is not null && e.AggregateType is not null)).IsTrue()
      .Because("every pointer column a rebuild needs comes back with the event");

    var tail = await _readAllAsync(store, stream, fromVersion: 2);
    await Assert.That(tail.ConvertAll(e => e.Version)).IsEquivalentTo([2L, 3L], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("fromVersion is inclusive and the read starts there");
  }

  [Test]
  public async Task Read_ARowArchivedBeforeTheStoreKeptEveryColumn_ReadsTheMissingOnesAsNullAsync() {
    // Rows archived by migration 085's copy have no flags; a reaped body was archived as NULL.
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    await _executeAsync(connection,
      "INSERT INTO wh_event_archive (event_id, stream_id, event_type, version) VALUES (gen_random_uuid(), @id, 'Old.Type', 4)", stream);

    var read = await _readAllAsync(_store(), stream);

    await Assert.That(read).Count().IsEqualTo(1);
    await Assert.That(read[0].EventDataJson).IsNull();
    await Assert.That(read[0].MetadataJson).IsNull();
    await Assert.That(read[0].ScopeJson).IsNull();
    await Assert.That(read[0].AggregateId).IsNull();
    await Assert.That(read[0].AggregateType).IsNull();
    await Assert.That(read[0].CreatedAt).IsNull();
    await Assert.That(read[0].Flags).IsNull();
  }

  // ── All or nothing ────────────────────────────────────────────────────────────────────────────────

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Write_AFailureBetweenCopyAndTruncate_LeavesTheStreamFullyHotAsync(bool wholeStream) {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    var ids = await _streamAsync(connection, stream, 3);
    await _executeAsync(connection,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@id, 'P', " +
      $"'{ids[2]}', 0, NOW())", stream);
    var before = await _fullHotRowsAsync(connection, stream);
    var ledger = await _ledgerRowAsync(connection, stream);
    var generation = await _generationAsync(connection);
    // The copy runs first, then the truncate deletes; this trigger fails the delete of the stream's pointers.
    await _executeAsync(connection, """
      CREATE OR REPLACE FUNCTION public.test_fail_truncate() RETURNS TRIGGER AS $f$
      BEGIN RAISE EXCEPTION 'injected failure between copy and truncate'; END $f$ LANGUAGE plpgsql;
      """);
    await _executeAsync(connection,
      $"CREATE TRIGGER test_fail_truncate BEFORE DELETE ON wh_event_store FOR EACH ROW WHEN (OLD.stream_id = '{stream}') " +
      "EXECUTE FUNCTION public.test_fail_truncate()");

    var batch = wholeStream ? ArchiveBatch.WholeStream(stream) : ArchiveBatch.Prefix(stream, 2);
    await Assert.That(async () => await _store().WriteAsync(batch)).Throws<PostgresException>()
      .Because("the failure surfaces to the caller");

    await Assert.That(await _fullHotRowsAsync(connection, stream)).IsEquivalentTo(before, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("every event, body and position is still hot");
    await Assert.That(await _archivedCountAsync(connection, stream)).IsEqualTo(0L)
      .Because("the copy rolled back with the truncate: nothing is half-archived");
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @id", stream)).IsEqualTo(1L);
    await Assert.That(await _ledgerRowAsync(connection, stream)).IsEqualTo(ledger)
      .Because("the stream's ledger row is as it was: no tombstone, no locator, the same bytes");
    await Assert.That(await _generationAsync(connection)).IsEqualTo(generation).Because("nothing moved, so integrity has nothing to re-verify");
  }

  [Test]
  public async Task Write_ARefusedPrefix_KeepsTheTruncatesStatus_AndCopiesNothingAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 3);
    var generation = await _generationAsync(connection);

    // Through the last version: no event would survive to carry the stream forward.
    var refused = await _store().WriteAsync(ArchiveBatch.Prefix(stream, 3));

    await Assert.That(refused).IsEqualTo(new ArchiveWriteResult("no_carry_forward", 0, null, null, generation));
    await Assert.That(await _archivedCountAsync(connection, stream)).IsEqualTo(0L)
      .Because("the copy made before the truncate refused is undone with it");
    await Assert.That(await _hotCountAsync(connection, stream)).IsEqualTo(3L);

    // The control: through version 2 the same stream archives.
    var archived = await _store().WriteAsync(ArchiveBatch.Prefix(stream, 2));
    await Assert.That(archived.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(archived.EventsArchived).IsEqualTo(2L);
    await Assert.That(archived.LastVersion).IsNull().Because("a prefix leaves the stream active, so there is no tombstone floor");
    await Assert.That(await _archivedCountAsync(connection, stream)).IsEqualTo(2L);
    await Assert.That(await _tombstoneAsync(connection, stream)).IsEqualTo(("Active", (long?)3, archived.Locator!.ToString(), (long?)archived.OriginGeneration))
      .Because("a prefix records where it went on the stream's row and leaves the stream active");
  }

  // ── Whole stream: tombstone and generation ────────────────────────────────────────────────────────

  [Test]
  public async Task Write_WholeStream_LeavesATombstone_AndBumpsTheOriginGenerationAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    var ids = await _streamAsync(connection, stream, 3);
    await _executeAsync(connection,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@id, 'P', " +
      $"'{ids[2]}', 0, NOW())", stream);
    await _executeAsync(connection,
      "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) " +
      $"VALUES (@id, 'P', '{ids[2]}', '{{}}'::jsonb, 1, 3)", stream);
    var generation = await _generationAsync(connection);
    var store = _store();

    var written = await store.WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(written).IsEqualTo(new ArchiveWriteResult(ArchiveStatus.ARCHIVED, 3, 3, store.Locator, generation + 1));
    await Assert.That(await _generationAsync(connection)).IsEqualTo(generation + 1)
      .Because("every archive bumps the origin generation once, so integrity re-verifies instead of alarming");
    await Assert.That(await _tombstoneAsync(connection, stream))
      .IsEqualTo(("Archived", (long?)3, "postgres-table://wh_event_archive?codec=none&format=1", (long?)(generation + 1)));
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @id", stream)).IsEqualTo(0L)
      .Because("no event is left for a cursor to point at");
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_event_archive_cursors WHERE stream_id = @id", stream)).IsEqualTo(1L)
      .Because("the cursor is parked for a restore");
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_perspective_snapshots WHERE stream_id = @id", stream)).IsEqualTo(1L)
      .Because("a snapshot still describes the archived stream");
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_stream_digests WHERE stream_id = @id", stream)).IsEqualTo(0L)
      .Because("no event of the stream is hot, so none of its digest buckets is left");
  }

  [Test]
  public async Task Write_WholeStream_UnprocessedWorkOrAnActiveHold_RefusesAndMovesNothingAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    await _executeAsync(connection,
      "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
      $"VALUES (@id, '{LEDGER}', 'perspective', '{PERSPECTIVE}', 'test-service', '{LEDGER}', NOW(), NOW())", Guid.CreateVersion7());
    var stream = Guid.CreateVersion7();
    var ids = await _streamAsync(connection, stream, 2);
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_perspective_events WHERE stream_id = @id AND processed_at IS NULL", stream))
      .IsEqualTo(2L).Because("the seed must leave owed work, or the gate proves nothing");
    var store = _store();

    await Assert.That((await store.WriteAsync(ArchiveBatch.WholeStream(stream))).Status).IsEqualTo(ArchiveStatus.BLOCKED);

    await _executeAsync(connection, "UPDATE wh_perspective_events SET processed_at = NOW() WHERE stream_id = @id", stream);
    await _executeAsync(connection, "INSERT INTO wh_event_destruction_hold (event_id, hold_until) VALUES (@id, NOW() + INTERVAL '1 day')", ids[0]);
    await Assert.That((await store.WriteAsync(ArchiveBatch.WholeStream(stream))).Status).IsEqualTo(ArchiveStatus.HELD);
    await Assert.That(await _hotCountAsync(connection, stream)).IsEqualTo(2L);
    await Assert.That(await _archivedCountAsync(connection, stream)).IsEqualTo(0L);

    // The control: a lapsed hold is no longer a decision to keep the event, so the stream archives.
    await _executeAsync(connection, "UPDATE wh_event_destruction_hold SET hold_until = NOW() - INTERVAL '1 second' WHERE event_id = @id", ids[0]);
    await Assert.That((await store.WriteAsync(ArchiveBatch.WholeStream(stream))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_event_destruction_hold WHERE event_id = @id", ids[0])).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_perspective_events WHERE stream_id = @id", stream)).IsEqualTo(0L)
      .Because("finished work rows of archived events go with them");
  }

  [Test]
  public async Task Write_NothingHot_ReportsNothingToArchive_AndBumpsNothingAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 2);
    var store = _store();
    _ = await store.WriteAsync(ArchiveBatch.Prefix(stream, 1));
    var generation = await _generationAsync(connection);

    var again = await store.WriteAsync(ArchiveBatch.Prefix(stream, 1));
    var unknown = await store.WriteAsync(ArchiveBatch.WholeStream(Guid.CreateVersion7()));

    await Assert.That(again).IsEqualTo(new ArchiveWriteResult(ArchiveStatus.NOTHING_TO_ARCHIVE, 0, null, null, generation))
      .Because("the prefix is already archived");
    await Assert.That(unknown.Status).IsEqualTo(ArchiveStatus.NOTHING_TO_ARCHIVE);
    await Assert.That(await _generationAsync(connection)).IsEqualTo(generation).Because("nothing moved");
  }

  [Test]
  public async Task Write_DebugMode_KeepsEverythingHotAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 2);
    await _executeAsync(connection,
      "INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES ('debug_mode', 'true', 'boolean', 'test') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = 'true'");

    var result = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(result.Status).IsEqualTo(ArchiveStatus.DEBUG_SKIPPED);
    await Assert.That(await _hotCountAsync(connection, stream)).IsEqualTo(2L);
  }

  [Test]
  public async Task Write_WaitsForTheStreamsAppendLock_SoNothingIsAppendedWhileItMovesAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 2);
    // A second session holds the key the emit chains and the direct append take for this stream.
    await using var appender = new NpgsqlConnection(ConnectionString);
    await appender.OpenAsync();
    await using var appending = await appender.BeginTransactionAsync();
    await _executeAsync(appender, "SELECT pg_advisory_xact_lock(hashtext('wh_event_store:' || @id::text))", stream);

    await using (var held = await connection.BeginTransactionAsync()) {
      await _executeAsync(connection, "SET LOCAL lock_timeout = '100ms'");
      var waited = await Assert.That(async () => await _executeAsync(connection, "SELECT * FROM wh_archive_stream(@id, NULL, 'x')", stream))
        .Throws<PostgresException>();
      await Assert.That(waited!.SqlState).IsEqualTo(PostgresErrorCodes.LockNotAvailable)
        .Because("the archive waits for the append in flight instead of moving the stream under it");
      await held.RollbackAsync();
    }

    // The control: once the append commits, the same call goes through.
    await appending.CommitAsync();
    await Assert.That((await _store().WriteAsync(ArchiveBatch.WholeStream(stream))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
  }

  // ── The tombstone blocks version reuse ─────────────────────────────────────────────────────────────

  [Test]
  public async Task Tombstone_AnAppendAtOrBelowTheArchivedVersion_IsRefused_AboveItIsStoredAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 3);
    _ = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));

    // The direct append numbers from the newest hot version; with nothing hot it would reuse version 0.
    var direct = await Assert.That(async () => await _executeAsync(connection,
        "SELECT wh_append_event(gen_random_uuid(), @id, @id, 'Agg', 'T', '{}'::jsonb, '{}'::jsonb, NULL::jsonb, NOW())", stream))
      .Throws<PostgresException>();
    await Assert.That(direct!.SqlState).IsEqualTo(ARCHIVED_VERSION_REFUSED);

    // The boundary: the last archived version itself is refused, the next one is stored.
    var atLast = await Assert.That(async () => await _executeAsync(connection,
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version) " +
        "VALUES (gen_random_uuid(), @id, @id, 'Agg', 'T', 3)", stream))
      .Throws<PostgresException>();
    await Assert.That(atLast!.SqlState).IsEqualTo(ARCHIVED_VERSION_REFUSED)
      .Because("version 3 is held by the archive, so storing another event there would give the stream two version 3s");
    await _executeAsync(connection,
      "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version) " +
      "VALUES (gen_random_uuid(), @id, @id, 'Agg', 'T', 4)", stream);
    await Assert.That(await _hotCountAsync(connection, stream)).IsEqualTo(1L).Because("a version above the archived floor is new history");
  }

  [Test]
  public async Task Tombstone_TheEmitChainCannotReuseAnArchivedVersion_EitherAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 2);
    _ = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));

    var refused = await Assert.That(async () => await _commitAsync(connection, Guid.CreateVersion7(), stream)).Throws<PostgresException>();

    await Assert.That(refused!.SqlState).IsEqualTo(ARCHIVED_VERSION_REFUSED);
    await Assert.That(await _hotCountAsync(connection, stream)).IsEqualTo(0L);

    // The control: an ordinary stream still commits through the same chain.
    var other = Guid.CreateVersion7();
    await _commitAsync(connection, Guid.CreateVersion7(), other);
    await Assert.That(await _hotCountAsync(connection, other)).IsEqualTo(1L);
  }

  [Test]
  public async Task Write_WholeStream_WithNoLedgerRowYet_BuildsTheTombstoneFromTheEventsAsync() {
    // A stream the ledger backfill has not reached yet has no row; the tombstone is built from its events by the
    // ledger's own rules: the first event's type, tenant and time, the UTC hour of the newest event.
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    await _commitAsync(connection, Guid.CreateVersion7(), stream, scope: """{"t":"tenant-first"}""");
    await _commitAsync(connection, Guid.CreateVersion7(), stream, scope: """{"t":"tenant-later"}""");
    await _commitAsync(connection, Guid.CreateVersion7(), stream);
    var expected = await _ledgerShapeFromEventsAsync(connection, stream);
    await _executeAsync(connection, "DELETE FROM wh_stream_lifecycle WHERE stream_id = @id", stream);

    var written = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(written.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await _ledgerRowAsync(connection, stream))
      .IsEqualTo($"{expected}|3|0|Archived|postgres-table://wh_event_archive?codec=none&format=1|{written.OriginGeneration}")
      .Because("the tombstone carries the stream's identity even when the ledger had no row for it");
    await Assert.That(expected.Split('|')[1]).IsEqualTo("tenant-first").Because("the tenant is the first event's, as the ledger records it");

    var restored = await _store().RestoreAsync(stream);

    await Assert.That(restored.Status).IsEqualTo(ArchiveStatus.RESTORED);
    var row = await _tombstoneAsync(connection, stream);
    await Assert.That(row!.Value.State).IsEqualTo("Active").Because("the restored events went in without tripping the guard");
    await Assert.That(row.Value.LastVersion).IsEqualTo(3L);
  }

  [Test]
  public async Task ApproxBytes_ArchiveSubtractsWhatLeft_RestoreAddsItBackAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 3);
    var before = await _approxBytesAsync(connection, stream);
    var prefixBytes = await _scalarAsync(connection,
      "SELECT COALESCE(SUM(pg_column_size(eb.event_data) + pg_column_size(eb.metadata)), 0) FROM wh_event_store es " +
      "JOIN wh_event_body eb ON eb.event_id = es.event_id WHERE es.stream_id = @id AND es.version <= 2", stream);
    await Assert.That(prefixBytes).IsGreaterThan(0L).Because("the prefix must have bytes, or their subtraction proves nothing");
    var store = _store();

    _ = await store.WriteAsync(ArchiveBatch.Prefix(stream, 2));
    await Assert.That(await _approxBytesAsync(connection, stream)).IsEqualTo(before - prefixBytes)
      .Because("the ledger counts the hot store, and the archived prefix left it");
    _ = await store.RestoreAsync(stream);
    await Assert.That(await _approxBytesAsync(connection, stream)).IsEqualTo(before)
      .Because("a restore brings the same bodies back");

    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    await Assert.That(await _approxBytesAsync(connection, stream)).IsEqualTo(0L).Because("nothing of the stream is hot");
    _ = await store.RestoreAsync(stream);
    await Assert.That(await _approxBytesAsync(connection, stream)).IsEqualTo(before);
  }

  [Test]
  public async Task Purge_AnArchivedStream_TakesItsArchiveParkedCursorsAndTombstoneAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    var ids = await _streamAsync(connection, stream, 2);
    await _executeAsync(connection,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@id, 'P', " +
      $"'{ids[1]}', 0, NOW())", stream);
    _ = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));

    var report = await new PostgresStreamPurger(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync).PurgeAsync(
      new StreamPurgeRequest { StreamIds = [stream], RequestedBy = "operator-1", Reason = "archived by mistake" });

    await Assert.That(report.Totals["wh_event_archive_cursors"]).IsEqualTo(1L)
      .Because("a purged stream is gone, so the cursors parked for its restore go too");
    await Assert.That(report.Totals["wh_event_archive"]).IsEqualTo(2L);
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_event_archive_cursors WHERE stream_id = @id", stream)).IsEqualTo(0L);
    await Assert.That(await _tombstoneAsync(connection, stream)).IsNull();
  }

  // ── The archive manifest (268, #1737) ────────────────────────────────────────────────────────────

  // One manifest row for a chunk of the stream, as a blob store writes it after its archive.
  private static Task _manifestRowAsync(NpgsqlConnection connection, Guid streamId, long firstVersion) =>
    _executeAsync(connection,
      "INSERT INTO wh_archive_manifest (stream_id, first_version, last_version, event_count, locator, byte_length, sha256, origin_generation) " +
      $"VALUES (@id, {firstVersion}, {firstVersion + 1}, 2, 'azure-blob://c/s/chunk?codec=brotli&format=1', 10, " +
      "'0000000000000000000000000000000000000000000000000000000000000000', 1)", streamId);

  [Test]
  public async Task Purge_AStreamArchivedOutsideTheDatabase_TakesItsManifestRows_AndLeavesOthersAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var purged = Guid.CreateVersion7();
    var kept = Guid.CreateVersion7();
    _ = await _streamAsync(connection, purged, 2);
    _ = await _streamAsync(connection, kept, 2);
    _ = await _store().WriteAsync(ArchiveBatch.WholeStream(purged));
    _ = await _store().WriteAsync(ArchiveBatch.WholeStream(kept));
    await _manifestRowAsync(connection, purged, 1);
    await _manifestRowAsync(connection, kept, 1);

    var report = await new PostgresStreamPurger(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync).PurgeAsync(
      new StreamPurgeRequest { StreamIds = [purged], RequestedBy = "operator-1", Reason = "archived by mistake" });

    await Assert.That(report.Totals.ContainsKey("wh_archive_manifest")).IsFalse()
      .Because("the rows go by the cascade from the lifecycle row, which the purge report does not count");
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_archive_manifest WHERE stream_id = @id", purged))
      .IsEqualTo(0L).Because("a purged stream must never be restored from an object its manifest still names");
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_archive_manifest WHERE stream_id = @id", kept))
      .IsEqualTo(1L).Because("the cascade follows the purged stream's lifecycle row only");
  }

  [Test]
  public async Task Manifest_ARowForAStreamWithNoLifecycleRow_IsRefusedAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);

    var refused = await Assert.ThrowsAsync<PostgresException>(() => _manifestRowAsync(connection, Guid.CreateVersion7(), 1));

    await Assert.That(refused!.ConstraintName).IsEqualTo("fk_archive_manifest_stream")
      .Because("every archive writes the stream's lifecycle row first, so a manifest row without one is a bug, not data");
  }

  // ── Restore and delete ───────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Restore_PutsTheStreamBackExactly_CursorsDigestsAndPositionsIncludedAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    var ids = await _streamAsync(connection, stream, 3);
    // An at-most-once event is never folded, and an audit record type is digest-exempt: the refold must skip both.
    await _commitAsync(connection, Guid.CreateVersion7(), stream, flags: 32);
    await _commitAsync(connection, Guid.CreateVersion7(), stream, eventType: "Whizbang.Core.SystemEvents.EventAudited, Whizbang.Core");
    await _executeAsync(connection, "SELECT stamp_pending_commit_sequences(1000)");
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_event_store WHERE stream_id = @id AND commit_sequence IS NULL", stream))
      .IsEqualTo(0L).Because("the events must hold commit positions, or the restore's positions prove nothing");
    await _executeAsync(connection,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at, error) VALUES (@id, 'P', " +
      $"'{ids[1]}', 2, '2026-01-02T03:04:05Z', 'kept as it was')", stream);
    var rows = await _fullHotRowsAsync(connection, stream);
    var digests = await _digestRowsAsync(connection, stream);
    var cursor = await _cursorRowAsync(connection, stream);
    await Assert.That(digests).IsNotEmpty().Because("the seed must have folded digests, or their return proves nothing");
    var store = _store();
    var archived = await store.WriteAsync(ArchiveBatch.WholeStream(stream));

    var restored = await store.RestoreAsync(stream);

    await Assert.That(restored).IsEqualTo(new ArchiveRestoreResult(ArchiveStatus.RESTORED, 5, archived.OriginGeneration + 1));
    await Assert.That(await _fullHotRowsAsync(connection, stream)).IsEquivalentTo(rows, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("every event returns at its version and commit position with its body, so nothing reads it as new");
    await Assert.That(await _digestRowsAsync(connection, stream)).IsEquivalentTo(digests, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the refold adds back exactly what the chain folded: not the at-most-once event, not the audit record");
    await Assert.That(await _cursorRowAsync(connection, stream)).IsEqualTo(cursor).Because("the parked cursor returns as it was");
    await Assert.That(await _archivedCountAsync(connection, stream)).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_event_archive_cursors WHERE stream_id = @id", stream)).IsEqualTo(0L);
    await Assert.That(await _tombstoneAsync(connection, stream)).IsEqualTo(("Active", (long?)5, (string?)null, (long?)restored.OriginGeneration))
      .Because("the stream is active again, so appends continue above its last version");
    await _commitAsync(connection, Guid.CreateVersion7(), stream);
    await Assert.That(await _scalarAsync(connection, "SELECT max(version) FROM wh_event_store WHERE stream_id = @id", stream)).IsEqualTo(6L);
  }

  [Test]
  public async Task Restore_APrefix_UnderItsSurvivingTailAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 3);
    var rows = await _hotRowsAsync(connection, stream);
    var store = _store();
    _ = await store.WriteAsync(ArchiveBatch.Prefix(stream, 2));

    var restored = await store.RestoreAsync(stream);

    await Assert.That(restored.EventsRestored).IsEqualTo(2L);
    await Assert.That(await _hotRowsAsync(connection, stream)).IsEquivalentTo(rows, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That((await _tombstoneAsync(connection, stream))!.Value.Locator).IsNull();
  }

  [Test]
  public async Task Restore_IsIdempotent_TheSecondCallFindsNothingArchivedAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 2);
    var store = _store(new ListLogger());
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    var first = await store.RestoreAsync(stream);
    var rows = await _fullHotRowsAsync(connection, stream);

    var second = await store.RestoreAsync(stream);

    await Assert.That(second).IsEqualTo(new ArchiveRestoreResult(ArchiveStatus.NOTHING_ARCHIVED, 0, first.OriginGeneration))
      .Because("a second restore moves nothing and bumps nothing");
    await Assert.That(await _fullHotRowsAsync(connection, stream)).IsEquivalentTo(rows, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Restore_AHotEventAtAnArchivedVersion_IsAVersionConflict_AndRestoresNothingAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 2);
    var store = _store();
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    // Only a lost tombstone lets this happen: the chain numbers the stream from 1 again.
    await _executeAsync(connection, "DELETE FROM wh_stream_lifecycle WHERE stream_id = @id", stream);
    await _commitAsync(connection, Guid.CreateVersion7(), stream);

    var result = await store.RestoreAsync(stream);

    await Assert.That(result.Status).IsEqualTo(ArchiveStatus.VERSION_CONFLICT);
    await Assert.That(await _archivedCountAsync(connection, stream)).IsEqualTo(2L).Because("the archive is kept for an operator to resolve");
    await Assert.That(await _hotCountAsync(connection, stream)).IsEqualTo(1L);
  }

  [Test]
  public async Task Restore_ARowArchivedBeforeTheStoreKeptPositions_IsStoredUnflaggedForTheStamperAsync() {
    // Migration 085's copy kept no flags and no commit position; such a row goes back unflagged and unstamped.
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    var id = Guid.CreateVersion7();
    await _executeAsync(connection,
      "INSERT INTO wh_event_archive (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at) " +
      $"VALUES ('{id}', @id, @id, 'Agg', 'Old.Type', '{{\"a\":1}}'::jsonb, '{{}}'::jsonb, 1, '2026-01-02T03:04:05Z')", stream);

    var restored = await _store().RestoreAsync(stream);

    await Assert.That(restored.EventsRestored).IsEqualTo(1L);
    await Assert.That(await _scalarAsync(connection,
        "SELECT count(*) FROM wh_event_store WHERE event_id = @id AND flags = 0 AND commit_sequence IS NULL AND insert_order IS NOT NULL", id))
      .IsEqualTo(1L).Because("with no recorded position, the stamper gives the row a new one");
  }

  [Test]
  public async Task Delete_RemovesTheArchivedCopy_TheTombstoneStillBlocksReuseAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    var ids = await _streamAsync(connection, stream, 2);
    await _executeAsync(connection,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@id, 'P', " +
      $"'{ids[1]}', 0, NOW())", stream);
    var store = _store(new ListLogger());
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));

    var deleted = await store.DeleteAsync(stream);

    await Assert.That(deleted).IsEqualTo(2L);
    await Assert.That(await _archivedCountAsync(connection, stream)).IsEqualTo(0L);
    await Assert.That(await _scalarAsync(connection, "SELECT count(*) FROM wh_event_archive_cursors WHERE stream_id = @id", stream)).IsEqualTo(0L);
    var tombstone = await _tombstoneAsync(connection, stream);
    await Assert.That(tombstone!.Value.State).IsEqualTo("Archived");
    await Assert.That(tombstone.Value.Locator).IsNull().Because("there is no archive left to point at");
    await Assert.That(async () => await _commitAsync(connection, Guid.CreateVersion7(), stream)).Throws<PostgresException>()
      .Because("the versions were seen downstream, so they are never reused even with the archive gone");
  }

  // ── Wiring ─────────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Store_LogsEachOutcome_AndNamesItsSchemaInTheLocatorAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 2);
    var logger = new ListLogger();

    _ = await _store(logger).WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(logger.Messages.Any(m => m.Contains(stream.ToString(), StringComparison.Ordinal) && m.Contains("archived", StringComparison.Ordinal))).IsTrue();
    await Assert.That(new PostgresTableArchiveStore(_ => throw new NotSupportedException(), "svc").Locator.ToString())
      .IsEqualTo("postgres-table://svc.wh_event_archive?codec=none&format=1")
      .Because("a named-schema service records which schema holds its archive");
  }

  [Test]
  public async Task PrefixTruncate_ThroughTheDriversRegistration_ArchivesThroughTheStoreAsync() {
    // The production caller: the driver registers the store, the worker pipeline hands it to the truncator, and an
    // archiving truncate lands in wh_event_archive with every column the store keeps (085's copy has no commit position).
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    _ = await _streamAsync(connection, stream, 3);
    await _executeAsync(connection, "SELECT stamp_pending_commit_sequences(1000)");
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<ArchiveRegistrationDbContext>(options => options.UseNpgsql(ConnectionString));
    services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
    var selector = new Whizbang.Core.Perspectives.WhizbangPerspectiveBuilder(services).WithEFCore<ArchiveRegistrationDbContext>();
    _ = selector.WithDriver.Postgres;
    services.AddWhizbangWorkers();
    services.AddSingleton<IWorkCoordinator>(_ => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      CreateDbContext(), Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    await using var provider = services.BuildServiceProvider();

    var registered = provider.GetRequiredService<IArchiveStore>();
    await Assert.That(registered).IsTypeOf<PostgresTableArchiveStore>();
    await Assert.That(((PostgresTableArchiveStore)registered).Locator.Address).IsEqualTo("public.wh_event_archive")
      .Because("the driver qualifies the archive table with the context's schema, as it does its other stores");
    await Assert.That(provider.GetRequiredService<PostgresArchiveConnection>().Schema).IsEqualTo("public")
      .Because("a store that keeps state in the database (the blob store's manifest) reaches it the way the table store does");
    var result = await provider.GetRequiredService<Whizbang.Core.Lifecycle.IStreamPrefixTruncator>().TruncateAsync(stream, 2, archive: true);

    await Assert.That(result).IsEqualTo(new StreamTruncateResult("truncated", 2));
    await Assert.That(await _scalarAsync(connection,
        "SELECT count(*) FROM wh_event_archive WHERE stream_id = @id AND commit_sequence IS NOT NULL AND insert_order IS NOT NULL", stream))
      .IsEqualTo(2L).Because("the archive went through the store's copy, which keeps the commit positions a restore needs");
    await Assert.That((await _tombstoneAsync(connection, stream))!.Value.Locator).IsNotNull();
  }

  /// <summary>A context with an empty model in the default schema: enough for the driver to register its stores.</summary>
  private sealed class ArchiveRegistrationDbContext(DbContextOptions<ArchiveRegistrationDbContext> options) : DbContext(options);

  // The identity part of a ledger row as 260 derives it from the events: type, tenant, first time, hour of the newest.
  private static async Task<string> _ledgerShapeFromEventsAsync(NpgsqlConnection connection, Guid streamId) {
    await using var c = connection.CreateCommand();
    c.CommandText =
      "SELECT concat_ws('|', f.aggregate_type, COALESCE(f.scope ->> 't', '<null>'), f.created_at, " +
      "  (SELECT date_trunc('hour', MAX(es.created_at), 'UTC') FROM wh_event_store es WHERE es.stream_id = @id)) " +
      "FROM (SELECT es.aggregate_type, es.scope, es.created_at FROM wh_event_store es WHERE es.stream_id = @id ORDER BY es.version LIMIT 1) f";
    c.Parameters.AddWithValue("id", streamId);
    return (string)(await c.ExecuteScalarAsync())!;
  }

  private static async Task<string> _ledgerRowAsync(NpgsqlConnection connection, Guid streamId) {
    await using var c = connection.CreateCommand();
    c.CommandText =
      "SELECT concat_ws('|', stream_type, COALESCE(tenant, '<null>'), first_appended_at, last_appended_bucket, last_version, approx_bytes, " +
      "  state, archive_locator, origin_generation) FROM wh_stream_lifecycle WHERE stream_id = @id";
    c.Parameters.AddWithValue("id", streamId);
    return (string)(await c.ExecuteScalarAsync())!;
  }

  private static Task<long> _approxBytesAsync(NpgsqlConnection connection, Guid streamId) =>
    _scalarAsync(connection, "SELECT approx_bytes FROM wh_stream_lifecycle WHERE stream_id = @id", streamId);

  private static async Task<string> _cursorRowAsync(NpgsqlConnection connection, Guid streamId) {
    await using var c = connection.CreateCommand();
    c.CommandText = "SELECT to_jsonb(c)::text FROM wh_perspective_cursors c WHERE stream_id = @id";
    c.Parameters.AddWithValue("id", streamId);
    return (string)(await c.ExecuteScalarAsync())!;
  }

  /// <summary>Records what the store logs.</summary>
  private sealed class ListLogger : ILogger<PostgresTableArchiveStore> {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Messages.Add(formatter(state, exception));
  }
}
