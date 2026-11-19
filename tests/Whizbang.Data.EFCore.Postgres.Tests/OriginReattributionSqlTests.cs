// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Re-attribution of a store's own pre-origin history (#2008), against the real <c>wh_integrity_reattribution_window</c>
/// and <c>wh_integrity_reattribute_origins</c> (migration 325). The walk reads the store in commit-sequence batches; an
/// event without an origin whose type another service owns takes that owner as its origin, its digest leaves the local
/// lane for the owner's, sealed local epochs over it are refolded, and the owner's repair ledger buckets it lands in are
/// re-armed. Ownership is the primary signal: a first hop naming a third service is a disagreement, logged and left.
/// A type with no known owner is counted as unattributed and left. The cursor is a compare-and-swap, and a changed
/// ownership fingerprint rewalks from the start.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/325_LocalLaneFollowsOwnership.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#re-attribution</docs>
[Category("Integration")]
[Category("Shard5")]
public class OriginReattributionSqlTests : EFCoreTestBase {
  private const string BILLING_TYPE = "App.Contracts.Billing.Raised, App.Contracts";
  private const string ORDERS_TYPE = "App.Contracts.Orders.Placed, App.Contracts";
  private const string TENANT = "tenant-a";
  private const string FINGERPRINT = "fingerprint-1";
  private const string BILLING_NAME = "billing-svc";
  private const int FLAG_EPHEMERAL = 8;
  private static readonly Guid _zeroLane = Guid.Empty;
  private static readonly Guid _billing = Guid.Parse("0b000000-0000-0000-0000-0000000000b1");

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private IWorkCoordinator _coordinator(WorkCoordinatorGate? gate = null) {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), gate: gate);
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>Stores one event as the emit chain does, folding its digest into its origin's lane (the local lane
  /// without one), and its body with <paramref name="hops"/> as the metadata's hop list.</summary>
  private static async Task<Guid> _storeAsync(NpgsqlConnection conn, Guid stream, int version, string eventType,
      Guid? origin = null, string? hops = null, int flags = 0) {
    var eventId = TrackedGuid.New().Value;
    await using (var store = conn.CreateCommand()) {
      store.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, flags, created_at, origin_service_id)
        VALUES (@event, @stream, @stream, 'Thing', @type, jsonb_build_object('t', @tenant), @version,
                nextval('wh_commit_seq'), @flags, NOW() - INTERVAL '2 hours', @origin)
        """;
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue(nameof(stream), stream);
      store.Parameters.AddWithValue("type", eventType);
      store.Parameters.AddWithValue("tenant", TENANT);
      store.Parameters.AddWithValue(nameof(version), version);
      store.Parameters.AddWithValue(nameof(flags), flags);
      store.Parameters.AddWithValue(nameof(origin), (object?)origin ?? DBNull.Value);
      await store.ExecuteNonQueryAsync();
    }
    if (hops is not null) {
      await using var body = conn.CreateCommand();
      body.CommandText = """
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@event, '{}'::jsonb, jsonb_build_object('MessageId', @event::text, 'Hops', @hops::jsonb))
        """;
      body.Parameters.AddWithValue("event", eventId);
      body.Parameters.AddWithValue(nameof(hops), hops);
      await body.ExecuteNonQueryAsync();
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
    fold.Parameters.AddWithValue("tenant", TENANT);
    fold.Parameters.AddWithValue("type", eventType);
    fold.Parameters.AddWithValue(nameof(stream), stream);
    fold.Parameters.AddWithValue("event", eventId);
    await fold.ExecuteNonQueryAsync();
    return eventId;
  }

  private static string _hops(params string[] services) =>
    "[" + string.Join(',', services.Select(s =>
      $$"""{"ty":0,"si":{"sn":"{{s}}","ii":"{{TrackedGuid.New().Value}}","hn":"host","pi":1},"ts":"2026-06-01T12:00:00+00:00"}""")) + "]";

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<Guid?> _originOfAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT origin_service_id FROM wh_event_store WHERE event_id = $1";
    cmd.Parameters.Add(new NpgsqlParameter { Value = eventId });
    var value = await cmd.ExecuteScalarAsync();
    return value is DBNull ? null : (Guid)value!;
  }

  /// <summary>How many digest buckets of the lane equal a recompute over the stream's events in that lane.</summary>
  private static Task<long> _bucketsMatchingTheStoreAsync(NpgsqlConnection conn, Guid lane, Guid stream) =>
    _scalarAsync<long>(conn, """
      SELECT count(*) FROM (
        SELECT event_type, bit_xor(hashtextextended(event_id::text, 0)) AS lo, bit_xor(hashtextextended(event_id::text, 1)) AS hi, count(*)::int AS n
        FROM wh_event_store
        WHERE stream_id = $2 AND COALESCE(origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid) = $1
          AND COALESCE(flags, 0) & 8 = 0
        GROUP BY event_type
      ) e
      JOIN wh_stream_digests d ON d.origin_service_id = $1 AND d.stream_id = $2 AND d.event_type = e.event_type
      WHERE d.digest_lo = e.lo AND d.digest_hi = e.hi AND d.event_count = e.n
      """, lane, stream);

  private static OriginReattributionTarget _billingTarget() =>
    new() { EventType = BILLING_TYPE, OwnerServiceId = _billing, OwnerServiceName = BILLING_NAME };

  private static OriginReattributionBatch _batch(OriginReattributionWindow window, params OriginReattributionTarget[] targets) =>
    new() { AfterCommitSequence = window.AfterCommitSequence, ThroughCommitSequence = window.ThroughCommitSequence, Targets = targets };

  // ── the window ────────────────────────────────────────────────────────

  [Test]
  public async Task Window_IsTheNextBatchByCommitSequence_WithTheTypesThatHaveNoOriginAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    await _storeAsync(conn, stream, 1, BILLING_TYPE);
    await _storeAsync(conn, stream, 2, ORDERS_TYPE);
    await _storeAsync(conn, stream, 3, "App.Contracts.Shipping.Shipped, App.Contracts", origin: TrackedGuid.New().Value);
    await _storeAsync(conn, stream, 4, "App.Contracts.Later.Later, App.Contracts");
    var third = await _scalarAsync<long>(conn, "SELECT commit_sequence FROM wh_event_store WHERE version = 3");

    var window = await _coordinator().GetOriginReattributionWindowAsync(FINGERPRINT, maxEvents: 3);

    await Assert.That(window).IsNotNull();
    await Assert.That(window!.AfterCommitSequence).IsEqualTo(0L);
    await Assert.That(window.ThroughCommitSequence).IsEqualTo(third).Because("the batch is bounded by rows scanned, not rows moved");
    await Assert.That(window.EventTypes).IsEquivalentTo([BILLING_TYPE, ORDERS_TYPE])
      .Because("only events without an origin are candidates; a received event that carries one is already attributed");
  }

  [Test]
  public async Task Window_OnACaughtUpWalk_IsEmptyAtTheCursorAsync() {
    var window = await _coordinator().GetOriginReattributionWindowAsync(FINGERPRINT, maxEvents: 100);

    await Assert.That(window!.ThroughCommitSequence).IsEqualTo(window.AfterCommitSequence);
    await Assert.That(window.EventTypes).IsEmpty();
  }

  [Test]
  public async Task Window_AfterABatch_StartsWhereTheBatchEndedAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    await _storeAsync(conn, stream, 1, ORDERS_TYPE);
    var coordinator = _coordinator();
    var first = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;

    var result = await coordinator.ReattributeOriginsAsync(_batch(first));
    var next = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;

    await Assert.That(result!.Advanced).IsTrue();
    await Assert.That(next.AfterCommitSequence).IsEqualTo(first.ThroughCommitSequence)
      .Because("the walk is resumable: the cursor is durable and every batch moves it past what it scanned");
  }

  [Test]
  public async Task Window_WithAChangedFingerprint_RewalksFromTheStartAsync() {
    await using var conn = await _openAsync();
    await _storeAsync(conn, TrackedGuid.New().Value, 1, ORDERS_TYPE);
    var coordinator = _coordinator();
    var first = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;
    await coordinator.ReattributeOriginsAsync(_batch(first));

    var rewalk = (await coordinator.GetOriginReattributionWindowAsync("fingerprint-2", 100))!;

    await Assert.That(rewalk.AfterCommitSequence).IsEqualTo(0L)
      .Because("an owner learned later must reach the history the earlier walk left unattributed");
  }

  // ── re-attribution ───────────────────────────────────────────────────

  [Test]
  public async Task Reattribute_ARowAnotherServiceOwns_TakesTheOwnerAsOrigin_AndItsDigestMovesLanesAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1, BILLING_TYPE, hops: _hops(BILLING_NAME, "orders-svc"));
    var own = await _storeAsync(conn, stream, 2, ORDERS_TYPE, hops: _hops("orders-svc"));
    var coordinator = _coordinator();
    var window = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;

    var result = await coordinator.ReattributeOriginsAsync(_batch(window, _billingTarget()));

    await Assert.That(result).IsEqualTo(new OriginReattributionResult { Reattributed = 1, Advanced = true });
    await Assert.That(await _originOfAsync(conn, received)).IsEqualTo(_billing);
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT origin_commit_sequence IS NULL FROM wh_event_store WHERE event_id = $1", received))
      .IsTrue().Because("the owner's sequence is not known here; adoption handles an origin without one");
    await Assert.That(await _originOfAsync(conn, own)).IsNull().Because("a type this service owns stays in its local lane");
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _billing, stream)).IsEqualTo(1L)
      .Because("the moved event's hash joined the owner's lane");
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _zeroLane, stream)).IsEqualTo(1L);
    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_stream_digests WHERE origin_service_id = $1 AND event_type = $2", _zeroLane, BILLING_TYPE))
      .IsEqualTo(0L).Because("the emptied local bucket is removed, not left at zero");
    // #2106: the owner's new bucket carries its id lanes; the hand-seeded local one keeps NULL.
    _ = await StoredDigestInvariant.AssertHashLanesAgreeAsync(conn, stream);
    await StoredDigestInvariant.AssertIdLanesAgreeAsync(conn, filled: 1, stream);
  }

  [Test]
  public async Task Reattribute_AFirstHopWithoutAServiceIdentity_FollowsOwnershipAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var noHops = await _storeAsync(conn, stream, 1, BILLING_TYPE, hops: "[]");
    var noBody = await _storeAsync(conn, stream, 2, BILLING_TYPE);
    var unknown = await _storeAsync(conn, stream, 3, BILLING_TYPE, hops: _hops("Unknown"));
    var otherCase = await _storeAsync(conn, stream, 4, BILLING_TYPE, hops: _hops("BILLING-SVC"));
    var coordinator = _coordinator();
    var window = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;

    var result = await coordinator.ReattributeOriginsAsync(_batch(window, _billingTarget()));

    await Assert.That(result!.Reattributed).IsEqualTo(4);
    foreach (var id in new[] { noHops, noBody, unknown, otherCase }) {
      await Assert.That(await _originOfAsync(conn, id)).IsEqualTo(_billing);
    }
  }

  [Test]
  public async Task Reattribute_AFirstHopNamingAThirdService_IsLeftAndCountedAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var disagreeing = await _storeAsync(conn, stream, 1, BILLING_TYPE, hops: _hops("shipping-svc"));
    var coordinator = _coordinator();
    var window = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;

    var result = await coordinator.ReattributeOriginsAsync(_batch(window, _billingTarget()));

    await Assert.That(result).IsEqualTo(new OriginReattributionResult { HopDisagreements = 1, Advanced = true });
    await Assert.That(await _originOfAsync(conn, disagreeing)).IsNull()
      .Because("ownership is primary and the first hop a cross-check; on disagreement the row is left as it was");
    await Assert.That(await _bucketsMatchingTheStoreAsync(conn, _zeroLane, stream)).IsEqualTo(1L);
  }

  [Test]
  public async Task Reattribute_ATypeWithNoKnownOwner_IsCountedAsUnattributedAndLeftAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var unattributed = await _storeAsync(conn, stream, 1, BILLING_TYPE, hops: _hops(BILLING_NAME));
    var coordinator = _coordinator();
    var window = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;

    var result = await coordinator.ReattributeOriginsAsync(_batch(window,
      new OriginReattributionTarget { EventType = BILLING_TYPE }));

    await Assert.That(result).IsEqualTo(new OriginReattributionResult { Unattributed = 1, Advanced = true });
    await Assert.That(await _originOfAsync(conn, unattributed)).IsNull();
  }

  [Test]
  public async Task Reattribute_AnEphemeralRow_TakesItsOwnerWithoutTouchingDigestsAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var ephemeral = await _storeAsync(conn, stream, 1, BILLING_TYPE, hops: _hops(BILLING_NAME), flags: FLAG_EPHEMERAL);
    var coordinator = _coordinator();
    var window = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;

    var result = await coordinator.ReattributeOriginsAsync(_batch(window, _billingTarget()));

    await Assert.That(result!.Reattributed).IsEqualTo(1);
    await Assert.That(await _originOfAsync(conn, ephemeral)).IsEqualTo(_billing);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_stream_digests WHERE stream_id = $1", stream))
      .IsEqualTo(0L).Because("an ephemeral event folds into no digest, so none moves");
  }

  [Test]
  public async Task Reattribute_ReArmsTheOwnersLedgerBucketsTheRowsLandIn_AndNoOthersAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    await _storeAsync(conn, stream, 1, BILLING_TYPE, hops: _hops(BILLING_NAME));
    await using (var ledger = conn.CreateCommand()) {
      ledger.CommandText = """
        INSERT INTO wh_integrity_ledger
          (origin_service_id, tenant_scope, event_type, stream_id, origin_lo, origin_hi, local_lo, local_hi,
           repair_attempts, unhealable_at, exhausted_at, answer_pending_until, answered_at)
        VALUES
          (@owner, @tenant, @type, @stream, 1, 1, 0, 0, 5, NOW(), NOW(), NOW() + INTERVAL '1 hour', NOW()),
          (@owner, @tenant, @type, '00000000-0000-0000-0000-000000000000', 1, 1, 0, 0, 5, NOW(), NOW(), NOW() + INTERVAL '1 hour', NOW()),
          (@owner, @tenant, 'App.Contracts.Billing.Other, App.Contracts', @stream, 1, 1, 0, 0, 5, NOW(), NOW(), NOW() + INTERVAL '1 hour', NOW())
        """;
      ledger.Parameters.AddWithValue("owner", _billing);
      ledger.Parameters.AddWithValue("tenant", TENANT);
      ledger.Parameters.AddWithValue("type", BILLING_TYPE);
      ledger.Parameters.AddWithValue(nameof(stream), stream);
      await ledger.ExecuteNonQueryAsync();
    }
    var coordinator = _coordinator();
    var window = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;

    await coordinator.ReattributeOriginsAsync(_batch(window, _billingTarget()));

    await Assert.That(await _scalarAsync<long>(conn, """
      SELECT count(*) FROM wh_integrity_ledger
      WHERE event_type = $1 AND repair_attempts = 0 AND unhealable_at IS NULL AND exhausted_at IS NULL
        AND answer_pending_until IS NULL AND answered_at IS NULL
      """, BILLING_TYPE)).IsEqualTo(2L)
      .Because("the moved stream's bucket and its type lane changed on this side, so their ladder starts afresh");
    await Assert.That(await _scalarAsync<int>(conn,
      "SELECT repair_attempts FROM wh_integrity_ledger WHERE event_type = 'App.Contracts.Billing.Other, App.Contracts'"))
      .IsEqualTo(5).Because("a bucket no moved row lands in keeps its ladder");
  }

  [Test]
  public async Task Reattribute_IntoASealedLocalEpoch_RefoldsIt_AndAdvancesTheOriginGenerationAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    await _storeAsync(conn, stream, 1, BILLING_TYPE, hops: _hops(BILLING_NAME));
    await using (var seal = conn.CreateCommand()) {
      seal.CommandText = """
        INSERT INTO wh_digest_epoch_frontiers (origin_service_id, closed_through_epoch, epoch_width, updated_at)
        VALUES ($1, 0, 1000, NOW())
        """;
      seal.Parameters.Add(new NpgsqlParameter { Value = _zeroLane });
      await seal.ExecuteNonQueryAsync();
    }
    _ = await _scalarAsync<int>(conn, "SELECT refold_digest_epochs($1, 0, 0)", _zeroLane);
    var generation = await _scalarAsync<string>(conn, "SELECT setting_value FROM wh_settings WHERE setting_key = 'integrity_origin_generation'");
    var coordinator = _coordinator();
    var window = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;

    await coordinator.ReattributeOriginsAsync(_batch(window, _billingTarget()));

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_digest_epochs WHERE origin_service_id = $1", _zeroLane))
      .IsEqualTo(0L).Because("the sealed local epoch no longer holds an event this service never wrote");
    await Assert.That(long.Parse(await _scalarAsync<string>(conn,
      "SELECT setting_value FROM wh_settings WHERE setting_key = 'integrity_origin_generation'"), System.Globalization.CultureInfo.InvariantCulture))
      .IsEqualTo(long.Parse(generation, System.Globalization.CultureInfo.InvariantCulture) + 1)
      .Because("this service's sealed history changed, so its consumers re-verify instead of alarming");
  }

  [Test]
  public async Task Reattribute_FromAStaleCursor_MovesNothingAndReportsNotAdvancedAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New().Value;
    var received = await _storeAsync(conn, stream, 1, BILLING_TYPE, hops: _hops(BILLING_NAME));
    var coordinator = _coordinator();
    var window = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;

    var result = await coordinator.ReattributeOriginsAsync(new OriginReattributionBatch {
      AfterCommitSequence = window.AfterCommitSequence + 1,
      ThroughCommitSequence = window.ThroughCommitSequence,
      Targets = [_billingTarget()],
    });

    await Assert.That(result).IsEqualTo(new OriginReattributionResult { Advanced = false });
    await Assert.That(await _originOfAsync(conn, received)).IsNull()
      .Because("the cursor moved under the batch (a rewalk or an overlapping pass), so the batch is discarded");
  }

  [Test]
  public async Task Reattribute_ThroughAThroughputGatedCoordinator_WalksAsync() {
    await using var conn = await _openAsync();
    var received = await _storeAsync(conn, TrackedGuid.New().Value, 1, BILLING_TYPE, hops: _hops(BILLING_NAME));
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1,
      logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorGate>.Instance);
    var coordinator = _coordinator(gate);
    var window = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;

    var result = await coordinator.ReattributeOriginsAsync(_batch(window, _billingTarget()));

    await Assert.That(result!.Reattributed).IsEqualTo(1);
    await Assert.That(await _originOfAsync(conn, received)).IsEqualTo(_billing);
  }
}
