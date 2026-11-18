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
/// #2340: a whole-type answer is judged once every one of its bundles has committed, in whatever order they commit. Its
/// bundles can ride several broker sessions (one per stream for a requester that does not accept bundles spanning
/// streams, or one per scope), so the bundle that says it is the last can commit before the others. Each bundle names
/// its answer and its place in it (<see cref="RedeliveryAnswerPart"/>), the last also the count, and the type lanes the
/// answer carried are marked only when the count is complete and no bundle of the answer brought anything.
/// #2341: only an answer to a whole-type request can mark a type lane; an answer to a request for named streams leaves a
/// waiting type lane exactly as it was.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/530_AWholeTypeAnswerIsJudgedOnceAllOfItIsIn.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#a-whole-type-answer-is-judged-once-all-of-it-is-in</docs>
public partial class IntegrityTypeLaneAnswerSqlTests {
  private const string TENANT_A = "tenant-a";
  private const string TENANT_B = "tenant-b";

  // ===== #2341 =====

  [Test]
  public async Task RecordBundleAnswer_ANamedStreamAnswerThatChangedNothing_LeavesAWaitingWholeTypeLaneUnmarkedAndWaitingAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var held = await _storeEventAsync(conn, origin, 250);

    // A separate request named the stream of `held`; its answer covers the whole window and stores nothing.
    var answer = await _recordAsync(coordinator, origin, [held], [250], TrackedGuid.New().Value, 0, 1, wholeType: false,
      from: null, to: null);

    await Assert.That(answer!.MarkedUnchanged).DoesNotContain(lane)
      .Because("an answer for named streams says nothing about the type's other streams");
    await Assert.That(await _laneMarkedAsync(conn)).IsFalse();
    await Assert.That(await _laneAwaitingAsync(conn)).IsTrue()
      .Because("the whole-type request's own answer has not come yet; the named-stream answer leaves the lane as it was");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_ANamedStreamAnswerThatBroughtSomething_LeavesAWaitingWholeTypeLaneWaitingAsync() {
    var (coordinator, conn, origin, _) = await _arrangeAsync();
    var lane = new IntegrityRepairLedger.DivergenceKey(origin, null, await _typeAsync(conn), Guid.Empty);
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var held = await _storeEventAsync(conn, origin, 250);

    _ = await _recordAsync(coordinator, origin, [held, TrackedGuid.New().Value], [250, 260], TrackedGuid.New().Value, 0, 1,
      wholeType: false, from: null, to: null);

    await Assert.That(await _laneAwaitingAsync(conn)).IsTrue()
      .Because("only the whole-type request's own answer stops the type lane waiting for it");
    await conn.DisposeAsync();
  }

  [Test]
  [Arguments("0,1")]
  [Arguments("1,0")]
  public async Task RecordBundleAnswer_AMultiBundleNamedStreamAnswerThatChangedNothing_NeverMarksTheTypeLaneAsync(string order) {
    var (coordinator, conn, origin, _) = await _arrangeAsync();
    var lane = new IntegrityRepairLedger.DivergenceKey(origin, null, await _typeAsync(conn), Guid.Empty);
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var events = new[] { await _storeEventAsync(conn, origin, 150), await _storeEventAsync(conn, origin, 250) };
    var answerId = TrackedGuid.New().Value;

    foreach (var i in _order(order)) {
      _ = await _recordAsync(coordinator, origin, [events[i]], [150 + (i * 100)], answerId, i, i == 1 ? 2 : null,
        wholeType: false, from: null, to: null);
    }

    await Assert.That(await _laneMarkedAsync(conn)).IsFalse();
    await Assert.That(await _laneAwaitingAsync(conn)).IsTrue();
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AOneBundleWholeTypeAnswerThatChangedNothing_MarksTheTypeLaneAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var held = await _storeEventAsync(conn, origin, 250);

    var answer = await _recordAsync(coordinator, origin, [held], [250], TrackedGuid.New().Value, 0, 1);

    await Assert.That(answer!.MarkedUnchanged).Contains(lane)
      .Because("positive control: the whole answer is in, it covered the request, and it brought nothing");
    await Assert.That(await _laneAwaitingAsync(conn)).IsFalse();
    await Assert.That(await _progressRowsAsync(conn)).IsEqualTo(0L).Because("a complete answer keeps no state");
    await conn.DisposeAsync();
  }

