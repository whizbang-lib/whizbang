// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// History received before the store recorded origins, reconciled by the redelivery that integrity repair asks for
/// (#1790), against the real <c>wh_adopt_redelivered_origin</c> (migration 249). An event stored with empty origin
/// columns adopts the origin identity the bundle carries, its digest leaves the local lane for the origin's, and the
/// sealed epochs it touched are refolded, so the next check counts it instead of asking for it again. An event that
/// already carries an origin is never re-attributed, and an event whose first hop names another service than the
/// bundle's origin adopts nothing and is counted as refused (#1844, migration 253).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/249_RedeliveredHistoryAdoptsItsOrigin.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/253_RedeliveredOriginChecksTheFirstHop.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#pre-stamp-history</docs>
[Category("Integration")]
[Category("Shard5")]
public class RedeliveredOriginAdoptionSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Contracts.OrderPlaced, Contracts";
  private const string TENANT = "tenant-a";
  private const int FLAG_EPHEMERAL = 8;
  private static readonly Guid _zeroLane = Guid.Empty;

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  /// <summary>
  /// Stores one event as the emit chain does, folding its digest into the lane its origin names (the local lane when
  /// it has none). An event stored before origins were recorded is one with <paramref name="origin"/> null.
  /// </summary>
  private static async Task<Guid> _storeAsync(NpgsqlConnection conn, Guid stream, int version, Guid? origin = null,
      long? originSequence = null, int flags = 0, string tenant = TENANT) {
    var eventId = TrackedGuid.New().Value;
    await using (var store = conn.CreateCommand()) {
      store.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
        VALUES (@event, @stream, @stream, 'Order', @type, jsonb_build_object('t', @tenant), @version,
                nextval('wh_commit_seq'), @flags, NOW() - INTERVAL '2 hours', @origin, @oseq)
        """;
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue(nameof(stream), stream);
      store.Parameters.AddWithValue("type", EVENT_TYPE);
      store.Parameters.AddWithValue(nameof(tenant), tenant);
      store.Parameters.AddWithValue(nameof(version), version);
      store.Parameters.AddWithValue(nameof(flags), flags);
      store.Parameters.AddWithValue(nameof(origin), (object?)origin ?? DBNull.Value);
      store.Parameters.AddWithValue("oseq", (object?)originSequence ?? DBNull.Value);
      await store.ExecuteNonQueryAsync();
    }
    if ((flags & FLAG_EPHEMERAL) != 0) {
      return eventId;
    }
    await using var fold = conn.CreateCommand();
    fold.CommandText = """
      INSERT INTO wh_stream_digests AS d (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count)
      VALUES (@lane, @tenant, @type, @stream, hashtextextended(@event::text, 0), hashtextextended(@event::text, 1), 1)
      ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
        digest_lo = d.digest_lo # EXCLUDED.digest_lo, digest_hi = d.digest_hi # EXCLUDED.digest_hi,
        event_count = d.event_count + 1
      """;
    fold.Parameters.AddWithValue("lane", origin ?? _zeroLane);
    fold.Parameters.AddWithValue(nameof(tenant), tenant);
    fold.Parameters.AddWithValue("type", EVENT_TYPE);
    fold.Parameters.AddWithValue(nameof(stream), stream);
    fold.Parameters.AddWithValue("event", eventId);
    await fold.ExecuteNonQueryAsync();
    return eventId;
  }

  /// <summary>
  /// Stores the event's body with <paramref name="hops"/> as its metadata's hop list, the form the emit chain and the
  /// event store write (<c>{"MessageId": ..., "Hops": [...]}</c>, each hop's service under <c>si.sn</c>).
  /// </summary>
  private static async Task _storeBodyAsync(NpgsqlConnection conn, Guid eventId, string hops) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      VALUES (@event, '{}'::jsonb, jsonb_build_object('MessageId', @event::text, 'Hops', @hops::jsonb))
      """;
    cmd.Parameters.AddWithValue("event", eventId);
    cmd.Parameters.AddWithValue(nameof(hops), hops);
    await cmd.ExecuteNonQueryAsync();
  }

  private static string _hop(string service) =>
    $$"""{"ty":0,"si":{"sn":"{{service}}","ii":"{{TrackedGuid.New().Value}}","hn":"host","pi":1},"ts":"2026-10-28T12:00:00+00:00"}""";

  private const string ORIGIN_NAME = "orders-service";
  private const string LOCAL_NAME = "inventory-service";

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<(Guid? Origin, long? Sequence)> _originOfAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT origin_service_id, origin_commit_sequence FROM wh_event_store WHERE event_id = $1";
    cmd.Parameters.Add(new NpgsqlParameter { Value = eventId });
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

  [Test]
  public async Task Adopt_EventsStoredWithoutAnOrigin_TakeTheBundlesOriginAndSequencesAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var first = await _storeAsync(conn, stream, 1);
    var second = await _storeAsync(conn, stream, 2);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [first, second], [7, 8]);

    await Assert.That(buckets).IsNotNull();
    await Assert.That(buckets).IsEquivalentTo([new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, Present: 2, Adopted: 2)]);
    await Assert.That(await _originOfAsync(conn, first)).IsEqualTo((origin, 7L));
    await Assert.That(await _originOfAsync(conn, second)).IsEqualTo((origin, 8L));
  }

  [Test]
  public async Task Adopt_AnEventWithNoTenant_ReportsItsBucketWithoutATenantAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var stored = await _storeAsync(conn, stream, 1, tenant: "");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [stored], [3]);

    await Assert.That(buckets).IsEquivalentTo([new RedeliveredBucketPresence(stream, null, EVENT_TYPE, Present: 1, Adopted: 1)])
      .Because("the ledger keys an untenanted bucket by the empty scope, which the coordinator reports as no tenant");
    await Assert.That(await _originOfAsync(conn, stored)).IsEqualTo((origin, 3L));
  }

  [Test]
  public async Task Adopt_TheNextCheckpointCountsTheAdoptedEvents_InsteadOfAskingForThemAgainAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var first = await _storeAsync(conn, stream, 1);
    var second = await _storeAsync(conn, stream, 2);
    var before = await _coordinator().CountReceivedFromOriginAsync(origin, 0, 10);
    await Assert.That(before).IsEmpty().Because("setup: history without an origin reads as missing");

    _ = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [first, second], [7, 8]);

    var after = await _coordinator().CountReceivedFromOriginAsync(origin, 0, 10);
    await Assert.That(after.Single(b => b.EventType == EVENT_TYPE).Count).IsEqualTo(2)
      .Because("the window the origin checkpoints now finds the events it would otherwise re-request forever");
  }

  [Test]
  public async Task Adopt_MovesTheDigestFromTheLocalLaneToTheOriginsLane_LeavingLocalEventsWhereTheyAreAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1);
    var redeliveredOnly = await _storeAsync(conn, stream, 2);
    _ = await _storeAsync(conn, stream, 3);   // an event this service authored stays local

    _ = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [received, redeliveredOnly], [7, 8]);

    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, origin, stream)).IsEqualTo(1L)
      .Because("the origin's lane now holds exactly the adopted events, as a recompute would fold them");
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _zeroLane, stream)).IsEqualTo(1L)
      .Because("the local lane keeps exactly the event that was never received");
  }

  [Test]
  public async Task Adopt_TheLastEventOfALocalBucket_RemovesThatBucketAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1);

    _ = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [received], [7]);

    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_stream_digests WHERE origin_service_id = $1 AND stream_id = $2", _zeroLane, stream))
      .IsEqualTo(0L).Because("an empty local bucket would advertise the type as this service's own");
  }

  [Test]
  public async Task Adopt_AnEventWithTheOriginButNoSequence_TakesOnlyTheSequenceAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var unsequenced = await _storeAsync(conn, stream, 1, origin);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [unsequenced], [42]);

    await Assert.That(buckets!.Single().Adopted).IsEqualTo(1);
    await Assert.That(await _originOfAsync(conn, unsequenced)).IsEqualTo((origin, 42L));
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, origin, stream)).IsEqualTo(1L)
      .Because("the event never left its lane, so its digest does not move");
  }

  [Test]
  public async Task Adopt_AnEventAlreadyCarryingTheOrigin_IsPresentButUnchangedAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var stored = await _storeAsync(conn, stream, 1, origin, 5);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [stored], [99]);

    await Assert.That(buckets).IsEquivalentTo([new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, 1, 0)]);
    await Assert.That(await _originOfAsync(conn, stored)).IsEqualTo((origin, 5L))
      .Because("a recorded position is never overwritten by a redelivery");
  }

  [Test]
  public async Task Adopt_AnUnsequencedRedeliveryOfAnUnsequencedEvent_ChangesNothingAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var unsequenced = await _storeAsync(conn, stream, 1, origin);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [unsequenced], [null]);

    await Assert.That(buckets!.Single().Adopted).IsEqualTo(0)
      .Because("the bundle has no position to give, so nothing is learned");
  }

  [Test]
  public async Task Adopt_AnEventReceivedFromAnotherOrigin_IsNeverReattributedAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var other = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var stored = await _storeAsync(conn, stream, 1, other, 3);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [stored], [3]);

    await Assert.That(buckets!.Single()).IsEqualTo(new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, 1, 0));
    await Assert.That(await _originOfAsync(conn, stored)).IsEqualTo((other, 3L));
  }

  [Test]
  public async Task Adopt_EventsTheStoreDoesNotHold_AreNotReportedAsync() {
    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(TrackedGuid.New().Value, null, [TrackedGuid.New().Value], [1]);

    await Assert.That(buckets).IsEmpty().Because("the bundle stores those itself; they are not present yet");
  }

  [Test]
  public async Task Adopt_NamingThisServiceAsTheOrigin_AdoptsNothingAsync() {
    await using var conn = await _openAsync();
    var self = await _scalarAsync<Guid>(conn, "SELECT service_id FROM wh_service_config LIMIT 1");
    var stream = TrackedGuid.New().Value;
    var local = await _storeAsync(conn, stream, 1);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(self, null, [local], [4]);

    await Assert.That(buckets!.Single().Adopted).IsEqualTo(0);
    await Assert.That(await _originOfAsync(conn, local)).IsEqualTo(((Guid?)null, (long?)null))
      .Because("a locally-originated event keeps no origin stamp (the 046 contract)");
  }

  [Test]
  public async Task Adopt_TheEmptyOrigin_AdoptsNothingAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var local = await _storeAsync(conn, stream, 1);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(Guid.Empty, null, [local], [4]);

    await Assert.That(buckets!.Single().Adopted).IsEqualTo(0);
  }

  [Test]
  public async Task Adopt_AnEventDigestsLeaveOut_TakesTheOriginWithoutTouchingAnyDigestAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var ephemeral = await _storeAsync(conn, stream, 1, flags: FLAG_EPHEMERAL);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [ephemeral], [6]);

    await Assert.That(buckets!.Single().Adopted).IsEqualTo(1);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_stream_digests WHERE stream_id = $1", stream))
      .IsEqualTo(0L).Because("an ephemeral event is in no digest, before or after");
  }

  [Test]
  public async Task Adopt_IsSafeToRepeat_ASecondBundleAdoptsNothingAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1);
    _ = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [received], [7]);

    var again = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [received], [7]);

    await Assert.That(again!.Single()).IsEqualTo(new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, 1, 0));
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, origin, stream)).IsEqualTo(1L)
      .Because("a repeat never folds the hash a second time");
  }

  [Test]
  public async Task Adopt_IntoSealedEpochs_RefoldsBothLanes_AndAdvancesTheOriginGenerationAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1);
    await using (var seal = conn.CreateCommand()) {
      seal.CommandText = """
        INSERT INTO wh_digest_epoch_frontiers (origin_service_id, closed_through_epoch, epoch_width, updated_at)
        VALUES ($1, 0, 1000, NOW()), ($2, 0, 1000, NOW())
        """;
      seal.Parameters.Add(new NpgsqlParameter { Value = _zeroLane });
      seal.Parameters.Add(new NpgsqlParameter { Value = origin });
      await seal.ExecuteNonQueryAsync();
    }
    _ = await _scalarAsync<int>(conn, "SELECT refold_digest_epochs($1, 0, 0)", _zeroLane);
    var generation = await _scalarAsync<string>(conn, "SELECT setting_value FROM wh_settings WHERE setting_key = 'integrity_origin_generation'");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_digest_epochs WHERE origin_service_id = $1", _zeroLane))
      .IsEqualTo(1L).Because("setup: the local lane sealed an epoch holding the received event");

    _ = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [received], [7]);

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_digest_epochs WHERE origin_service_id = $1", _zeroLane))
      .IsEqualTo(0L).Because("the sealed local epoch no longer holds an event that was never this service's");
    await Assert.That(await _scalarAsync<int>(conn,
      "SELECT event_count FROM wh_digest_epochs WHERE origin_service_id = $1 AND epoch_id = 0", origin))
      .IsEqualTo(1).Because("the origin's sealed epoch now holds the adopted event");
    await Assert.That(long.Parse(await _scalarAsync<string>(conn,
      "SELECT setting_value FROM wh_settings WHERE setting_key = 'integrity_origin_generation'"), System.Globalization.CultureInfo.InvariantCulture))
      .IsEqualTo(long.Parse(generation, System.Globalization.CultureInfo.InvariantCulture) + 1)
      .Because("this service's own sealed history changed, so its consumers re-verify instead of alarming");
  }

  [Test]
  public async Task Adopt_AnEventWhoseFirstHopNamesTheBundlesOrigin_AdoptsAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, received, $"[{_hop(ORIGIN_NAME)},{_hop(LOCAL_NAME)}]");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, ORIGIN_NAME, [received], [7]);

    await Assert.That(buckets).IsEquivalentTo([new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, Present: 1, Adopted: 1, RefusedForeignOrigin: 0)]);
    await Assert.That(await _originOfAsync(conn, received)).IsEqualTo((origin, 7L));
  }

  [Test]
  public async Task Adopt_AnEventWhoseFirstHopNamesThisService_IsRefusedAndStaysLocalAsync() {
    // #1844: an origin whose store filed events received from this service as its own redelivers them naming itself.
    // They were authored here, so they keep no origin stamp and their digest stays in the local lane.
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var authoredHere = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, authoredHere, $"[{_hop(LOCAL_NAME)},{_hop(ORIGIN_NAME)}]");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, ORIGIN_NAME, [authoredHere], [7]);

    await Assert.That(buckets).IsEquivalentTo([new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, Present: 1, Adopted: 0, RefusedForeignOrigin: 1)]);
    await Assert.That(await _originOfAsync(conn, authoredHere)).IsEqualTo(((Guid?)null, (long?)null));
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _zeroLane, stream)).IsEqualTo(1L)
      .Because("the refused event's digest never leaves the local lane");
    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_stream_digests WHERE origin_service_id = $1 AND stream_id = $2", origin, stream))
      .IsEqualTo(0L).Because("nothing joined the origin's lane");
  }

  [Test]
  public async Task Adopt_RefusesOnlyTheEventsWhoseFirstHopNamesAnotherService_InTheSameBundleAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, received, $"[{_hop(ORIGIN_NAME)}]");
    var authoredHere = await _storeAsync(conn, stream, 2);
    await _storeBodyAsync(conn, authoredHere, $"[{_hop(LOCAL_NAME)}]");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, ORIGIN_NAME, [received, authoredHere], [7, 8]);

    await Assert.That(buckets).IsEquivalentTo([new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, Present: 2, Adopted: 1, RefusedForeignOrigin: 1)]);
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, origin, stream)).IsEqualTo(1L);
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _zeroLane, stream)).IsEqualTo(1L)
      .Because("each lane holds exactly the events the store now files under it");
  }

  [Test]
  public async Task Adopt_AFirstHopWithoutAServiceIdentity_AdoptsAsync() {
    // The slim stored form drops the hop's service, so the history it covers still converges.
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var slimmed = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, slimmed, """[{"ty":0,"ts":"2026-10-28T12:00:00+00:00","st":"s"}]""");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, ORIGIN_NAME, [slimmed], [7]);

    await Assert.That(buckets!.Single()).IsEqualTo(new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, 1, 1, RefusedForeignOrigin: 0));
    await Assert.That(await _originOfAsync(conn, slimmed)).IsEqualTo((origin, 7L));
  }

  [Test]
  public async Task Adopt_AFirstHopNamingTheUnknownService_AdoptsAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var unknown = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, unknown, $"[{_hop("Unknown")}]");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, ORIGIN_NAME, [unknown], [7]);

    await Assert.That(buckets!.Single().Adopted).IsEqualTo(1)
      .Because("the unknown sentinel is the absence of a service identity, not another service");
  }

  [Test]
  public async Task Adopt_EmptyHops_AdoptsAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var hopless = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, hopless, "[]");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, ORIGIN_NAME, [hopless], [7]);

    await Assert.That(buckets!.Single()).IsEqualTo(new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, 1, 1, RefusedForeignOrigin: 0));
  }

  [Test]
  public async Task Adopt_NoStoredBody_AdoptsAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var bodiless = await _storeAsync(conn, stream, 1);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, ORIGIN_NAME, [bodiless], [7]);

    await Assert.That(buckets!.Single()).IsEqualTo(new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, 1, 1, RefusedForeignOrigin: 0))
      .Because("an event with no stored hops has no service identity to refuse it on");
  }

  /// <summary>Locks a documented first-hop decision; fails if the behavior changes.</summary>
  /// <docs>resilience/stream-integrity#pre-stamp-history-first-hop</docs>
  [Test]
  public async Task Adopt_TheLegacyLongFormHop_IsReadAndComparedWithoutCaseAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, received, """[{"Type":0,"ServiceInstance":{"ServiceName":"Orders-Service"}}]""");
    var authoredHere = await _storeAsync(conn, stream, 2);
    await _storeBodyAsync(conn, authoredHere, """[{"Type":0,"ServiceInstance":{"ServiceName":"Inventory-Service"}}]""");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, ORIGIN_NAME, [received, authoredHere], [7, 8]);

    await Assert.That(buckets!.Single()).IsEqualTo(new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, 2, 1, RefusedForeignOrigin: 1));
    await Assert.That(await _originOfAsync(conn, received)).IsEqualTo((origin, 7L));
    await Assert.That(await _originOfAsync(conn, authoredHere)).IsEqualTo(((Guid?)null, (long?)null));
  }

  /// <summary>Owner decision: a bundle that names no origin service leaves nothing to compare, so it adopts.</summary>
  /// <docs>resilience/stream-integrity#pre-stamp-history-first-hop</docs>
  [Test]
  public async Task Adopt_WithoutTheOriginsName_AdoptsAsBeforeAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var stored = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, stored, $"[{_hop(LOCAL_NAME)}]");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, null, [stored], [7]);

    await Assert.That(buckets!.Single()).IsEqualTo(new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, 1, 1, RefusedForeignOrigin: 0))
      .Because("a bundle that names no service leaves the first-hop check nothing to compare");
  }

  /// <summary>
  /// Owner decision: a blank name, or the Unknown sentinel in any case, is no origin name, so the event adopts even
  /// though its first hop names another service.
  /// </summary>
  /// <docs>resilience/stream-integrity#pre-stamp-history-first-hop</docs>
  [Test]
  [Arguments("")]
  [Arguments("   ")]
  [Arguments("Unknown")]
  [Arguments("UNKNOWN")]
  public async Task Adopt_ABlankOrUnknownOriginName_AdoptsAsBeforeAsync(string originName) {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var stored = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, stored, $"[{_hop(LOCAL_NAME)}]");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, originName, [stored], [7]);

    await Assert.That(buckets!.Single()).IsEqualTo(new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, 1, 1, RefusedForeignOrigin: 0))
      .Because("a name that identifies no service leaves the first-hop check nothing to compare");
    await Assert.That(await _originOfAsync(conn, stored)).IsEqualTo((origin, 7L));
  }

  /// <summary>
  /// Owner decision: service names compare without case, in the short form every current writer stores. The same
  /// service in another case adopts; another service, in whatever case, is refused.
  /// </summary>
  /// <docs>resilience/stream-integrity#pre-stamp-history-first-hop</docs>
  [Test]
  public async Task Adopt_ServiceNamesCompareWithoutCase_AndAnotherServiceInAnyCaseIsRefusedAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var sameService = await _storeAsync(conn, stream, 1);
    await _storeBodyAsync(conn, sameService, $"[{_hop("OrdersService")}]");
    var otherService = await _storeAsync(conn, stream, 2);
    await _storeBodyAsync(conn, otherService, $"[{_hop("INVENTORYSERVICE")}]");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, "ordersservice", [sameService, otherService], [7, 8]);

    await Assert.That(buckets!.Single()).IsEqualTo(new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, 2, 1, RefusedForeignOrigin: 1));
    await Assert.That(await _originOfAsync(conn, sameService)).IsEqualTo((origin, 7L))
      .Because("OrdersService and ordersservice name one service");
    await Assert.That(await _originOfAsync(conn, otherService)).IsEqualTo(((Guid?)null, (long?)null))
      .Because("case never turns another service into the origin");
  }

  [Test]
  public async Task Adopt_AnEventAlreadyCarryingAnOrigin_IsUntouchedWhateverItsFirstHopNamesAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var other = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var recorded = await _storeAsync(conn, stream, 1, other, 3);
    await _storeBodyAsync(conn, recorded, $"[{_hop(LOCAL_NAME)}]");
    var unsequenced = await _storeAsync(conn, stream, 2, origin);
    await _storeBodyAsync(conn, unsequenced, $"[{_hop(LOCAL_NAME)}]");

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(origin, ORIGIN_NAME, [recorded, unsequenced], [3, 9]);

    await Assert.That(buckets!.Single()).IsEqualTo(new RedeliveredBucketPresence(stream, TENANT, EVENT_TYPE, 2, 1, RefusedForeignOrigin: 0))
      .Because("the first-hop check guards only an empty origin; a recorded one already says who the origin is");
    await Assert.That(await _originOfAsync(conn, recorded)).IsEqualTo((other, 3L));
    await Assert.That(await _originOfAsync(conn, unsequenced)).IsEqualTo((origin, 9L));
  }

  [Test]
  public async Task Adopt_WithMismatchedArrays_IsRefusedAsync() {
    await Assert.That(async () => await _coordinator().AdoptRedeliveredOriginAsync(TrackedGuid.New().Value, null, [TrackedGuid.New().Value], []))
      .Throws<ArgumentException>().Because("the sequences pair with the ids by position");
  }

  [Test]
  public async Task Adopt_ThroughAThroughputGatedCoordinator_HoldsAGateSlotAndAdoptsAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1);
    await using var ctx = CreateDbContext();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorGate>.Instance);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), gate: gate);

    var buckets = await coordinator.AdoptRedeliveredOriginAsync(origin, null, [received], [7]);

    await Assert.That(buckets!.Single().Adopted).IsEqualTo(1);
  }
}

