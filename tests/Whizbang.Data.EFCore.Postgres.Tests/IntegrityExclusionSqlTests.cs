// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2588: the SQL of integrity exclusions on a real store (migration 663). Marking refuses what may not be excluded (this
/// service's own events, decision D1; events written here; events not stored, #2616; superseding events) and takes the
/// rest out of every fold it was in, recording where its origin holds it; unmarking is the exact inverse; selections walk
/// in bounded batches; pending marks are asked once and settled by the answer; the adjustment the comparison subtracts
/// is the fold of the held marks in the window; requests apply in batches from a cursor; and every mark and unmark is
/// logged.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/663_AnOperatorCanExcludeReceivedEventsFromIntegrity.sql</code-under-test>
/// <docs>resilience/integrity-exclusions</docs>
[Category("Integration")]
[NotInParallel("IntegrityCheckpoint")]
[Category("Shard2")]
public class IntegrityExclusionSqlTests : EFCoreTestBase {
  private const string TYPE = "Contracts.ExcludedProbe, Contracts";
  private const string OTHER_TYPE = "Contracts.OtherProbe, Contracts";
  private const string TENANT = "tenant-e";
  private const string REASON = "a defect made these unresolvable";
  private const string OPERATOR = "operator-a";
  private const int EXCLUDED = (int)EventFlags.IntegrityExcluded;
  private const string THIS_SERVICE = "receiver-svc";