  // ===== #2340: every order =====

  [Test]
  [Arguments("0,1,2")]
  [Arguments("0,2,1")]
  [Arguments("1,0,2")]
  [Arguments("1,2,0")]
  [Arguments("2,0,1")]
  [Arguments("2,1,0")]
  public async Task RecordBundleAnswer_AWholeTypeAnswerThatChangedNothing_MarksTheTypeLaneOnlyOnceAllItsBundlesAreInAsync(string order) {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var events = await _storeThreeAsync(conn, origin, [null, null, null]);
    var answerId = TrackedGuid.New().Value;
    var steps = _order(order);

    for (var step = 0; step < steps.Length; step++) {
      var i = steps[step];
      var result = await _recordAsync(coordinator, origin, [events[i]], [_seq(i)], answerId, i, i == 2 ? 3 : null,
        now: _t0.AddMinutes(5 + step));
      if (step < steps.Length - 1) {
        await Assert.That(result!.MarkedUnchanged).DoesNotContain(lane)
          .Because($"after {step + 1} of 3 bundles the rest of the answer may still store something");
        await Assert.That(await _laneMarkedAsync(conn)).IsFalse();
        await Assert.That(await _laneAwaitingAsync(conn)).IsTrue().Because("the type lane waits for the whole answer");
      } else {
        await Assert.That(result!.MarkedUnchanged).Contains(lane)
          .Because("the last bundle to commit completes the answer, and no bundle of it brought anything");
      }
    }
    await Assert.That(await _laneAwaitingAsync(conn)).IsFalse();
    await Assert.That(await _progressRowsAsync(conn)).IsEqualTo(0L);
    await conn.DisposeAsync();
  }

  [Test]
  [Arguments("0,1,2", 0)]
  [Arguments("0,1,2", 1)]
  [Arguments("0,1,2", 2)]
  [Arguments("0,2,1", 0)]
  [Arguments("0,2,1", 1)]
  [Arguments("0,2,1", 2)]
  [Arguments("1,0,2", 0)]
  [Arguments("1,0,2", 1)]
  [Arguments("1,0,2", 2)]
  [Arguments("1,2,0", 0)]
  [Arguments("1,2,0", 1)]
  [Arguments("1,2,0", 2)]
  [Arguments("2,0,1", 0)]
  [Arguments("2,0,1", 1)]
  [Arguments("2,0,1", 2)]
  [Arguments("2,1,0", 0)]
  [Arguments("2,1,0", 1)]
  [Arguments("2,1,0", 2)]
  public async Task RecordBundleAnswer_AWholeTypeAnswerWithABundleThatBroughtSomething_NeverMarksTheTypeLaneAsync(
      string order, int brought) {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var events = await _storeThreeAsync(conn, origin, [null, null, null]);
    var answerId = TrackedGuid.New().Value;
    var marked = new List<IntegrityRepairLedger.DivergenceKey>();

    var steps = _order(order);
    for (var step = 0; step < steps.Length; step++) {
      var i = steps[step];
      // The bundle that brought something carries one event this store does not hold.
      Guid[] ids = i == brought ? [events[i], TrackedGuid.New().Value] : [events[i]];
      long?[] seqs = i == brought ? [_seq(i), _seq(i) + 10] : [_seq(i)];
      var result = await _recordAsync(coordinator, origin, ids, seqs, answerId, i, i == 2 ? 3 : null,
        now: _t0.AddMinutes(5 + step));
      marked.AddRange(result!.MarkedUnchanged);
    }

    await Assert.That(marked).DoesNotContain(lane)
      .Because($"bundle {brought} of the answer brought something new, so the type is still worth asking for");
    await Assert.That(await _laneMarkedAsync(conn)).IsFalse();
    await Assert.That(await _laneAwaitingAsync(conn)).IsFalse().Because("the answer is in");
    await Assert.That(await _progressRowsAsync(conn)).IsEqualTo(0L);
    await conn.DisposeAsync();
  }

