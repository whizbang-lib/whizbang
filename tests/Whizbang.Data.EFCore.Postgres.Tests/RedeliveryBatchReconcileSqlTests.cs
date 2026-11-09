// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Redelivery bundles reconciled as a batch (#2099), against the real <c>wh_reconcile_redelivered_bundles</c>
/// (migration 335). Each bundle gets what the single-bundle reconcile gives it (253, 321), the fills are one update,
/// and each sealed epoch that holds a moved event is refolded once per batch instead of once per bundle. A backlog of
/// one-event bundles that each fill one origin sequence (history re-attributed to its owner, 325, carries the origin
/// but not its sequence) then costs one epoch fold, not one per bundle.
/// </summary>
/// <remarks>
/// The fold counts are read from <c>pg_stat_xact_user_functions</c> inside the call's own transaction, so they do not
/// depend on the machine or on anything else running.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/335_RedeliveryReconcilesRunAsABatch.sql</code-under-test>
/// <docs>resilience/stream-integrity#redelivery-reconciles-run-as-a-batch</docs>
[Category("Integration")]
[Category("Shard5")]
public class RedeliveryBatchReconcileSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Contracts.OrderPlaced, Contracts";
  private const string TENANT = "tenant-a";
  private const string ORIGIN_NAME = "orders-service";
  private const string LOCAL_NAME = "inventory-service";
  private const string FOLD = "_wh_fold_digest_epoch";
  private const string REFUSED_TRUNCATED = "repair refused: stream prefix truncated";
  private static readonly Guid _zeroLane = Guid.Empty;

  /// <summary>One returned row: (bundle, stream, tenant, type) and its counts.</summary>
  private sealed record Row(int Bundle, Guid StreamId, string Tenant, string EventType, int Present, int Adopted,
    int OriginsFilled, int RefusedTruncated, int RefusedForeignOrigin);

  /// <summary>One bundle as the batch takes it.</summary>
  private sealed record Bundle(Guid Origin, string? Name, params (Guid EventId, long? Sequence)[] Events);

  [Test]
  public async Task Batch_FillsEverySequence_AsTheSingleBundleCallsWouldAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var first = await _storeAsync(conn, stream, 1, origin);
    var second = await _storeAsync(conn, stream, 2, origin);
    var third = await _storeAsync(conn, stream, 3, origin);

    var rows = await _reconcileAsync(conn,
      new Bundle(origin, ORIGIN_NAME, (first, 11)), new Bundle(origin, ORIGIN_NAME, (second, 12)),
      new Bundle(origin, ORIGIN_NAME, (third, 13)));

    await Assert.That(rows).IsEquivalentTo([
      new Row(1, stream, TENANT, EVENT_TYPE, 1, 1, 0, 0, 0),
      new Row(2, stream, TENANT, EVENT_TYPE, 1, 1, 0, 0, 0),
      new Row(3, stream, TENANT, EVENT_TYPE, 1, 1, 0, 0, 0),
    ]).Because("each event already carried its origin, so each bundle filled only its sequence");
    await Assert.That(await _originOfAsync(conn, first)).IsEqualTo((origin, 11L));
    await Assert.That(await _originOfAsync(conn, second)).IsEqualTo((origin, 12L));
    await Assert.That(await _originOfAsync(conn, third)).IsEqualTo((origin, 13L));
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, origin, stream)).IsEqualTo(1L)
      .Because("the events never left their lane, so the digest does not move");
  }

  [Test]
  public async Task Batch_RefoldsEachSealedEpochOnce_NotOncePerBundleAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _sealAsync(conn, origin, closedThrough: 0, width: 1000);
    var bundles = new List<Bundle>();
    for (var i = 1; i <= 50; i++) {
      bundles.Add(new Bundle(origin, ORIGIN_NAME, (await _storeAsync(conn, TrackedGuid.New().Value, 1, origin), i)));
    }

    var (rows, folds) = await _reconcileCountingFoldsAsync(conn, [.. bundles]);

    await Assert.That(rows.Sum(r => r.Adopted)).IsEqualTo(50);
    await Assert.That(folds).IsEqualTo(1L)
      .Because("the fifty sequences all fall in one sealed epoch, which is folded once for the batch");
    await Assert.That(await _scalarAsync<int>(conn,
        "SELECT sum(event_count)::int FROM wh_digest_epochs WHERE origin_service_id = $1 AND epoch_id = 0", origin))
      .IsEqualTo(50).Because("the one fold holds every event the batch gave a sequence");
  }

  /// <summary>
  /// The measurement behind #2099: a backlog of one-event bundles that each fill one sequence, drained by the
  /// single-bundle reconcile (one call per bundle) and by the batch reconcile, on two identical seeded lanes.
  /// </summary>
  [Test]
  public async Task Batch_DrainingASeededBacklog_FoldsEachEpochOnce_WhereTheSingleBundleCallsFoldEveryBundleAsync() {
    const int backlog = 1000;
    const int width = 250;
    await using var conn = await _openAsync();
    var perBundle = TrackedGuid.New().Value;
    var batched = TrackedGuid.New().Value;
    await _sealAsync(conn, perBundle, closedThrough: 4, width: width);
    await _sealAsync(conn, batched, closedThrough: 4, width: width);
    var perBundleEvents = await _storeManyAsync(conn, perBundle, backlog);
    var batchedEvents = await _storeManyAsync(conn, batched, backlog);

    var singleFolds = await _singleBundleCallsCountingFoldsAsync(conn, perBundle, perBundleEvents);
    var (rows, batchFolds) = await _reconcileCountingFoldsAsync(conn,
      [.. batchedEvents.Select((e, i) => new Bundle(batched, ORIGIN_NAME, (e, i + 1)))]);
    // The fold count is the measure: it is deterministic, where elapsed time depends on the machine.
    TestContext.Current?.Output.WriteLine(
      $"#2099 backlog of {backlog} one-event bundles over 5 sealed epochs of {width}: single-bundle calls "
      + $"{singleFolds} epoch folds; batch {batchFolds} epoch folds");

    await Assert.That(rows.Sum(r => r.Adopted)).IsEqualTo(backlog);
    await Assert.That(singleFolds).IsEqualTo(backlog)
      .Because("precondition: the single-bundle reconcile folds the epoch of every bundle it moves");
    await Assert.That(batchFolds).IsEqualTo(5L)
      .Because("the batch folds each of the five sealed epochs its sequences 1 to 1000 fall in once");
    await Assert.That(await _epochsAsync(conn, batched)).IsEquivalentTo(await _epochsAsync(conn, perBundle))
      .Because("both drains leave the same sealed epochs");
  }

  [Test]
  public async Task Batch_AnEventTwoBundlesName_AdoptsOnce_ForTheFirstBundleAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1);

    var rows = await _reconcileAsync(conn,
      new Bundle(origin, ORIGIN_NAME, (received, 5)), new Bundle(origin, ORIGIN_NAME, (received, 5)));

    await Assert.That(rows).IsEquivalentTo([
      new Row(1, stream, TENANT, EVENT_TYPE, 1, 1, 1, 0, 0),
      new Row(2, stream, TENANT, EVENT_TYPE, 1, 0, 0, 0, 0),
    ]).Because("the second bundle finds the event the first one filled, as a later call would");
    await Assert.That(await _originOfAsync(conn, received)).IsEqualTo((origin, 5L));
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, origin, stream)).IsEqualTo(1L)
      .Because("the hash moves into the origin's lane once");
    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_stream_digests WHERE origin_service_id = $1 AND stream_id = $2", _zeroLane, stream))
      .IsEqualTo(0L).Because("the local bucket the event left is empty and removed");
  }

  [Test]
  public async Task Batch_AnUnsequencedBundleBeforeASequencedOne_TakesTheSequenceOfTheSameOriginAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1);

    _ = await _reconcileAsync(conn,
      new Bundle(origin, ORIGIN_NAME, (received, null)), new Bundle(origin, ORIGIN_NAME, (received, 7)));

    await Assert.That(await _originOfAsync(conn, received)).IsEqualTo((origin, 7L))
      .Because("one call after the other would have filled the origin and then the sequence");
  }

  [Test]
  public async Task Batch_EventsThatAlreadyCarryTheirOrigin_AdoptNothing_AndRefoldNothingAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await _sealAsync(conn, origin, closedThrough: 0, width: 1000);
    var recorded = await _storeAsync(conn, stream, 1, origin, 5);
    var unsequencedRedelivery = await _storeAsync(conn, stream, 2, origin);

    var (rows, folds) = await _reconcileCountingFoldsAsync(conn,
      new Bundle(origin, ORIGIN_NAME, (recorded, 5)), new Bundle(origin, ORIGIN_NAME, (unsequencedRedelivery, null)));

    await Assert.That(rows.Sum(r => r.Present)).IsEqualTo(2);
    await Assert.That(rows.Sum(r => r.Adopted)).IsEqualTo(0)
      .Because("an event carrying its origin and sequence, or redelivered without a sequence to give, changes nothing");
    await Assert.That(folds).IsEqualTo(0L).Because("nothing moved, so no sealed epoch is refolded");
    await Assert.That(await _originOfAsync(conn, recorded)).IsEqualTo((origin, 5L));
  }

  [Test]
  public async Task Batch_ReportsOriginsFilled_ApartFromSequencesFilledAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var withoutOrigin = await _storeAsync(conn, stream, 1);
    var withoutSequence = await _storeAsync(conn, stream, 2, origin);

    var rows = await _reconcileAsync(conn, new Bundle(origin, ORIGIN_NAME, (withoutOrigin, 3), (withoutSequence, 4)));

    await Assert.That(rows).IsEquivalentTo([new Row(1, stream, TENANT, EVENT_TYPE, 2, 2, 1, 0, 0)])
      .Because("both events adopted, but only one had its origin filled; the other already carried it");
  }

  [Test]
  public async Task Batch_AFirstHopNamingAnotherService_IsRefused_AndALaterBundleOfThatServiceAdoptsAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var author = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var stored = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, stored, $"[{_hop(LOCAL_NAME)}]");

    var rows = await _reconcileAsync(conn,
      new Bundle(origin, ORIGIN_NAME, (stored, 3)), new Bundle(author, LOCAL_NAME, (stored, 8)));

    await Assert.That(rows).IsEquivalentTo([
      new Row(1, stream, TENANT, EVENT_TYPE, 1, 0, 0, 0, 1),
      new Row(2, stream, TENANT, EVENT_TYPE, 1, 1, 1, 0, 0),
    ]).Because("the first bundle's origin is not the event's author; the second bundle's is");
    await Assert.That(await _originOfAsync(conn, stored)).IsEqualTo((author, 8L));
  }

  [Test]
  public async Task Batch_ARefusedEventNoBundleCanFill_StaysLocal_AndEveryBundleCountsTheRefusalAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var stored = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, stored, $"[{_hop(LOCAL_NAME)}]");

    var rows = await _reconcileAsync(conn,
      new Bundle(TrackedGuid.New().Value, ORIGIN_NAME, (stored, 3)), new Bundle(TrackedGuid.New().Value, "billing", (stored, 4)));

    await Assert.That(rows.Select(r => r.RefusedForeignOrigin)).IsEquivalentTo([1, 1]);
    await Assert.That(await _originOfAsync(conn, stored)).IsEqualTo(((Guid?)null, (long?)null));
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _zeroLane, stream)).IsEqualTo(1L)
      .Because("a refused event keeps its local digest");
  }

  [Test]
  public async Task Batch_MovesDigestsFromTheLocalLane_AndRefoldsTheLocalLaneOnceAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var first = await _storeAsync(conn, stream, 1);
    var second = await _storeAsync(conn, stream, 2);
    var stays = await _storeAsync(conn, stream, 3);
    var localSequence = await _scalarAsync<long>(conn, "SELECT commit_sequence FROM wh_event_store WHERE event_id = $1", first);
    await _sealAsync(conn, _zeroLane, closedThrough: (localSequence / 1_000_000) + 1, width: 1_000_000);
    var generation = await _generationAsync(conn);

    var (_, folds) = await _reconcileCountingFoldsAsync(conn,
      new Bundle(origin, ORIGIN_NAME, (first, 1)), new Bundle(origin, ORIGIN_NAME, (second, 2)));

    await Assert.That(folds).IsLessThanOrEqualTo(2L).And.IsGreaterThanOrEqualTo(1L)
      .Because("the local lane's sealed epochs holding the two moved events are each folded once, not once per bundle");
    await Assert.That(await _generationAsync(conn)).IsEqualTo(generation + 1)
      .Because("this service's sealed history changed once, so the origin generation moves once");
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, origin, stream)).IsEqualTo(1L);
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _zeroLane, stream)).IsEqualTo(1L)
      .Because("the event no bundle named stays in the local lane, and its digest with it");
    await Assert.That(await _originOfAsync(conn, stays)).IsEqualTo(((Guid?)null, (long?)null));
    // #2106: the origin's new bucket carries its id lanes; the hand-seeded local one keeps NULL.
    _ = await StoredDigestInvariant.AssertHashLanesAgreeAsync(conn, stream);
    await StoredDigestInvariant.AssertIdLanesAgreeAsync(conn, filled: 1, stream);
  }

  [Test]
  public async Task Batch_NamingThisServiceOrTheEmptyOrigin_AdoptsNothingAsync() {
    await using var conn = await _openAsync();
    var self = await _scalarAsync<Guid>(conn, "SELECT service_id FROM wh_service_config LIMIT 1");
    var stream = TrackedGuid.New().Value;
    var local = await _storeAsync(conn, stream, 1);

    var rows = await _reconcileAsync(conn, new Bundle(self, LOCAL_NAME, (local, 4)), new Bundle(Guid.Empty, null, (local, 4)));

    await Assert.That(rows.Sum(r => r.Adopted)).IsEqualTo(0);
    await Assert.That(await _originOfAsync(conn, local)).IsEqualTo(((Guid?)null, (long?)null));
  }

  [Test]
  public async Task Batch_EventsTheStoreDoesNotHold_AreNotReportedAsync() {
    await using var conn = await _openAsync();

    var rows = await _reconcileAsync(conn, new Bundle(TrackedGuid.New().Value, ORIGIN_NAME, (TrackedGuid.New().Value, 1)));

    await Assert.That(rows).IsEmpty().Because("the bundle stores those itself; they are not present yet");
  }

  [Test]
  public async Task Batch_ReportsTheChildrenTheStoreRefusedToRestore_PerBundleAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var refused = TrackedGuid.New().Value;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        WITH m AS (
          INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, stream_id, scope)
          VALUES ($1, 'TestHandler', $2, '{}'::jsonb, '{}'::jsonb, $3, jsonb_build_object('t', $4::text))
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, attempts, status, failure_reason, error)
        SELECT message_id, stream_id, received_at, priority, is_event, 1, 1, 99, $5 FROM m
        """;
      cmd.Parameters.AddWithValue(refused);
      cmd.Parameters.AddWithValue(EVENT_TYPE);
      cmd.Parameters.AddWithValue(stream);
      cmd.Parameters.AddWithValue(TENANT);
      cmd.Parameters.AddWithValue(REFUSED_TRUNCATED);
      await cmd.ExecuteNonQueryAsync();
    }

    var rows = await _reconcileAsync(conn, new Bundle(origin, ORIGIN_NAME, (refused, 1)), new Bundle(origin, ORIGIN_NAME, (refused, 1)));

    await Assert.That(rows.Select(r => (r.Bundle, r.RefusedTruncated, r.Present))).IsEquivalentTo([(1, 1, 0), (2, 1, 0)])
      .Because("each bundle that named the refused child is told so, keyed as the digests key it");
  }

  [Test]
  [Arguments(1, 2, 2)]
  [Arguments(2, 1, 1)]
  [Arguments(2, 2, 1)]
  public async Task Batch_MismatchedArrays_AreRefusedAsync(int names, int bundlePositions, int sequences) {
    await using var conn = await _openAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_reconcile_redelivered_bundles($1, $2, $3, $4, $5)";
    cmd.Parameters.AddWithValue(new[] { TrackedGuid.New().Value, TrackedGuid.New().Value });
    cmd.Parameters.AddWithValue(Enumerable.Repeat(ORIGIN_NAME, names).ToArray());
    cmd.Parameters.AddWithValue(Enumerable.Repeat(1, bundlePositions).ToArray());
    cmd.Parameters.AddWithValue(new[] { TrackedGuid.New().Value, TrackedGuid.New().Value });
    cmd.Parameters.AddWithValue(Enumerable.Repeat(1L, sequences).ToArray());

    await Assert.That(async () => await cmd.ExecuteScalarAsync()).Throws<PostgresException>()
      .Because("bundles pair with their names, and events with their bundle positions and sequences, by position");
  }

  /// <summary>
  /// Under the generic plan a long-lived connection settles on, the batch finds each stored event, body and digest it
  /// changes by key, never by reading the whole event store; and it takes no row lock before the stream locks.
  /// </summary>
  [Test]
  public async Task Batch_UnderAGenericPlan_FindsStoredEventsByKey_NotAStoreScanAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    // A wide store, analyzed, so a scan of it is visible as thousands of rows where the batch needs a handful.
    await _execAsync(conn, """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, created_at)
      SELECT gen_random_uuid(), s, s, 'Order', 'Contracts.Other, Contracts', NULL, 1, nextval('wh_commit_seq'), NOW()
      FROM (SELECT gen_random_uuid() AS s FROM generate_series(1, 5000)) g;
      ANALYZE wh_event_store;
      """);
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, received, $"[{_hop(ORIGIN_NAME)}]");
    var unsequenced = await _storeAsync(conn, stream, 2, origin);
    await _execAsync(conn, "SET plan_cache_mode = force_generic_plan");

    var nodes = await _plansOfOneBatchAsync(conn,
      new Bundle(origin, ORIGIN_NAME, (received, 3)), new Bundle(origin, ORIGIN_NAME, (unsequenced, 4)));

    var storeReads = nodes.Where(n => n.Relation == "wh_event_store").ToList();
    await Assert.That(storeReads).IsNotEmpty().Because("precondition: the batch reads the events it reconciles");
    await Assert.That(storeReads.Any(n => n.Type == "Seq Scan")).IsFalse()
      .Because("each stored event is found by its key; a sequential scan reads the whole store on every batch");
    await Assert.That(storeReads.Max(n => n.RowsReturned)).IsLessThanOrEqualTo(4)
      .Because("the batch names two events");
    await Assert.That(nodes.Where(n => n.Relation is "wh_event_body" or "wh_stream_digests").All(n => n.Type != "Seq Scan"))
      .IsTrue();
  }

  // ─── Fixture ────────────────────────────────────────────────────────────────────────────────────────────────────────

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>Stores one event as the emit chain does, folding its digest into the lane its origin names.</summary>
  private static async Task<Guid> _storeAsync(NpgsqlConnection conn, Guid stream, int version, Guid? origin = null,
      long? originSequence = null) {
    var eventId = TrackedGuid.New().Value;
    await using (var store = conn.CreateCommand()) {
      store.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, created_at, origin_service_id, origin_commit_sequence)
        VALUES ($1, $2, $2, 'Order', $3, jsonb_build_object('t', $4::text), $5, nextval('wh_commit_seq'),
                NOW() - INTERVAL '2 hours', $6, $7)
        """;
      store.Parameters.AddWithValue(eventId);
      store.Parameters.AddWithValue(stream);
      store.Parameters.AddWithValue(EVENT_TYPE);
      store.Parameters.AddWithValue(TENANT);
      store.Parameters.AddWithValue(version);
      store.Parameters.Add(new NpgsqlParameter { Value = (object?)origin ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
      store.Parameters.Add(new NpgsqlParameter { Value = (object?)originSequence ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
      await store.ExecuteNonQueryAsync();
    }
    await using var fold = conn.CreateCommand();
    fold.CommandText = """
      INSERT INTO wh_stream_digests AS d (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count)
      VALUES ($1, $2, $3, $4, hashtextextended($5::text, 0), hashtextextended($5::text, 1), 1)
      ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
        digest_lo = d.digest_lo # EXCLUDED.digest_lo, digest_hi = d.digest_hi # EXCLUDED.digest_hi,
        event_count = d.event_count + 1
      """;
    fold.Parameters.AddWithValue(origin ?? _zeroLane);
    fold.Parameters.AddWithValue(TENANT);
    fold.Parameters.AddWithValue(EVENT_TYPE);
    fold.Parameters.AddWithValue(stream);
    fold.Parameters.AddWithValue(eventId);
    await fold.ExecuteNonQueryAsync();
    return eventId;
  }

  /// <summary><paramref name="count"/> events of <paramref name="origin"/> without their sequence, one per stream.</summary>
  private static async Task<List<Guid>> _storeManyAsync(NpgsqlConnection conn, Guid origin, int count) {
    var ids = new List<Guid>(count);
    for (var i = 0; i < count; i++) {
      ids.Add(await _storeAsync(conn, TrackedGuid.New().Value, 1, origin));
    }
    return ids;
  }

  private static async Task _storeBodyAsync(NpgsqlConnection conn, Guid eventId, string hops) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      VALUES ($1, '{}'::jsonb, jsonb_build_object('MessageId', $1::text, 'Hops', $2::jsonb))
      """;
    cmd.Parameters.AddWithValue(eventId);
    cmd.Parameters.AddWithValue(hops);
    await cmd.ExecuteNonQueryAsync();
  }

  private static string _hop(string service) =>
    $$"""{"ty":0,"si":{"sn":"{{service}}","ii":"{{TrackedGuid.New().Value}}","hn":"host","pi":1},"ts":"2026-10-28T12:00:00+00:00"}""";

  private static async Task _sealAsync(NpgsqlConnection conn, Guid lane, long closedThrough, long width) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_digest_epoch_frontiers (origin_service_id, closed_through_epoch, epoch_width, updated_at)
      VALUES ($1, $2, $3, NOW())
      ON CONFLICT (origin_service_id) DO UPDATE SET closed_through_epoch = EXCLUDED.closed_through_epoch,
        epoch_width = EXCLUDED.epoch_width
      """;
    cmd.Parameters.AddWithValue(lane);
    cmd.Parameters.AddWithValue(closedThrough);
    cmd.Parameters.AddWithValue(width);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _generationAsync(NpgsqlConnection conn) =>
    long.Parse(await _scalarAsync<string>(conn,
      "SELECT COALESCE((SELECT setting_value FROM wh_settings WHERE setting_key = 'integrity_origin_generation'), '0')"),
      System.Globalization.CultureInfo.InvariantCulture);

  private static async Task<List<(long Epoch, string Type, int Count)>> _epochsAsync(NpgsqlConnection conn, Guid lane) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT epoch_id, event_type, event_count FROM wh_digest_epochs WHERE origin_service_id = $1 ORDER BY 1, 2";
    cmd.Parameters.AddWithValue(lane);
    var epochs = new List<(long, string, int)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      epochs.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
    }
    return epochs;
  }

  private static NpgsqlCommand _batchCommand(NpgsqlConnection conn, Bundle[] bundles) {
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT bundle, stream_id, tenant_scope, event_type, present_count, adopted_count, origins_filled_count,
             refused_truncated_count, refused_foreign_origin_count
      FROM wh_reconcile_redelivered_bundles($1, $2, $3, $4, $5)
      """;
    cmd.Parameters.AddWithValue(bundles.Select(b => b.Origin).ToArray());
    cmd.Parameters.Add(new NpgsqlParameter {
      Value = bundles.Select(b => (object?)b.Name ?? DBNull.Value).ToArray(),
      NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
    });
    cmd.Parameters.AddWithValue(bundles.SelectMany((b, i) => b.Events.Select(_ => i + 1)).ToArray());
    cmd.Parameters.AddWithValue(bundles.SelectMany(b => b.Events.Select(e => e.EventId)).ToArray());
    cmd.Parameters.Add(new NpgsqlParameter {
      Value = bundles.SelectMany(b => b.Events.Select(e => (object?)e.Sequence ?? DBNull.Value)).ToArray(),
      NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint,
    });
    return cmd;
  }

  private static async Task<List<Row>> _reconcileAsync(NpgsqlConnection conn, params Bundle[] bundles) {
    await using var cmd = _batchCommand(conn, bundles);
    var rows = new List<Row>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new Row(reader.GetInt32(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
        reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8)));
    }
    return rows;
  }

  /// <summary>One batch in its own transaction, with the number of epoch folds it ran.</summary>
  private static async Task<(List<Row> Rows, long Folds)> _reconcileCountingFoldsAsync(NpgsqlConnection conn, params Bundle[] bundles) {
    await _execAsync(conn, "BEGIN; SET LOCAL track_functions = 'all'");
    var rows = await _reconcileAsync(conn, bundles);
    var folds = await _foldsSoFarAsync(conn);
    await _execAsync(conn, "COMMIT");
    return (rows, folds);
  }

  /// <summary>The single-bundle reconcile once per event, in one transaction, with the number of epoch folds it ran.</summary>
  private static async Task<long> _singleBundleCallsCountingFoldsAsync(NpgsqlConnection conn, Guid origin, List<Guid> events) {
    await _execAsync(conn, "BEGIN; SET LOCAL track_functions = 'all'");
    for (var i = 0; i < events.Count; i++) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT count(*) FROM wh_reconcile_redelivered_bundle_by_first_hop($1, $2, $3, $4)";
      cmd.Parameters.AddWithValue(origin);
      cmd.Parameters.AddWithValue(ORIGIN_NAME);
      cmd.Parameters.AddWithValue(new[] { events[i] });
      cmd.Parameters.AddWithValue(new long[] { i + 1 });
      _ = await cmd.ExecuteScalarAsync();
    }
    var folds = await _foldsSoFarAsync(conn);
    await _execAsync(conn, "COMMIT");
    return folds;
  }

  private static Task<long> _foldsSoFarAsync(NpgsqlConnection conn) =>
    _scalarAsync<long>(conn, $"SELECT COALESCE(sum(calls), 0)::bigint FROM pg_stat_xact_user_functions WHERE funcname = '{FOLD}'");

  private static async Task<IReadOnlyList<PlanNode>> _plansOfOneBatchAsync(NpgsqlConnection conn, params Bundle[] bundles) {
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
    _ = await _reconcileAsync(conn, bundles);
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
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        _flatten(child, nodes);
      }
    }
    nodes.Add(new PlanNode(
      node.GetProperty("Node Type").GetString()!,
      node.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
      (long)(node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble())));
  }

  private sealed record PlanNode(string Type, string? Relation, long RowsReturned);

  private static async Task<(Guid? Origin, long? Sequence)> _originOfAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT origin_service_id, origin_commit_sequence FROM wh_event_store WHERE event_id = $1";
    cmd.Parameters.AddWithValue(eventId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (await reader.IsDBNullAsync(0) ? null : reader.GetGuid(0), await reader.IsDBNullAsync(1) ? null : reader.GetInt64(1));
  }

  /// <summary>How many digest buckets of the lane equal a recompute over the stream's events in that lane.</summary>
  private static Task<long> _bucketsMatchingTheStoreAsync(NpgsqlConnection conn, Guid lane, Guid stream) =>
    _scalarAsync<long>(conn, """
      SELECT count(*) FROM (
        SELECT event_type, bit_xor(hashtextextended(event_id::text, 0)) AS lo, bit_xor(hashtextextended(event_id::text, 1)) AS hi, count(*)::int AS n
        FROM wh_event_store
        WHERE stream_id = $2 AND COALESCE(origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid) = $1
        GROUP BY event_type
      ) e
      JOIN wh_stream_digests d ON d.origin_service_id = $1 AND d.stream_id = $2 AND d.event_type = e.event_type
      WHERE d.digest_lo = e.lo AND d.digest_hi = e.hi AND d.event_count = e.n
      """, lane, stream);

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }
}
