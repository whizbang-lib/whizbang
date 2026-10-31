// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The stream lifecycle ledger (<c>wh_stream_lifecycle</c>, #1732, WS-3 S3): one row per stream, written by every path
/// that stores an event (the outbox emit chain, the inbox emit chain and the direct append), so the end-of-life sweeps
/// that come after it find candidate streams without reading the event store.
/// </summary>
/// <remarks>
/// Every write is driven with a fixed instant: the hour bucket is the point of several tests, and a test that read the
/// clock would cross an hour boundary on some run.
/// </remarks>
/// <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/260_StreamLifecycleLedger.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StreamLifecycleLedgerSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Whizbang.Tests.LedgerOpened, Whizbang.Tests";
  private const string LATER_TYPE = "Whizbang.Tests.LedgerAdjusted, Whizbang.Tests";
  private const string AGGREGATE_TYPE = "Whizbang.Tests.LedgerOpened";
  private const int FLAG_EPHEMERAL = 8;
  private const int FLAG_COMPACTED = 16;

  /// <summary>12:10 UTC: inside an hour, so the bucket is 12:00 and the next hour starts fifty minutes later.</summary>
  private static readonly DateTimeOffset _t0 = new(2026, 10, 29, 12, 10, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset _hour12 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset _hour13 = new(2026, 10, 29, 13, 0, 0, TimeSpan.Zero);

  // 266 (#1733) adds sealed_at, the predicate of idx_stream_lifecycle_sealed: written only when a stream is sealed.
  private static readonly string[] _indexedColumns = ["disposition_due_at", "sealed_at", "state", "stream_id"];
  private static readonly string[] _duePredicate = ["(disposition_due_at IS NOT NULL)"];
  private static readonly string[] _states = ["Active", "Sealed", "Archived"];

  // ===== The outbox emit chain =====

  [Test]
  public async Task OutboxAppend_CreatesTheRow_FromTheStreamsFirstEventAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();

    var ids = await _emitOutboxAsync(conn, _t0,
      _event(stream, EVENT_TYPE, tenant: "tenant-a"),
      _event(stream, LATER_TYPE, tenant: "tenant-b"),
      _event(stream, LATER_TYPE, tenant: "tenant-b"));

    var row = await _rowAsync(conn, stream);
    await Assert.That(row).IsNotNull().Because("an append creates the stream's ledger row");
    await Assert.That(row!.StreamType).IsEqualTo(AGGREGATE_TYPE)
      .Because("the stream type is the aggregate_type of the stream's first event (decision 1)");
    await Assert.That(row.Tenant).IsEqualTo("tenant-a").Because("the tenant is the first event's scope tenant");
    await Assert.That(row.FirstAppendedAt).IsEqualTo(_t0);
    await Assert.That(row.LastAppendedBucket).IsEqualTo(_hour12).Because("the bucket is the UTC hour of the append");
    await Assert.That(row.LastVersion).IsEqualTo(await _maxVersionAsync(conn, stream));
    await Assert.That(row.ApproxBytes).IsEqualTo(await _bodyBytesAsync(conn, ids))
      .Because("approx_bytes is the size of the stored body (payload plus metadata) of every event appended");
    await Assert.That(row.State).IsEqualTo("Active").Because("a stream starts Active");
    await Assert.That(row.SealedAt).IsNull();
    await Assert.That(row.DispositionDueAt).IsNull().Because("an Active stream is never due, so it stays out of the due index");
    await Assert.That(row.ArchiveLocator).IsNull();
    await Assert.That(row.OriginGeneration).IsNull().Because("appends never record a history mutation");
  }

  [Test]
  public async Task SecondAppend_AdvancesVersionAndBytes_AndKeepsWhatTheFirstEventSetAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var first = await _emitOutboxAsync(conn, _t0, _event(stream, EVENT_TYPE, tenant: "tenant-a"));

    var second = await _emitOutboxAsync(conn, _t0.AddMinutes(20),
      _event(stream, LATER_TYPE, tenant: "tenant-b"), _event(stream, LATER_TYPE, tenant: "tenant-b"));

    var row = (await _rowAsync(conn, stream))!;
    await Assert.That(row.LastVersion).IsEqualTo(await _maxVersionAsync(conn, stream))
      .Because("each append moves last_version to the stream's newest version");
    await Assert.That(row.ApproxBytes).IsEqualTo(await _bodyBytesAsync(conn, [.. first, .. second]))
      .Because("each append adds the bytes it stored");
    await Assert.That(row.StreamType).IsEqualTo(AGGREGATE_TYPE).Because("a later event of another type never renames the stream");
    await Assert.That(row.Tenant).IsEqualTo("tenant-a");
    await Assert.That(row.FirstAppendedAt).IsEqualTo(_t0).Because("first_appended_at is written once");
  }

  [Test]
  public async Task Append_WritesOneLedgerRowPerStreamPerBatch_NotOnePerEventAsync() {
    await using var conn = await _openAsync();
    var a = (Guid)TrackedGuid.New();
    var b = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, _event(a, EVENT_TYPE), _event(b, EVENT_TYPE));

    await using var tx = await conn.BeginTransactionAsync();
    var (startInserted, startUpdated, _) = await _ledgerTransactionCountsAsync(conn);
    var batch = Enumerable.Range(0, 5).SelectMany(_ => new[] { _event(a, LATER_TYPE), _event(b, LATER_TYPE) }).ToArray();
    await _emitOutboxAsync(conn, _t0.AddMinutes(5), batch);
    var (endInserted, endUpdated, _) = await _ledgerTransactionCountsAsync(conn);
    await tx.RollbackAsync();
    var (inserted, updated) = (endInserted - startInserted, endUpdated - startUpdated);

    await Assert.That(inserted).IsEqualTo(0L);
    await Assert.That(updated).IsEqualTo(2L)
      .Because("ten events on two streams are one set-based upsert of two rows, not ten row writes");
  }

  // ===== HOT: an append within the hour rewrites no indexed column =====

  [Test]
  public async Task AppendWithinTheHour_IsAHeapOnlyUpdate_WhileAnIndexedColumnChangeIsNotAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, _event(stream, EVENT_TYPE));

    await using var tx = await conn.BeginTransactionAsync();
    var (_, startUpdated, startHot) = await _ledgerTransactionCountsAsync(conn);
    await _emitOutboxAsync(conn, _t0.AddMinutes(20), _event(stream, LATER_TYPE));
    var (_, appendUpdated, appendHot) = await _ledgerTransactionCountsAsync(conn);

    // Control: the same harness sees an update that changes an indexed column as not heap-only.
    await _executeAsync(conn, "UPDATE wh_stream_lifecycle SET state = 'Sealed' WHERE stream_id = @s", ("s", stream));
    var (_, controlUpdated, controlHot) = await _ledgerTransactionCountsAsync(conn);
    await tx.RollbackAsync();
    var (updated, hot) = (appendUpdated - startUpdated, appendHot - startHot);
    var (updatedAfterControl, hotAfterControl) = (controlUpdated - startUpdated, controlHot - startHot);

    await Assert.That(updated).IsEqualTo(1L);
    await Assert.That(hot).IsEqualTo(1L)
      .Because("an append inside the hour writes last_version, approx_bytes and an unchanged bucket, none of them indexed");
    await Assert.That(updatedAfterControl).IsEqualTo(2L);
    await Assert.That(hotAfterControl).IsEqualTo(1L)
      .Because("state is indexed, so changing it is not heap-only: the harness can tell the two apart");
  }

  [Test]
  public async Task OneBatchAppendingToEveryStreamOnItsPages_IsStillHeapOnlyAsync() {
    await using var conn = await _openAsync();
    // Streams created in one batch share the ledger's pages, and a bulk load appends to all of them together.
    var streams = Enumerable.Range(0, 60).Select(_ => (Guid)TrackedGuid.New()).ToArray();
    await _emitOutboxAsync(conn, _t0, [.. streams.Select(s => _event(s, EVENT_TYPE, tenant: "tenant-page"))]);
    var pages = await _scalarAsync(conn, "SELECT pg_relation_size('wh_stream_lifecycle') / current_setting('block_size')::int");

    await using var tx = await conn.BeginTransactionAsync();
    var (_, startUpdated, startHot) = await _ledgerTransactionCountsAsync(conn);
    await _emitOutboxAsync(conn, _t0.AddMinutes(1), [.. streams.Select(s => _event(s, LATER_TYPE))]);
    var (_, endUpdated, endHot) = await _ledgerTransactionCountsAsync(conn);
    await tx.RollbackAsync();

    await Assert.That(pages).IsGreaterThan(1L).Because("the rows fill more than one page, so every page is rewritten whole");
    await Assert.That(endUpdated - startUpdated).IsEqualTo(60L);
    await Assert.That(endHot - startHot).IsEqualTo(60L)
      .Because("fillfactor 45 leaves room on a page for a new version of every row on it; at 70 about half of these updates leave the page");
  }

  [Test]
  public async Task TheLedger_IndexesOnlyItsKeyStateAndDueTime_AndLeavesRoomOnEachPageAsync() {
    await using var conn = await _openAsync();

    var indexed = await _stringsAsync(conn, """
      SELECT DISTINCT a.attname
      FROM pg_index i
      JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
      WHERE i.indrelid = 'wh_stream_lifecycle'::regclass
      UNION
      SELECT DISTINCT a.attname
      FROM pg_index i
      JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum > 0
      WHERE i.indrelid = 'wh_stream_lifecycle'::regclass
        AND i.indpred IS NOT NULL
        AND pg_get_expr(i.indpred, i.indrelid) ~ ('\m' || a.attname || '\M')
      ORDER BY 1
      """);
    var predicate = await _stringsAsync(conn, """
      SELECT pg_get_expr(i.indpred, i.indrelid)
      FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
      WHERE i.indrelid = 'wh_stream_lifecycle'::regclass AND c.relname = 'idx_stream_lifecycle_due'
      """);
    var options = await _stringsAsync(conn,
      "SELECT array_to_string(reloptions, ',') FROM pg_class WHERE oid = 'wh_stream_lifecycle'::regclass");

    await Assert.That(indexed).IsEquivalentTo(_indexedColumns)
      .Because("an update is heap-only only when no indexed column changes: the stream id never changes, and state, the seal time and the due time change only at end of life");
    await Assert.That(predicate).IsEquivalentTo(_duePredicate)
      .Because("the due index holds only the streams that have a disposition scheduled");
    await Assert.That(options.Single()).Contains("fillfactor=45")
      .Because("each append rewrites its stream's row, and a heap-only update needs free space on the row's page");
  }

  // ===== The hour bucket =====

  [Test]
  public async Task Bucket_AdvancesOnlyWhenTheHourChanges_AndAnEarlierClockNeverMovesItBackAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    // A zone half an hour off UTC: a local-hour truncation would land on :30, which is not an hour of the bucket.
    await _executeAsync(conn, "SET TimeZone = 'Asia/Kolkata'");

    await _emitOutboxAsync(conn, _t0, _event(stream, EVENT_TYPE));
    var first = (await _rowAsync(conn, stream))!.LastAppendedBucket;
    await _emitOutboxAsync(conn, _hour13.AddTicks(-TimeSpan.TicksPerSecond), _event(stream, LATER_TYPE));
    var justUnder = (await _rowAsync(conn, stream))!.LastAppendedBucket;
    await _emitOutboxAsync(conn, _hour13, _event(stream, LATER_TYPE));
    var atTheHour = (await _rowAsync(conn, stream))!.LastAppendedBucket;
    await _emitOutboxAsync(conn, _t0.AddMinutes(20), _event(stream, LATER_TYPE));
    var afterSkew = (await _rowAsync(conn, stream))!.LastAppendedBucket;

    await Assert.That(first).IsEqualTo(_hour12).Because("the bucket is the UTC hour, whatever the session's time zone");
    await Assert.That(justUnder).IsEqualTo(_hour12).Because("12:59:59 is still the 12:00 hour");
    await Assert.That(atTheHour).IsEqualTo(_hour13).Because("13:00:00 is the next hour");
    await Assert.That(afterSkew).IsEqualTo(_hour13).Because("an append stamped earlier (another instance's clock) never moves the bucket back");
  }

  // ===== The inbox emit chain =====

  [Test]
  public async Task InboxAppend_CreatesAndAdvancesTheRowAsync() {
    await using var dbContext = CreateDbContext();
    var stream = (Guid)TrackedGuid.New();

    var conn = await _receiveAsync(dbContext, _t0, _inboxEvent(stream, "tenant-r"), _inboxEvent(stream, "tenant-r"));
    var afterFirst = (await _rowAsync(conn, stream))!;
    await _receiveAsync(dbContext, _t0.AddHours(2), _inboxEvent(stream, "tenant-r"));
    var afterSecond = (await _rowAsync(conn, stream))!;

    await Assert.That(afterFirst.StreamType).IsEqualTo(AGGREGATE_TYPE);
    await Assert.That(afterFirst.Tenant).IsEqualTo("tenant-r");
    await Assert.That(afterFirst.FirstAppendedAt).IsEqualTo(_t0);
    await Assert.That(afterFirst.LastAppendedBucket).IsEqualTo(_hour12);
    await Assert.That(afterSecond.LastVersion).IsEqualTo(await _maxVersionAsync(conn, stream));
    await Assert.That(afterSecond.LastVersion).IsGreaterThan(afterFirst.LastVersion);
    await Assert.That(afterSecond.ApproxBytes).IsEqualTo(await _bodyBytesOfStreamAsync(conn, stream));
    await Assert.That(afterSecond.LastAppendedBucket).IsEqualTo(_hour12.AddHours(2));
    await Assert.That(afterSecond.FirstAppendedAt).IsEqualTo(_t0);
  }

  // ===== The direct append =====

  [Test]
  public async Task DirectAppend_CreatesAndAdvancesTheRowAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();

    await _appendDirectAsync(conn, stream, "Whizbang.Tests.LedgerAggregate", """{"t":"tenant-d"}""", _t0, 0);
    var afterFirst = (await _rowAsync(conn, stream))!;
    await _appendDirectAsync(conn, stream, "Whizbang.Tests.OtherAggregate", """{"t":"tenant-x"}""", _hour13.AddMinutes(5), 0);
    var afterSecond = (await _rowAsync(conn, stream))!;

    await Assert.That(afterFirst.StreamType).IsEqualTo("Whizbang.Tests.LedgerAggregate")
      .Because("a direct append names its aggregate type, and the first one is the stream's type");
    await Assert.That(afterFirst.Tenant).IsEqualTo("tenant-d");
    await Assert.That(afterFirst.FirstAppendedAt).IsEqualTo(_t0);
    await Assert.That(afterFirst.LastAppendedBucket).IsEqualTo(_hour12);
    await Assert.That(afterFirst.LastVersion).IsEqualTo(1L).Because("a direct append numbers a new stream from 1 (#1913)");
    await Assert.That(afterSecond.LastVersion).IsEqualTo(2L);
    await Assert.That(afterSecond.StreamType).IsEqualTo("Whizbang.Tests.LedgerAggregate");
    await Assert.That(afterSecond.Tenant).IsEqualTo("tenant-d");
    await Assert.That(afterSecond.LastAppendedBucket).IsEqualTo(_hour13);
    await Assert.That(afterSecond.ApproxBytes).IsEqualTo(await _bodyBytesOfStreamAsync(conn, stream));
  }

  [Test]
  public async Task DirectAppend_ThroughTheEventStore_CreatesTheRowAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using (var context = CreateDbContext()) {
      var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
      await store.AppendAsync(stream, _envelope());
      await store.AppendAsync(stream, _envelope());
    }

    await using var conn = await _openAsync();
    var row = await _rowAsync(conn, stream);
    await Assert.That(row).IsNotNull().Because("IEventStore.AppendAsync stores through wh_append_event, which keeps the ledger");
    await Assert.That(row!.LastVersion).IsEqualTo(await _maxVersionAsync(conn, stream));
  }

  // ===== What gets no row =====

  [Test]
  public async Task EphemeralAndCompactionOriginEvents_CreateNoRow_WhileASourcedEventDoesAsync() {
    await using var conn = await _openAsync();
    var ephemeral = (Guid)TrackedGuid.New();
    var sourced = (Guid)TrackedGuid.New();
    var direct = (Guid)TrackedGuid.New();

    await _emitOutboxAsync(conn, _t0,
      _event(ephemeral, EVENT_TYPE, flags: FLAG_COMPACTED),
      _event(ephemeral, LATER_TYPE, flags: FLAG_EPHEMERAL),
      _event(sourced, EVENT_TYPE));
    await _appendDirectAsync(conn, direct, "Whizbang.Tests.Typing", "null", _t0, FLAG_EPHEMERAL);
    await _appendDirectAsync(conn, direct, "Whizbang.Tests.Typing", "null", _t0, FLAG_COMPACTED);

    await Assert.That(await _storedCountAsync(conn, ephemeral)).IsEqualTo(2L).Because("the events themselves are stored");
    await Assert.That(await _storedCountAsync(conn, direct)).IsEqualTo(2L);
    await Assert.That(await _rowAsync(conn, ephemeral)).IsNull()
      .Because("an ephemeral stream is reaped by its time to live and never reaches end of life");
    await Assert.That(await _rowAsync(conn, direct)).IsNull()
      .Because("the direct append applies the same rule");
    await Assert.That(await _rowAsync(conn, sourced)).IsNotNull()
      .Because("the control: a Sourced event in the same batch does get its row");
  }

  [Test]
  public async Task EventsWithoutAStream_CreateNoRowAsync() {
    await using var conn = await _openAsync();

    await _emitOutboxAsync(conn, _t0, _event(Guid.Empty, EVENT_TYPE));
    await _appendDirectAsync(conn, Guid.Empty, "Whizbang.Tests.Streamless", "null", _t0, 0);

    await Assert.That(await _storedCountAsync(conn, Guid.Empty)).IsEqualTo(2L);
    await Assert.That(await _rowAsync(conn, Guid.Empty)).IsNull()
      .Because("the empty stream id means no stream: there is nothing to finalize, archive or purge");
  }

  // ===== Locks =====

  [Test]
  public async Task ConcurrentBatchesOnDifferentStreams_NeitherWaitsForTheOtherAsync() {
    await using var holder = await _openAsync();
    await using var other = await _openAsync();
    var held = (Guid)TrackedGuid.New();
    var free = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(holder, _t0, _event(held, EVENT_TYPE));
    await _emitOutboxAsync(other, _t0, _event(free, EVENT_TYPE));
    await _executeAsync(other, "SET lock_timeout = '500ms'");

    await using var tx = await holder.BeginTransactionAsync();
    await _emitOutboxAsync(holder, _t0.AddMinutes(1), _event(held, LATER_TYPE));

    // Lock waits only: lock_timeout counts the time spent waiting for a lock, never the time spent working.
    await _emitOutboxAsync(other, _t0.AddMinutes(1), _event(free, LATER_TYPE));
    var blocked = await Assert.That(async () => await _emitOutboxAsync(other, _t0.AddMinutes(1), _event(held, LATER_TYPE)))
      .Throws<PostgresException>();
    await tx.RollbackAsync();

    await Assert.That((await _rowAsync(other, free))!.LastVersion).IsEqualTo(await _maxVersionAsync(other, free))
      .Because("a batch on another stream upserts its own ledger row while the first batch is still open");
    await Assert.That(blocked!.SqlState).IsEqualTo(PostgresErrorCodes.LockNotAvailable)
      .Because("the control: a batch on the held stream does wait, so the harness sees a wait when there is one");
  }

  // ===== The due-stream query =====

  [Test]
  public async Task DueStreamQuery_UsesThePartialIndex_AndNeverReadsTheEventStoreAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, """
      INSERT INTO wh_stream_lifecycle (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes)
      SELECT gen_random_uuid(), 'Perf.Active', NULL, @t, @b, 10, 1000 FROM generate_series(1, 20000);
      INSERT INTO wh_stream_lifecycle (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version,
                                       approx_bytes, state, sealed_at, disposition_due_at)
      SELECT gen_random_uuid(), 'Perf.Sealed', NULL, @t, @b, 10, 1000, 'Sealed', @t, @t + g * INTERVAL '1 minute'
      FROM generate_series(1, 100) AS g;
      ANALYZE wh_stream_lifecycle;
      """, ("t", _t0), ("b", _hour12));

    var plan = await _stringsAsync(conn, $"""
      EXPLAIN (FORMAT JSON)
      SELECT l.stream_id, l.state, l.disposition_due_at
      FROM wh_stream_lifecycle l
      WHERE l.disposition_due_at <= '{_t0.AddHours(1):O}'::timestamptz
      ORDER BY l.disposition_due_at
      LIMIT 50
      """);

    await Assert.That(plan.Single()).Contains("\"Index Name\": \"idx_stream_lifecycle_due\"")
      .Because("the due streams are found in the partial index, not by reading every stream's row");
    await Assert.That(plan.Single()).DoesNotContain("wh_event_store")
      .Because("candidate discovery never reads the event store");
    await Assert.That(plan.Single()).DoesNotContain("Seq Scan");
  }

  [Test]
  public async Task TheState_AcceptsOnlyItsNamedValuesAsync() {
    await using var conn = await _openAsync();

    foreach (var state in _states) {
      await _executeAsync(conn, """
        INSERT INTO wh_stream_lifecycle (stream_id, stream_type, first_appended_at, last_appended_bucket, last_version, state)
        VALUES (gen_random_uuid(), 'T', @t, @t, 1, @s)
        """, ("t", _t0), ("s", state));
    }
    var refused = await Assert.That(async () => await _executeAsync(conn, """
        INSERT INTO wh_stream_lifecycle (stream_id, stream_type, first_appended_at, last_appended_bucket, last_version, state)
        VALUES (gen_random_uuid(), 'T', @t, @t, 1, 'active')
        """, ("t", _t0)))
      .Throws<PostgresException>();

    await Assert.That(refused!.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation)
      .Because("the states are stable strings another slice matches on exactly; a misspelling is refused, not stored");
  }

  // ===== Helpers =====

  private sealed record LedgerEvent(Guid Stream, string Type, int Flags, string? Tenant);

  private sealed record LedgerRow(
    string StreamType, string? Tenant, DateTimeOffset FirstAppendedAt, DateTimeOffset LastAppendedBucket,
    long LastVersion, long ApproxBytes, string State, DateTimeOffset? SealedAt, DateTimeOffset? DispositionDueAt,
    string? ArchiveLocator, long? OriginGeneration);

  private static LedgerEvent _event(Guid stream, string type, int flags = 0, string? tenant = null) =>
    new(stream, type, flags, tenant);

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>Stores the events through the outbox emit chain at <paramref name="now"/>; returns their ids in order.</summary>
  private static async Task<Guid[]> _emitOutboxAsync(NpgsqlConnection conn, DateTimeOffset now, params LedgerEvent[] events) {
    var instanceId = (Guid)TrackedGuid.New();
    var ids = new List<Guid>();
    for (var i = 0; i < events.Length; i++) {
      var id = (Guid)TrackedGuid.New();
      ids.Add(id);
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'ledger-topic', @type, 'env', jsonb_build_object('p', jsonb_build_object('n', @n, 'note', repeat('x', @n))),
           '{}'::jsonb, CASE WHEN @tenant::text IS NULL THEN NULL ELSE jsonb_build_object('t', @tenant::text) END,
           1, 0, @inst, @lease, 0, @sid, true, @flags, @now)
        """,
        ("id", id), ("type", events[i].Type), ("n", 10 + (i * 7)), ("tenant", (object?)events[i].Tenant ?? DBNull.Value),
        ("inst", instanceId), ("lease", now.AddMinutes(5)), ("sid", events[i].Stream), ("flags", events[i].Flags), ("now", now));
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids.ToArray());
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", now.AddMinutes(5));
    emit.Parameters.AddWithValue(nameof(now), now);
    _ = await emit.ExecuteScalarAsync();
    return [.. ids];
  }

  private static async Task _appendDirectAsync(
      NpgsqlConnection conn, Guid stream, string aggregateType, string scopeJson, DateTimeOffset at, int flags) {
    await _executeAsync(conn, """
      SELECT wh_append_event(@id, @sid, @sid, @agg, 'Whizbang.Tests.DirectLedgerEvent, Whizbang.Tests',
                             '{"amount": 12}'::jsonb, '{"k": "v"}'::jsonb, @scope::jsonb, @at, @flags)
      """,
      ("id", (Guid)TrackedGuid.New()), ("sid", stream), ("agg", aggregateType), ("scope", scopeJson), ("at", at), ("flags", flags));
  }

  /// <summary>Stores inbox rows and runs the inbox emit chain over them at <paramref name="now"/>.</summary>
  private static async Task<NpgsqlConnection> _receiveAsync(WorkCoordinationDbContext dbContext, DateTimeOffset now, params InboxMessage[] rows) {
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.StoreInboxMessagesAsync(rows, partitionCount: 100);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var instanceId = (Guid)TrackedGuid.New();
    await _executeAsync(conn, "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = @lease WHERE message_id = ANY(@ids)",
      ("inst", instanceId), ("lease", now.AddMinutes(5)), ("ids", rows.Select(r => r.MessageId).ToArray()));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, @lease, @now, 4)",
      ("inst", instanceId), ("lease", now.AddMinutes(5)), ("now", now));
    return conn;
  }

  private static InboxMessage _inboxEvent(Guid stream, string tenant) {
    var id = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(id),
      Payload = JsonSerializer.SerializeToElement(new { happened = true }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new InboxMessage {
      MessageId = id,
      HandlerName = "LedgerHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = EVENT_TYPE,
      StreamId = stream,
      IsEvent = true,
      Flags = EventFlags.NoRebroadcast,
      Scope = new PerspectiveScope { TenantId = tenant },
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
    };
  }

  private static MessageEnvelope<JsonElement> _envelope() {
    var id = (Guid)TrackedGuid.New();
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(id),
      Payload = JsonSerializer.SerializeToElement(new { amount = 5 }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
  }

  private static async Task<LedgerRow?> _rowAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes, state,
             sealed_at, disposition_due_at, archive_locator, origin_generation
      FROM wh_stream_lifecycle WHERE stream_id = @s
      """;
    cmd.Parameters.AddWithValue("s", stream);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) {
      return null;
    }
    return new LedgerRow(
      r.GetString(0),
      r.IsDBNull(1) ? null : r.GetString(1),
      r.GetFieldValue<DateTimeOffset>(2),
      r.GetFieldValue<DateTimeOffset>(3),
      Convert.ToInt64(r.GetValue(4), CultureInfo.InvariantCulture),
      r.GetInt64(5),
      r.GetString(6),
      r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
      r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
      r.IsDBNull(9) ? null : r.GetString(9),
      r.IsDBNull(10) ? null : r.GetInt64(10));
  }

  private static Task<long> _maxVersionAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync(conn, "SELECT MAX(version) FROM wh_event_store WHERE stream_id = @s", ("s", stream));

  private static Task<long> _storedCountAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync(conn, "SELECT count(*) FROM wh_event_store WHERE stream_id = @s", ("s", stream));

  private static Task<long> _bodyBytesAsync(NpgsqlConnection conn, Guid[] eventIds) =>
    _scalarAsync(conn, """
      SELECT COALESCE(SUM(pg_column_size(b.event_data) + pg_column_size(b.metadata)), 0)
      FROM wh_event_body b WHERE b.event_id = ANY(@ids)
      """, ("ids", eventIds));

  private static Task<long> _bodyBytesOfStreamAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync(conn, """
      SELECT COALESCE(SUM(pg_column_size(b.event_data) + pg_column_size(b.metadata)), 0)
      FROM wh_event_store es JOIN wh_event_body b ON b.event_id = es.event_id WHERE es.stream_id = @s
      """, ("s", stream));

  /// <summary>
  /// The backend's not-yet-reported row counts on the ledger (pg_stat_xact_user_tables, no reporting delay). They can still
  /// hold an earlier transaction's counts that have not been flushed, so callers take the difference across their window.
  /// </summary>
  private static async Task<(long Inserted, long Updated, long Hot)> _ledgerTransactionCountsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT n_tup_ins, n_tup_upd, n_tup_hot_upd FROM pg_stat_xact_user_tables
      WHERE relid = 'wh_stream_lifecycle'::regclass
      """;
    await using var r = await cmd.ExecuteReaderAsync();
    _ = await r.ReadAsync();
    return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<List<string>> _stringsAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var values = new List<string>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) {
      values.Add(r.IsDBNull(0) ? string.Empty : r.GetValue(0).ToString()!);
    }
    return values;
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