  [Test]
  [Arguments("0,1,2")]
  [Arguments("0,2,1")]
  [Arguments("1,0,2")]
  [Arguments("1,2,0")]
  [Arguments("2,0,1")]
  [Arguments("2,1,0")]
  public async Task RecordBundleAnswer_AnAnswerWhoseBundlesCarryDifferentScopes_MarksEveryTenantsLaneOnlyOnceAllOfItIsInAsync(string order) {
    var (coordinator, conn, origin, laneA, laneB) = await _arrangeTwoTenantsAsync();
    var events = await _storeThreeAsync(conn, origin, [TENANT_A, TENANT_B, TENANT_A]);
    var answerId = TrackedGuid.New().Value;
    var marked = new List<IntegrityRepairLedger.DivergenceKey>();

    var steps = _order(order);
    for (var step = 0; step < steps.Length; step++) {
      var i = steps[step];
      if (step == steps.Length - 1) {
        await Assert.That((await _laneMarkedAsync(conn, TENANT_A), await _laneMarkedAsync(conn, TENANT_B))).IsEqualTo((false, false))
          .Because("no tenant's lane is judged before the whole answer is in");
      }
      var result = await _recordAsync(coordinator, origin, [events[i]], [_seq(i)], answerId, i, i == 2 ? 3 : null,
        now: _t0.AddMinutes(5 + step));
      marked.AddRange(result!.MarkedUnchanged);
    }

    await Assert.That(marked).Contains(laneA);
    await Assert.That(marked).Contains(laneB)
      .Because("the answer covered both tenants' whole type and brought nothing for either, whichever bundle came last");
    await Assert.That((await _laneAwaitingAsync(conn, TENANT_A), await _laneAwaitingAsync(conn, TENANT_B))).IsEqualTo((false, false));
    await conn.DisposeAsync();
  }

  [Test]
  [Arguments("0,1,2")]
  [Arguments("0,2,1")]
  [Arguments("1,0,2")]
  [Arguments("1,2,0")]
  [Arguments("2,0,1")]
  [Arguments("2,1,0")]
  public async Task RecordBundleAnswer_AnAnswerThatBroughtSomethingForOneTenant_MarksOnlyTheOtherTenantsLaneAsync(string order) {
    var (coordinator, conn, origin, laneA, laneB) = await _arrangeTwoTenantsAsync();
    var events = await _storeThreeAsync(conn, origin, [TENANT_A, TENANT_B, TENANT_A]);
    var answerId = TrackedGuid.New().Value;
    var marked = new List<IntegrityRepairLedger.DivergenceKey>();

    var steps = _order(order);
    for (var step = 0; step < steps.Length; step++) {
      var i = steps[step];
      // Bundle 1 (tenant b) also carries an event this store does not hold.
      Guid[] ids = i == 1 ? [events[i], TrackedGuid.New().Value] : [events[i]];
      long?[] seqs = i == 1 ? [_seq(i), _seq(i) + 10] : [_seq(i)];
      var result = await _recordAsync(coordinator, origin, ids, seqs, answerId, i, i == 2 ? 3 : null,
        now: _t0.AddMinutes(5 + step));
      marked.AddRange(result!.MarkedUnchanged);
    }

    await Assert.That(marked).Contains(laneA);
    await Assert.That(marked).DoesNotContain(laneB);
    await Assert.That(await _laneMarkedAsync(conn, TENANT_B)).IsFalse()
      .Because("the answer brought tenant b something new");
    await conn.DisposeAsync();
  }

  // ===== #2340: the count, the window and the state =====

  [Test]
  public async Task RecordBundleAnswer_ABundleCommittedTwice_DoesNotStandInForAMissingOneAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var events = await _storeThreeAsync(conn, origin, [null, null, null]);
    var answerId = TrackedGuid.New().Value;

    _ = await _recordAsync(coordinator, origin, [events[2]], [_seq(2)], answerId, 2, 3);
    _ = await _recordAsync(coordinator, origin, [events[0]], [_seq(0)], answerId, 0, null);
    var again = await _recordAsync(coordinator, origin, [events[0]], [_seq(0)], answerId, 0, null);
    var markedBeforeTheMissing = await _laneMarkedAsync(conn);
    var last = await _recordAsync(coordinator, origin, [events[1]], [_seq(1)], answerId, 1, null);

    await Assert.That(again!.MarkedUnchanged).DoesNotContain(lane);
    await Assert.That(markedBeforeTheMissing).IsFalse().Because("bundle 1 of 3 had not come; a repeat of bundle 0 is not it");
    await Assert.That(last!.MarkedUnchanged).Contains(lane);
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnAnswerWithABundleNeverCommitted_LeavesTheTypeLaneWaitingAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var events = await _storeThreeAsync(conn, origin, [null, null, null]);
    var answerId = TrackedGuid.New().Value;