  private static readonly DateTimeOffset _t0 = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);
  private static readonly long[] _sequence42 = [42L];
  private static readonly int[] _lanesRead = [0, 2];

  // ── marking ──

  [Test]
  public async Task Exclude_RefusesOwnEventsEventsWrittenHereMissingAndSupersedingOnes_AndMarksTheRestAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var own = await _storeAsync(conn, origin: null, TrackedGuid.New().Value, seq: null);
    var writtenHere = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 3,
      metadata: $$$"""{"Hops":[{"si":{"sn":"{{{THIS_SERVICE}}}"}}]}""");
    var superseding = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 4, supersedeKey: "k");
    var received = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 5);
    var missing = TrackedGuid.New().Value;

    var first = await _excludeAsync(conn, [own, writtenHere, superseding, received, missing]);
    var again = await _excludeAsync(conn, [received]);

    await Assert.That(first[own]).IsEqualTo("own_event")
      .Because("an origin never excludes its own events: its consumers compare against them (decision D1)");
    await Assert.That(first[writtenHere]).IsEqualTo("written_here")
      .Because("an event whose first hop names this service was written here, and 599 returns it to the local lane");
    await Assert.That(first[superseding]).IsEqualTo("superseding");
    await Assert.That(first[missing]).IsEqualTo("not_stored").Because("excluding a missing event is #2616 (decision D3)");
    await Assert.That(first[received]).IsEqualTo("marked");
    await Assert.That(again[received]).IsEqualTo("already_excluded");
    await Assert.That(await _flagsAsync(conn, own) & EXCLUDED).IsEqualTo(0);
    await Assert.That(await _flagsAsync(conn, writtenHere) & EXCLUDED).IsEqualTo(0);
    await Assert.That(await _flagsAsync(conn, received) & EXCLUDED).IsEqualTo(EXCLUDED);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_integrity_exclusions")).IsEqualTo(1L);
  }

  [Test]
  public async Task Exclude_RequiresAReasonAndWhoAskedAsync() {
    await using var conn = await _openAsync();
    var received = await _storeAsync(conn, TrackedGuid.New().Value, TrackedGuid.New().Value, seq: 1);

    await Assert.That(async () => await _excludeAsync(conn, [received], reason: "  ")).Throws<PostgresException>();
    await Assert.That(async () => await _excludeAsync(conn, [received], requestedBy: "")).Throws<PostgresException>();
    await Assert.That(async () => await _unexcludeAsync(conn, [received], reason: "")).Throws<PostgresException>();
  }

  [Test]
  public async Task Exclude_TakesTheEventOutOfItsDigestEpochAndHeldFold_RecordsWhereTheOriginHoldsIt_AndForgetsItsLedgerRowsAsync() {
    await using var conn = await _openAsync();
    var (origin, stream, kept, positioned, held, disowned) = await _scenarioAsync(conn);

    var outcome = await _excludeAsync(conn, [positioned, held, disowned]);

    await Assert.That(outcome.Values.All(o => o == "marked")).IsTrue();
    await Assert.That(await _bucketAsync(conn, origin, stream)).IsEqualTo((1, _lane(kept, 0)))
      .Because("the positioned event leaves its stream digest; the kept one is all that is left in it");
    await Assert.That(await _epochCountAsync(conn, origin)).IsEqualTo(1L)
      .Because("the sealed epoch that counted it refolds without it");
    await Assert.That(await _heldCountAsync(conn, origin)).IsEqualTo(0L)
      .Because("both held events leave the held fold through its trigger");
    await Assert.That(await _marksAsync(conn)).IsEquivalentTo([
      $"{positioned}|1|2", $"{held}|0|", $"{disowned}|2|"])
      .Because("a positioned row is held at the origin's sequence it carries; an unpositioned one is pending, unless its "
        + "origin already answered unknown");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_origin_position_unknown")).IsEqualTo(0L)
      .Because("the event is resolved by decision; its unknown answer is kept on the mark");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_integrity_ledger WHERE origin_service_id = $1", origin))
      .IsEqualTo(0L).Because("the buckets' ledger rows go, so the repair drain never re-asks for an excluded event");
    await Assert.That(await _logAsync(conn)).IsEquivalentTo([$"exclude|{origin}|3|{REASON}|{OPERATOR}"]);
    await Assert.That(await _originCountsAsync(conn, origin)).IsEqualTo("3|1|1");
  }

  [Test]
  public async Task Unexclude_IsTheExactInverseOfExclude_AndBothAreLoggedAsync() {
    await using var conn = await _openAsync();
    var (origin, stream, kept, positioned, held, disowned) = await _scenarioAsync(conn);
    var bucketBefore = await _bucketAsync(conn, origin, stream);
    var epochBefore = await _epochCountAsync(conn, origin);
    var heldBefore = await _heldCountAsync(conn, origin);
    _ = await _excludeAsync(conn, [positioned, held, disowned]);

    var outcome = await _unexcludeAsync(conn, [positioned, held, disowned, kept]);

    await Assert.That(outcome[positioned]).IsEqualTo("unmarked");
    await Assert.That(outcome[kept]).IsEqualTo("not_excluded");
    await Assert.That(await _bucketAsync(conn, origin, stream)).IsEqualTo(bucketBefore);
    await Assert.That(await _epochCountAsync(conn, origin)).IsEqualTo(epochBefore);
    await Assert.That(await _heldCountAsync(conn, origin)).IsEqualTo(heldBefore);
    await Assert.That(await _flagsAsync(conn, positioned) & EXCLUDED).IsEqualTo(0);
    await Assert.That(await _marksAsync(conn)).IsEmpty();
    await Assert.That(await _originCountsAsync(conn, origin)).IsNull()
      .Because("an origin with no marks has no row, so the comparison's probe finds nothing");
    await Assert.That(await _logAsync(conn)).IsEquivalentTo([
      $"exclude|{origin}|3|{REASON}|{OPERATOR}", $"unexclude|{origin}|3|{REASON}|{OPERATOR}"]);
  }

  // ── selections ──

  [Test]
  public async Task Select_ByOriginTypeAndTime_WalksInBoundedBatches_AndNeverPicksOwnEventsAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var other = TrackedGuid.New().Value;
    List<Guid> picked = [];
    for (var i = 0; i < 5; i++) {
      picked.Add(await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: i + 1, createdAt: _t0.AddMinutes(i)));
    }
    _ = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 10, type: OTHER_TYPE, createdAt: _t0);
    _ = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 11, createdAt: _t0.AddDays(1));
    _ = await _storeAsync(conn, other, TrackedGuid.New().Value, seq: 1, createdAt: _t0);
    _ = await _storeAsync(conn, null, TrackedGuid.New().Value, seq: null, createdAt: _t0);
    var selection = $$"""
      {"originServiceId":"{{origin}}","eventTypes":["{{TYPE}}"],"from":"{{_t0:O}}","until":"{{_t0.AddHours(1):O}}"}
      """;

    var all = new List<Guid>();
    Guid? after = null;
    var calls = 0;
    var done = false;
    while (!done) {
      (var ids, after, done) = await _selectAsync(conn, selection, after, limit: 2, scan: 3);
      all.AddRange(ids);
      calls++;
    }

    await Assert.That(all).IsEquivalentTo(picked);
    await Assert.That(calls).IsGreaterThan(2).Because("each call reads at most the scan window of the store");
  }

  [Test]
  public async Task Select_Unresolved_PicksUnknownAnswersHeldRowsAndUnhealedBuckets_BeforeTheCutoffAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var unknown = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: null, createdAt: _t0);
    await _executeAsync(conn, "INSERT INTO wh_origin_position_unknown (event_id, origin_service_id, recorded_at) VALUES ($1, $2, $3)",
      unknown, origin, _t0);
    var heldOld = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: null, createdAt: _t0);
    _ = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: null, createdAt: _t0.AddDays(2));
    var unhealedStream = TrackedGuid.New().Value;
    var unhealed = await _storeAsync(conn, origin, unhealedStream, seq: 7, createdAt: _t0);
    _ = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 8, createdAt: _t0);
    await _ledgerRowAsync(conn, origin, unhealedStream, firstSeen: _t0, unhealable: true);
    var until = _t0.AddDays(1);

    var unknownOnly = await _selectAllAsync(conn, $$"""{"originServiceId":"{{origin}}","unresolved":1,"until":"{{until:O}}"}""");
    var undecided = await _selectAllAsync(conn, $$"""{"originServiceId":"{{origin}}","unresolved":2,"until":"{{until:O}}"}""");
    var unhealedOnly = await _selectAllAsync(conn, $$"""{"originServiceId":"{{origin}}","unresolved":4,"until":"{{until:O}}"}""");
    var everything = await _selectAllAsync(conn, $$"""{"unresolved":7,"until":"{{until:O}}"}""");

    await Assert.That(unknownOnly).IsEquivalentTo([unknown]);
    await Assert.That(undecided).IsEquivalentTo(_list(unknown, heldOld))
      .Because("an unknown answer is a held row too; the held row stored after the cutoff is left");
    await Assert.That(unhealedOnly).IsEquivalentTo([unhealed]);
    await Assert.That(everything).IsEquivalentTo(_list(unknown, heldOld, unhealed));
  }

  // ── pending marks ──

  [Test]
  public async Task APendingMark_IsAskedOnce_AndTheAnswerSettlesIt_HeldOrUnknownAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var a = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: null);
    var b = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: null);
    var c = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: null);
    _ = await _excludeAsync(conn, [a, b, c]);

    var asked = await _askAsync(conn, _t0);
    var askedAgainAtOnce = await _askAsync(conn, _t0.AddMinutes(1));
    var settled = await _scalarAsync<Guid[]>(conn,
      "SELECT wh_integrity_exclusion_settle($1, $2, $3, $4, NULL, NULL, NULL, $5)", origin, new[] { a }, _sequence42, new[] { b }, _t0);
    var askedNextLap = await _askAsync(conn, _t0.AddHours(1));

    await Assert.That(asked!.Value.Origin).IsEqualTo(origin);
    await Assert.That(asked.Value.Ids).IsEquivalentTo(_list(a, b, c));
    await Assert.That(askedAgainAtOnce).IsNull().Because("a mark asked within the lap pause is not asked again");
    await Assert.That(settled).IsEquivalentTo(_list(a, b));
    await Assert.That(await _marksAsync(conn)).IsEquivalentTo([$"{a}|1|42", $"{b}|2|", $"{c}|0|"]);
    await Assert.That(askedNextLap!.Value.Ids).IsEquivalentTo(_list(c))
      .Because("only the mark still pending is asked again, once the lap pause has passed");
    await Assert.That(await _originCountsAsync(conn, origin)).IsEqualTo("3|1|1");
  }

  // ── the adjustment the comparison subtracts ──

  [Test]
  public async Task Adjustment_IsTheXorAndCountOfTheHeldMarksInTheWindow_AndCountsPendingOnesAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var low = await _storeAsync(conn, origin, stream, seq: 5);
    var high = await _storeAsync(conn, origin, stream, seq: 15);
    var pending = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: null);
    _ = await _excludeAsync(conn, [low, high, pending]);

    var byStream = await _adjustmentAsync(conn, origin, since: null, until: null, byStream: true);
    var typeWindow = await _adjustmentAsync(conn, origin, since: 10, until: 20, byStream: false);
    var noMarks = await _adjustmentAsync(conn, TrackedGuid.New().Value, since: null, until: null, byStream: false);

    await Assert.That(byStream[stream]).IsEqualTo((_lane(low, 0) ^ _lane(high, 0), _lane(low, 2) ^ _lane(high, 2), 2, 0));
    await Assert.That(byStream.Values.Sum(v => v.Pending)).IsEqualTo(1);
    await Assert.That(typeWindow[Guid.Empty]).IsEqualTo((_lane(high, 0), _lane(high, 2), 1, 1))
      .Because("a window counts the marks the origin holds inside it, and a pending mark leaves every window undecided");
    await Assert.That(noMarks).IsEmpty().Because("an origin without marks adjusts nothing");
  }

  // ── dry run, requests, summary, roll-off ──

  [Test]
  public async Task DryRun_CountsWhatWouldBeMarkedAndRefused_AndMarksNothingAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var a = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 1);
    var b = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 2);
    var own = await _storeAsync(conn, null, TrackedGuid.New().Value, seq: null);

    var byIds = await _dryRunAsync(conn, [a, b, own], selection: null, cap: 100);
    var bySelection = await _dryRunAsync(conn, null, $$"""{"originServiceId":"{{origin}}"}""", cap: 1);

    await Assert.That(byIds).IsEquivalentTo([$"{origin}|marked|2|False", "|own_event|1|False"]);
    await Assert.That(bySelection).IsEquivalentTo([$"{origin}|marked|1|True"]).Because("the cap stops the count, and says so");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_integrity_exclusions")).IsEqualTo(0L);
    await Assert.That(await _flagsAsync(conn, a) & EXCLUDED).IsEqualTo(0);
  }

  [Test]
  public async Task ARequest_IsAppliedInBoundedBatchesFromItsCursor_AndCompletesAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    List<Guid> ids = [];
    for (var i = 0; i < 5; i++) {
      ids.Add(await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: i + 1));
    }
    var own = await _storeAsync(conn, null, TrackedGuid.New().Value, seq: null);
    var request = await _requestAsync(conn, "exclude", [.. ids, own], selection: null, ofSelection: null);

    var first = await _applyBatchAsync(conn, batch: 4);
    var second = await _applyBatchAsync(conn, batch: 4);
    var nothingLeft = await _applyBatchAsync(conn, batch: 4);

    // Ids are time-ordered, so the own event, stored last, sorts last and falls in the second batch.
    await Assert.That(first).IsEquivalentTo([$"{request}|exclude|{origin}|4|0|False"]);
    await Assert.That(second).IsEquivalentTo([$"{request}|exclude|{origin}|1|0|True", $"{request}|exclude||0|1|True"]);
    await Assert.That(nothingLeft).IsEmpty().Because("a done request is not applied again");
    await Assert.That(await _requestStateAsync(conn, request)).IsEqualTo("Done|5|1");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_integrity_exclusions WHERE selection_id = $1", request))
      .IsEqualTo(5L).Because("each mark names the request that made it");
  }

  [Test]
  public async Task AnUnexcludeRequest_ForAnEarlierRequest_UnmarksEveryMarkItMadeAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var a = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 1);
    var b = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 2);
    var c = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 3);
    var earlier = await _requestAsync(conn, "exclude", [a, b], selection: null, ofSelection: null);
    _ = await _applyBatchAsync(conn, batch: 10);
    _ = await _excludeAsync(conn, [c]);
    var reversal = await _requestAsync(conn, "unexclude", null, selection: "{}", ofSelection: earlier);

    var applied = await _applyBatchAsync(conn, batch: 10);

    await Assert.That(applied).IsEquivalentTo([$"{reversal}|unexclude|{origin}|2|0|True"]);
    await Assert.That(await _marksAsync(conn)).IsEquivalentTo([$"{c}|1|3"]).Because("only the earlier request's marks go");
  }

  [Test]
  public async Task ARequestWhoseBatchFails_IsMarkedFailed_AndTheNextIsAppliedAsync() {
    await using var conn = await _openAsync();
    var a = await _storeAsync(conn, TrackedGuid.New().Value, TrackedGuid.New().Value, seq: 1);
    var broken = await _requestAsync(conn, "exclude", [a], selection: null, ofSelection: null, reason: " ");
    var good = await _requestAsync(conn, "exclude", [a], selection: null, ofSelection: null);

    var failed = await _applyBatchAsync(conn, batch: 10);
    var next = await _applyBatchAsync(conn, batch: 10);

    await Assert.That(failed.Single()).StartsWith($"{broken}|exclude||0|0|True");
    await Assert.That((await _requestStateAsync(conn, broken))!).StartsWith("Failed|0|0");
    await Assert.That(next.Single()).StartsWith($"{good}|exclude|");
  }

  [Test]
  public async Task Summary_CountsEachOriginsMarksByState_AndAnOriginWithNoneHasNoRowAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var a = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: 1);
    var b = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: null);
    _ = await _excludeAsync(conn, [a, b]);
    _ = await _requestAsync(conn, "exclude", [a], selection: null, ofSelection: null);

    var mine = await _summaryAsync(conn, origin);
    var none = await _summaryAsync(conn, TrackedGuid.New().Value);

    await Assert.That(mine).IsEquivalentTo([$"{origin}|2|1|0|1"]);
    await Assert.That(none).IsEmpty();
  }

  [Test]
  public async Task RollOff_RemovesOnlyLogRowsAndFinishedRequestsOlderThanTheRetentionAsync() {
    await using var conn = await _openAsync();
    var a = await _storeAsync(conn, TrackedGuid.New().Value, TrackedGuid.New().Value, seq: 1);
    _ = await _excludeAsync(conn, [a], now: _t0);
    var done = await _requestAsync(conn, "exclude", [a], selection: null, ofSelection: null);
    _ = await _applyBatchAsync(conn, batch: 10, now: _t0);
    var open = await _requestAsync(conn, "exclude", [a], selection: null, ofSelection: null);
    _ = await _unexcludeAsync(conn, [a], now: _t0.AddDays(30));

    var removed = await _scalarAsync<string>(conn,
      "SELECT log_rows || '|' || requests FROM wh_integrity_exclusion_roll_off($1)", _t0.AddDays(1));

    await Assert.That(removed).IsEqualTo("1|1");
    await Assert.That(await _requestStateAsync(conn, done)).IsNull();
    await Assert.That(await _requestStateAsync(conn, open)).IsNotNull().Because("an open request is work, never rolled off");
    await Assert.That(await _logAsync(conn)).IsEquivalentTo([$"unexclude|{await _originOfAsync(conn, a)}|1|{REASON}|{OPERATOR}"]);
  }

  // ── scenario and helpers ──

  private static List<Guid> _list(params Guid[] ids) => [.. ids];

  /// <summary>An origin's stream with a kept and a positioned event in a sealed epoch, two held events (one its origin
  /// answered unknown), and ledger rows for the stream and its type lane.</summary>
  private static async Task<(Guid Origin, Guid Stream, Guid Kept, Guid Positioned, Guid Held, Guid Disowned)> _scenarioAsync(
      NpgsqlConnection conn) {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var kept = await _storeAsync(conn, origin, stream, seq: 1);
    var positioned = await _storeAsync(conn, origin, stream, seq: 2);
    var held = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: null);
    var disowned = await _storeAsync(conn, origin, TrackedGuid.New().Value, seq: null);
    await _executeAsync(conn, "INSERT INTO wh_origin_position_unknown (event_id, origin_service_id) VALUES ($1, $2)", disowned, origin);
    await _executeAsync(conn, """
      INSERT INTO wh_digest_epoch_frontiers (origin_service_id, closed_through_epoch, epoch_width, updated_at)
      VALUES ($1, 0, 10, NOW())
      """, origin);
    _ = await _scalarAsync<int>(conn, "SELECT refold_digest_epochs($1, 0, 0)", origin);
    await _ledgerRowAsync(conn, origin, stream, _t0, unhealable: true);
    await _ledgerRowAsync(conn, origin, Guid.Empty, _t0, unhealable: false);
    await Assert.That(await _epochCountAsync(conn, origin)).IsEqualTo(2L).Because("precondition: the epoch counts both");
    await Assert.That(await _heldCountAsync(conn, origin)).IsEqualTo(2L).Because("precondition: both are held");
    return (origin, stream, kept, positioned, held, disowned);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>One digest lane of a stored event, as the database computed it (<c>_wh_digest_lane</c>) when it was stored.</summary>
  private static long _lane(Guid eventId, int lane) => _lanes[(eventId, lane)];

  private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, int), long> _lanes = new();

  private static async Task<Guid> _storeAsync(NpgsqlConnection conn, Guid? origin, Guid stream, long? seq,
      string type = TYPE, string? supersedeKey = null, string? metadata = null, DateTimeOffset? createdAt = null) {
    var id = TrackedGuid.New().Value;
    await _executeAsync(conn, """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence, supersede_key)
      VALUES ($1, $2, $2, 'TestAggregate', $3, jsonb_build_object('t', $4::text),
              (SELECT COALESCE(MAX(version), 0) + 1 FROM wh_event_store WHERE stream_id = $2),
              nextval('wh_commit_seq'), 0, $5, $6, $7, $8)
      """, id, stream, type, TENANT, createdAt ?? _t0, (object?)origin ?? DBNull.Value, (object?)seq ?? DBNull.Value,
      (object?)supersedeKey ?? DBNull.Value);
    if (metadata is not null) {
      await _executeAsync(conn, "UPDATE wh_event_body SET metadata = $2::jsonb WHERE event_id = $1", id, metadata);
      if (await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_body WHERE event_id = $1", id) == 0) {
        await _executeAsync(conn, "INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES ($1, '{}'::jsonb, $2::jsonb)",
          id, metadata);
      }
    }
    if (origin is not null && supersedeKey is null) {
      await _executeAsync(conn, """
        SELECT _wh_fold_stream_digests(ARRAY[$1::uuid], ARRAY[$2::text], ARRAY[$3::text], ARRAY[$4::uuid], ARRAY[$5::uuid], 'fold', NOW())
        """, origin, TENANT, type, stream, id);
    }
    foreach (var lane in _lanesRead) {
      _lanes[(id, lane)] = await _scalarAsync<long>(conn, "SELECT _wh_digest_lane($1, $2)", id, lane);
    }
    return id;
  }

  private static async Task _ledgerRowAsync(NpgsqlConnection conn, Guid origin, Guid stream, DateTimeOffset firstSeen, bool unhealable) =>
    await _executeAsync(conn, """
      INSERT INTO wh_integrity_ledger
        (origin_service_id, tenant_scope, event_type, stream_id, origin_lo, origin_hi, local_lo, local_hi, first_seen_at, unhealable_at)
      VALUES ($1, $2, $3, $4, 1, 2, 3, 4, $5, CASE WHEN $6 THEN $5 END)
      """, origin, TENANT, TYPE, stream, firstSeen, unhealable);

  private static async Task<Dictionary<Guid, string>> _excludeAsync(NpgsqlConnection conn, Guid[] ids,
      string reason = REASON, string requestedBy = OPERATOR, DateTimeOffset? now = null) =>
    await _outcomesAsync(conn, "SELECT event_id, outcome FROM wh_integrity_exclude($1, $2, $3, NULL, $4, $5)",
      ids, reason, requestedBy, THIS_SERVICE, now ?? _t0);

  private static async Task<Dictionary<Guid, string>> _unexcludeAsync(NpgsqlConnection conn, Guid[] ids,
      string reason = REASON, DateTimeOffset? now = null) =>
    await _outcomesAsync(conn, "SELECT event_id, outcome FROM wh_integrity_unexclude($1, $2, $3, NULL, $4)",
      ids, reason, OPERATOR, now ?? _t0);

  private static async Task<Dictionary<Guid, string>> _outcomesAsync(NpgsqlConnection conn, string sql, params object[] args) {
    var result = new Dictionary<Guid, string>();
    await using var cmd = _command(conn, sql, args);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      result[reader.GetGuid(0)] = reader.GetString(1);
    }
    return result;
  }

  private static async Task<(Guid[] Ids, Guid After, bool Done)> _selectAsync(
      NpgsqlConnection conn, string selection, Guid? after, int limit, int scan) {
    await using var cmd = _command(conn, "SELECT event_ids, next_after, done FROM wh_integrity_exclusion_select($1::jsonb, $2, $3, $4)",
      selection, (object?)after ?? DBNull.Value, limit, scan);
    cmd.Parameters[1].NpgsqlDbType = NpgsqlDbType.Uuid;
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetFieldValue<Guid[]>(0), reader.GetGuid(1), reader.GetBoolean(2));
  }

  private static async Task<List<Guid>> _selectAllAsync(NpgsqlConnection conn, string selection) {
    var all = new List<Guid>();
    Guid? after = null;
    var done = false;
    while (!done) {
      (var ids, after, done) = await _selectAsync(conn, selection, after, limit: 2, scan: 2);
      all.AddRange(ids);
    }
    return all;
  }

  private static async Task<(Guid Origin, Guid[] Ids)?> _askAsync(NpgsqlConnection conn, DateTimeOffset now) {
    await using var cmd = _command(conn, "SELECT origin_service_id, event_ids FROM wh_integrity_exclusion_ask(10, INTERVAL '30 minutes', $1)", now);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return null;
    }
    return (reader.GetGuid(0), reader.GetFieldValue<Guid[]>(1));
  }

  private static async Task<Dictionary<Guid, (long Lo, long IdLo, int Count, int Pending)>> _adjustmentAsync(
      NpgsqlConnection conn, Guid origin, long? since, long? until, bool byStream) {
    await using var cmd = _command(conn, """
      SELECT stream_id, digest_lo, id_lo, event_count, pending_count
      FROM wh_integrity_exclusion_adjustment($1, NULL, NULL, $2, $3, $4)
      """, origin, (object?)since ?? DBNull.Value, (object?)until ?? DBNull.Value, byStream);
    cmd.Parameters[1].NpgsqlDbType = NpgsqlDbType.Bigint;
    cmd.Parameters[2].NpgsqlDbType = NpgsqlDbType.Bigint;
    var result = new Dictionary<Guid, (long, long, int, int)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      result[reader.GetGuid(0)] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4));
    }
    return result;
  }

  private static async Task<List<string>> _dryRunAsync(NpgsqlConnection conn, Guid[]? ids, string? selection, int cap) {
    await using var cmd = _command(conn, """
      SELECT origin_service_id, outcome, event_count, capped FROM wh_integrity_exclusion_dry_run($1, $2::jsonb, $3, $4, 100)
      """, (object?)ids ?? DBNull.Value, (object?)selection ?? DBNull.Value, THIS_SERVICE, cap);
    cmd.Parameters[0].NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid;
    cmd.Parameters[1].NpgsqlDbType = NpgsqlDbType.Text;
    return await _rowsAsync(cmd);
  }

  private static async Task<Guid> _requestAsync(NpgsqlConnection conn, string action, Guid[]? ids, string? selection,
      Guid? ofSelection, string reason = REASON) {
    var id = TrackedGuid.New().Value;
    await using var cmd = _command(conn, """
      INSERT INTO wh_integrity_exclusion_requests
        (request_id, action, event_ids, selection, of_selection_id, reason, requested_by, requested_at)
      VALUES ($1, $2, $3, $4::jsonb, $5, $6, $7, clock_timestamp())
      """, id, action, (object?)ids ?? DBNull.Value, (object?)selection ?? DBNull.Value, (object?)ofSelection ?? DBNull.Value,
      reason, OPERATOR);
    cmd.Parameters[2].NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid;
    cmd.Parameters[3].NpgsqlDbType = NpgsqlDbType.Text;
    cmd.Parameters[4].NpgsqlDbType = NpgsqlDbType.Uuid;
    _ = await cmd.ExecuteNonQueryAsync();
    return id;
  }

  private static async Task<List<string>> _applyBatchAsync(NpgsqlConnection conn, int batch, DateTimeOffset? now = null) {
    await using var cmd = _command(conn, """
      SELECT request_id, action, origin_service_id, applied, refused, done FROM wh_integrity_exclusion_apply_batch($1, 100, $2, $3)
      """, batch, THIS_SERVICE, now ?? _t0);
    return await _rowsAsync(cmd);
  }

  private static Task<string?> _requestStateAsync(NpgsqlConnection conn, Guid request) =>
    _scalarAsync<string?>(conn,
      "SELECT status || '|' || applied || '|' || refused || COALESCE('|' || error, '') FROM wh_integrity_exclusion_requests WHERE request_id = $1",
      request);

  private static async Task<List<string>> _summaryAsync(NpgsqlConnection conn, Guid origin) {
    await using var cmd = _command(conn, """
      SELECT origin_service_id, excluded_count, pending_count, unknown_count, open_requests FROM wh_integrity_exclusion_summary($1)
      """, origin);
    return await _rowsAsync(cmd);
  }

  private static async Task<int> _flagsAsync(NpgsqlConnection conn, Guid eventId) =>
    await _scalarAsync<int>(conn, "SELECT flags FROM wh_event_store WHERE event_id = $1", eventId);

  private static Task<Guid> _originOfAsync(NpgsqlConnection conn, Guid eventId) =>
    _scalarAsync<Guid>(conn, "SELECT origin_service_id FROM wh_event_store WHERE event_id = $1", eventId);

  private static async Task<(int Count, long Lo)> _bucketAsync(NpgsqlConnection conn, Guid origin, Guid stream) {
    await using var cmd = _command(conn,
      "SELECT event_count, digest_lo FROM wh_stream_digests WHERE origin_service_id = $1 AND stream_id = $2", origin, stream);
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync() ? (reader.GetInt32(0), reader.GetInt64(1)) : (0, 0);
  }

  private static Task<long> _epochCountAsync(NpgsqlConnection conn, Guid origin) =>
    _scalarAsync<long>(conn, "SELECT COALESCE(SUM(event_count), 0)::bigint FROM wh_digest_epochs WHERE origin_service_id = $1", origin);

  private static Task<long> _heldCountAsync(NpgsqlConnection conn, Guid origin) =>
    _scalarAsync<long>(conn,
      "SELECT COALESCE(SUM(event_count), 0)::bigint FROM wh_unsequenced_received_type_digests($1, ARRAY[$2::text], INTERVAL '0')",
      origin, TYPE);

  private static async Task<List<string>> _marksAsync(NpgsqlConnection conn) {
    await using var cmd = _command(conn,
      "SELECT event_id || '|' || origin_state || '|' || COALESCE(origin_commit_sequence::text, '') FROM wh_integrity_exclusions");
    return await _rowsAsync(cmd);
  }

  private static async Task<List<string>> _logAsync(NpgsqlConnection conn) {
    await using var cmd = _command(conn, """
      SELECT action || '|' || origin_service_id || '|' || event_count || '|' || reason || '|' || requested_by
      FROM wh_integrity_exclusion_log ORDER BY log_id
      """);
    return await _rowsAsync(cmd);
  }

  private static Task<string?> _originCountsAsync(NpgsqlConnection conn, Guid origin) =>
    _scalarAsync<string?>(conn,
      "SELECT excluded_count || '|' || pending_count || '|' || unknown_count FROM wh_integrity_exclusion_origins WHERE origin_service_id = $1",
      origin);

  private static async Task<List<string>> _rowsAsync(NpgsqlCommand cmd) {
    var rows = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      var fields = new string[reader.FieldCount];
      for (var i = 0; i < fields.Length; i++) {
        fields[i] = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
      }
      rows.Add(string.Join('|', fields));
    }
    return rows;
  }

  private static NpgsqlCommand _command(NpgsqlConnection conn, string sql, params object[] args) {
    var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return cmd;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = _command(conn, sql, args);
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? default! : (T)value;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = _command(conn, sql, args);
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