    _ = await _recordAsync(coordinator, origin, [events[2]], [_seq(2)], answerId, 2, 3);
    _ = await _recordAsync(coordinator, origin, [events[0]], [_seq(0)], answerId, 0, null);

    await Assert.That(await _laneMarkedAsync(conn)).IsFalse()
      .Because("bundle 1 may have been lost with something the type is missing; the lane waits out its answer timeout");
    await Assert.That(await _laneAwaitingAsync(conn)).IsTrue();
    await conn.DisposeAsync();
  }

  [Test]
  [Arguments("0,1")]
  [Arguments("1,0")]
  public async Task RecordBundleAnswer_PagesThatEachAnswerPartOfTheWindow_AreJudgedByTheWindowTheyAnsweredTogetherAsync(string order) {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var events = new[] { await _storeEventAsync(conn, origin, 150), await _storeEventAsync(conn, origin, 300) };
    var answerId = TrackedGuid.New().Value;
    // A backfill window's pages: the first covers (100, 250], the second (250, 400].
    var windows = new (long From, long To)[] { (100, 250), (250, 400) };
    var marked = new List<IntegrityRepairLedger.DivergenceKey>();

    foreach (var i in _order(order)) {
      var result = await _recordAsync(coordinator, origin, [events[i]], [i == 0 ? 150 : 300], answerId, i, i == 1 ? 2 : null,
        from: windows[i].From, to: windows[i].To);
      marked.AddRange(result!.MarkedUnchanged);
    }

    await Assert.That(marked).Contains(lane)
      .Because("together the pages covered (100, 400], the whole window the request asked for");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnAnswerThatCoveredLessThanTheRequest_AnswersTheTypeLaneWithoutMarkingItAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var events = new[] { await _storeEventAsync(conn, origin, 250), await _storeEventAsync(conn, origin, 300) };
    var answerId = TrackedGuid.New().Value;

    _ = await _recordAsync(coordinator, origin, [events[1]], [300], answerId, 1, 2, from: 200, to: 400);
    _ = await _recordAsync(coordinator, origin, [events[0]], [250], answerId, 0, null, from: 200, to: 400);

    await Assert.That(await _laneMarkedAsync(conn)).IsFalse()
      .Because("the answer did not look at sequences 101 to 200, where the type's deficit may lie");
    await Assert.That(await _laneAwaitingAsync(conn)).IsFalse().Because("the answer is in, so the lane stops waiting for it");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_ALastBundleThatCarriedAStreamInPart_LeavesTheTypeLaneUnmarkedAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var (first, cut) = (await _storeEventAsync(conn, origin, 150), await _storeEventOnAsync(conn, origin, 250, null));
    var answerId = TrackedGuid.New().Value;

    // The last bundle stopped at the origin's cap in the middle of a stream.
    _ = await _recordAsync(coordinator, origin, [cut.EventId], [250], answerId, 1, 2, partial: [cut.StreamId]);
    _ = await _recordAsync(coordinator, origin, [first], [150], answerId, 0, null);

    await Assert.That(await _laneMarkedAsync(conn)).IsFalse()
      .Because("the rest of that stream lies beyond the answer, so it has not shown the type is missing nothing");
    await Assert.That(await _laneAwaitingAsync(conn)).IsFalse();
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnEarlierBundleThatCarriedAStreamInPart_StillLetsTheWholeAnswerMarkAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var split = await _storeEventOnAsync(conn, origin, 150, null);
    var rest = await _storeEventOnAsync(conn, origin, 250, null, split.StreamId);
    var answerId = TrackedGuid.New().Value;

    // An earlier bundle of the answer names a stream partial; as before (431), only the last bundle's partial streams
    // stop the mark, since only there can the answer have stopped short of a stream (at the origin's cap).
    _ = await _recordAsync(coordinator, origin, [rest.EventId], [250], answerId, 1, 2);
    var last = await _recordAsync(coordinator, origin, [split.EventId], [150], answerId, 0, null, partial: [split.StreamId]);

    await Assert.That(last!.MarkedUnchanged).Contains(lane)
      .Because("the stream continues in a later bundle of the same answer, which carried the rest of it");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnAnswerLeftIncompleteForADay_IsForgottenWhenAnotherAnswerStartsAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var events = await _storeThreeAsync(conn, origin, [null, null, null]);

    _ = await _recordAsync(coordinator, origin, [events[0]], [_seq(0)], TrackedGuid.New().Value, 0, null, now: _t0);
    var keptWithinTheDay = await _progressRowsAsync(conn);
    _ = await _recordAsync(coordinator, origin, [events[1]], [_seq(1)], TrackedGuid.New().Value, 0, null, now: _t0.AddHours(23));
    var bothKept = await _progressRowsAsync(conn);
    _ = await _recordAsync(coordinator, origin, [events[2]], [_seq(2)], TrackedGuid.New().Value, 0, null, now: _t0.AddHours(25));

    await Assert.That((keptWithinTheDay, bothKept)).IsEqualTo((1L, 2L));
    await Assert.That(await _progressRowsAsync(conn)).IsEqualTo(2L)
      .Because("the first answer's bundles stopped coming a day ago: its state is dropped, the other two are kept");
    await conn.DisposeAsync();
  }

  // ===== #2340: two bundles committing at once =====

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task RecordBundleAnswer_ConcurrentBundlesOfOneAnswer_JudgeTheAnswerOnceBothHaveCommittedAsync(bool firstBroughtSomething) {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var (e0, e1) = (await _storeEventAsync(conn, origin, 150), await _storeEventAsync(conn, origin, 250));
    var answerId = TrackedGuid.New().Value;
    await using var last = new NpgsqlConnection(ConnectionString);
    await using var first = new NpgsqlConnection(ConnectionString);
    await last.OpenAsync();
    await first.OpenAsync();

    // The answer's last bundle commits on one session, and is still in its transaction when bundle 0 arrives on another.
    await using var tx = await last.BeginTransactionAsync();
    var lastMarked = await _recordRawAsync(last, origin, [e1], [250], answerId, 1, 2);
    Guid[] firstIds = firstBroughtSomething ? [e0, TrackedGuid.New().Value] : [e0];
    long[] firstSeqs = firstBroughtSomething ? [150, 160] : [150];
    var firstCall = _recordRawAsync(first, origin, firstIds, firstSeqs, answerId, 0, null);
    await _untilBlockedByAsync(conn, first.ProcessID, last.ProcessID);
    await tx.CommitAsync();
    var firstMarked = await firstCall;

    await Assert.That(lastMarked).IsFalse().Because("bundle 0 had not committed when the last bundle did");
    await Assert.That(firstMarked).IsEqualTo(!firstBroughtSomething)
      .Because("bundle 0 waited for the last bundle's commit, then saw the answer complete and judged it whole");
    await Assert.That(await _laneMarkedAsync(conn)).IsEqualTo(!firstBroughtSomething);
    await Assert.That(await _laneAwaitingAsync(conn)).IsFalse();
    await Assert.That(await _progressRowsAsync(conn)).IsEqualTo(0L);
    await conn.DisposeAsync();
  }

  // ===== helpers =====

  // Calls the function on the given session; true when it marked the type lane.
  private static async Task<bool> _recordRawAsync(
      NpgsqlConnection conn, Guid origin, Guid[] eventIds, long[] sequences, Guid answerId, int index, int? count) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT COUNT(*) FILTER (WHERE stream_id = $1 AND marked IS NOT NULL) > 0
      FROM wh_integrity_record_bundle_answer($2, $3, $4, $5, true, 100, 400, NULL, $6, $7, $8, $9, true)
      """;
    cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.Empty });
    cmd.Parameters.Add(new NpgsqlParameter { Value = origin });
    cmd.Parameters.Add(new NpgsqlParameter { Value = eventIds });
    cmd.Parameters.Add(new NpgsqlParameter { Value = sequences });
    cmd.Parameters.Add(new NpgsqlParameter { Value = _t0.AddMinutes(5) });
    cmd.Parameters.Add(new NpgsqlParameter { Value = count is null });
    cmd.Parameters.Add(new NpgsqlParameter { Value = answerId });
    cmd.Parameters.Add(new NpgsqlParameter { Value = index });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)count ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  // Waits for the exact transition: the waiting session is blocked by the holding one's transaction.
  private static async Task _untilBlockedByAsync(NpgsqlConnection observer, int waiting, int holding) {
    using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    while (!await _scalarAsync<bool>(observer, "SELECT $1 = ANY(pg_blocking_pids($2))", holding, waiting)) {
      guard.Token.ThrowIfCancellationRequested();
    }
  }

  private static int[] _order(string order) => [.. order.Split(',').Select(int.Parse)];

  private static long _seq(int bundle) => 150 + (bundle * 50);

  private static Task<IntegrityBundleAnswer?> _recordAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, Guid origin, Guid[] eventIds, long?[] sequences,
      Guid answerId, int index, int? count, bool wholeType = true, long? from = 100, long? to = 400,
      List<Guid>? partial = null, DateTimeOffset? now = null) =>
    coordinator.IntegrityRecordBundleAnswerAsync(origin, eventIds, sequences,
      new RedeliveryAnswerWindow(from, to) {
        Continues = count is null,
        PartialStreamIds = partial,
        Part = new RedeliveryAnswerPart(answerId, index, count, wholeType),
      }, now ?? _t0.AddMinutes(5));

  private static Task<string> _typeAsync(NpgsqlConnection conn) =>
    _scalarAsync<string>(conn, "SELECT normalize_event_type($1)", MESSAGE_TYPE);

  private async Task<(EFCoreWorkCoordinator<WorkCoordinationDbContext> Coordinator, NpgsqlConnection Conn, Guid Origin,
      IntegrityRepairLedger.DivergenceKey LaneA, IntegrityRepairLedger.DivergenceKey LaneB)> _arrangeTwoTenantsAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var type = await _typeAsync(conn);
    var laneA = new IntegrityRepairLedger.DivergenceKey(origin, TENANT_A, type, Guid.Empty);
    var laneB = new IntegrityRepairLedger.DivergenceKey(origin, TENANT_B, type, Guid.Empty);
    await _reportAsync(coordinator, laneA);
    await _reportAsync(coordinator, laneB);
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [laneA, laneB], 101, 401, _t0, _timeout);
    return (coordinator, conn, origin, laneA, laneB);
  }

  private static async Task<Guid[]> _storeThreeAsync(NpgsqlConnection conn, Guid origin, string?[] tenants) {
    var ids = new Guid[3];
    for (var i = 0; i < 3; i++) {
      ids[i] = (await _storeEventOnAsync(conn, origin, _seq(i), tenants[i])).EventId;
    }
    return ids;
  }

  // One collective event of the lane's type, on its own stream (or the given one), under the tenant's scope when given.
  private static async Task<(Guid EventId, Guid StreamId)> _storeEventOnAsync(
      NpgsqlConnection conn, Guid origin, long originSequence, string? tenant, Guid? stream = null) {
    var eventId = TrackedGuid.New().Value;
    var streamId = stream ?? TrackedGuid.New().Value;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, flags, created_at,
         origin_service_id, origin_commit_sequence, scope)
      VALUES ($1, $2, $2, 'Upstream', normalize_event_type($3),
              (SELECT COUNT(*) + 1 FROM wh_event_store WHERE stream_id = $2), nextval('wh_commit_seq'), 0, NOW(), $4, $5,
              CASE WHEN $6::text IS NULL THEN NULL ELSE jsonb_build_object('t', $6::text) END)
      """;
    foreach (var arg in new object[] { eventId, streamId, MESSAGE_TYPE, origin, originSequence }) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)tenant ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
    await cmd.ExecuteNonQueryAsync();
    return (eventId, streamId);
  }

  private static Task<bool> _laneMarkedAsync(NpgsqlConnection conn, string tenant) =>
    _scalarAsync<bool>(conn,
      "SELECT unhealable_at IS NOT NULL FROM wh_integrity_ledger WHERE stream_id = $1 AND tenant_scope = $2", Guid.Empty, tenant);

  private static Task<bool> _laneAwaitingAsync(NpgsqlConnection conn, string tenant) =>
    _scalarAsync<bool>(conn,
      "SELECT answer_pending_until IS NOT NULL FROM wh_integrity_ledger WHERE stream_id = $1 AND tenant_scope = $2", Guid.Empty, tenant);

  private static Task<long> _progressRowsAsync(NpgsqlConnection conn) =>
    _scalarAsync<long>(conn, "SELECT COUNT(*) FROM wh_integrity_answer_progress");
}
