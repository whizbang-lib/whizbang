// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Repair waits for its answer (#2029, #2030, #2033, #2034), against the real ledger functions. A bucket the drain
/// requested is not claimed again while its answer is on the way; a bundle that lands for it stops the asking until
/// the next comparison decides; the attempt budget ends the asking instead of settling into a terminal cadence; only
/// a change repair can act on (the local digest, or a deficit that grew) starts the ladder over; a bundle that changed
/// nothing marks its buckets when it commits; and the type lane (bulk and checkpoint asks) covers the per-stream rows
/// it answers for.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/320_RepairWaitsForItsAnswer.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityRepairAnswerSqlTests : EFCoreTestBase {
  private const string MESSAGE_TYPE = "Upstream.Contracts.OrderPlaced, Upstream.Contracts";
  private const int MAX_ATTEMPTS = 8;

  // Fixed instants, not the host clock: every function under test takes the caller's clock.
  private static readonly DateTimeOffset _t0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _backoff = TimeSpan.FromSeconds(300);
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);
  private static readonly TimeSpan _answerTimeout = TimeSpan.FromMinutes(180);

  // ===== #2029: answer pending =====

  [Test]
  public async Task Claim_ABucketWithItsAnswerPending_IsNotClaimed_UntilTheAnswerTimeoutPassesAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var key = _key(origin, TrackedGuid.New().Value);
    await _reportAsync(coordinator, key, deficit: 3, at: _t0);
    var first = await _claimAsync(coordinator, origin, _t0.AddSeconds(1));
    await Assert.That(first.Count).IsEqualTo(1).Because("precondition: the first attempt is claimed");

    var marked = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [key], null, null, _t0.AddSeconds(1), _answerTimeout);

    await Assert.That(marked).IsEqualTo(1);
    await Assert.That((await _claimAsync(coordinator, origin, _t0.AddHours(1))).Count).IsEqualTo(0)
      .Because("the backoff has passed, but the answer to the last request is still on its way");
    await Assert.That((await _claimAsync(coordinator, origin, _t0.AddSeconds(1) + _answerTimeout)).Count).IsEqualTo(1)
      .Because("an answer that never came is asked for again once the answer timeout passes");
  }

  [Test]
  public async Task RecordBundleAnswer_ClearsThePending_AndTheBucketWaitsForTheNextComparisonAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var key = _key(origin, stream, await _normalizedTypeAsync(conn));
    await _reportAsync(coordinator, key, deficit: 1, at: _t0);
    _ = await _claimAsync(coordinator, origin, _t0.AddSeconds(1));
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [key], null, null, _t0.AddSeconds(1), _answerTimeout);
    var missing = TrackedGuid.New().Value;
    await _storeAsync(_message(missing, stream, origin, 7));

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(origin, [missing], [7], _t0.AddMinutes(5));

    await Assert.That(answer).IsNotNull();
    await Assert.That(answer!.Absent).IsEqualTo(1).Because("the bundle carried an event this store did not hold");
    await Assert.That(answer.AllPresent).IsFalse();
    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT answer_pending_until IS NULL AND answered_at IS NOT NULL AND unhealable_at IS NULL FROM wh_integrity_ledger WHERE stream_id = $1", stream))
      .IsTrue().Because("the answer landed: pending clears, the bucket is answered, and nothing says repair cannot change it");
    await Assert.That((await _claimAsync(coordinator, origin, _t0.AddDays(10))).Count).IsEqualTo(0)
      .Because("an answered bucket is not asked for again until a comparison says it is still divergent");

    await _reportAsync(coordinator, key, deficit: 1, at: _t0.AddDays(10));

    await Assert.That((await _claimAsync(coordinator, origin, _t0.AddDays(10).AddSeconds(1))).Count).IsEqualTo(1)
      .Because("the next comparison still found it divergent, which re-arms it");
  }

  // ===== #2033: the unhealable mark at commit =====

  [Test]
  public async Task RecordBundleAnswer_ABundleWhoseEventsWereAllStoredAlready_MarksTheBucketAtCommitAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var key = _key(origin, stream, await _normalizedTypeAsync(conn));
    var stored = await _storeEventAsync(conn, stream, version: 1, origin, originSequence: 4);
    await _reportAsync(coordinator, key, deficit: 1, at: _t0);

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(origin, [stored], [4], _t0.AddMinutes(5));
    var again = await coordinator.IntegrityRecordBundleAnswerAsync(origin, [stored], [4], _t0.AddMinutes(6));

    await Assert.That(answer!.AllPresent).IsTrue();
    await Assert.That(answer.MarkedUnchanged).IsEquivalentTo([key])
      .Because("every event was already stored under its origin and none needs adopting: asking again re-ships the same events");
    await Assert.That(again!.MarkedUnchanged).IsEmpty().Because("a bucket is marked, and reported, once");
    await Assert.That((await _claimAsync(coordinator, origin, _t0.AddDays(1))).Count).IsEqualTo(0);
  }

  [Test]
  public async Task RecordBundleAnswer_ChildrenTheBundleJustBrought_AreAbsentAtCommit_ThoughAReconcileAfterTheyAreStoredFindsThemPresentAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerInstanceAsync(conn, instance);
    var key = _key(origin, stream, await _normalizedTypeAsync(conn));
    var kept = await _storeEventAsync(conn, stream, version: 1, origin, originSequence: 1);
    await _reportAsync(coordinator, key, deficit: 1, at: _t0);
    var missing = TrackedGuid.New().Value;
    await _storeAsync(_message(missing, stream, origin, 2));

    var atCommit = await coordinator.IntegrityRecordBundleAnswerAsync(origin, [kept, missing], [1, 2], _t0.AddMinutes(1));
    await _leaseAsync(conn, instance, missing);
    await _emitInboxChainAsync(conn, instance);
    var later = await coordinator.AdoptRedeliveredOriginAsync(origin, null, [kept, missing], [1, 2]);

    await Assert.That(atCommit!.Present).IsEqualTo(1);
    await Assert.That(atCommit.Absent).IsEqualTo(1).Because("at commit the new child is in the inbox, not the event store");
    await Assert.That(atCommit.MarkedUnchanged).IsEmpty().Because("the bundle stores an event: repair is working");
    await Assert.That(later!.Sum(b => b.Present)).IsEqualTo(2)
      .Because("once the child is stored a later reconcile counts it present, so 'changed nothing' must be judged at commit");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT unhealable_at IS NULL FROM wh_integrity_ledger WHERE stream_id = $1", stream))
      .IsTrue();
  }

  [Test]
  public async Task RecordBundleAnswer_AStoredEventWithoutItsOrigin_IsLeftForTheReconcile_NotMarkedAtCommitAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var key = _key(origin, stream, await _normalizedTypeAsync(conn));
    var preOrigin = await _storeEventAsync(conn, stream, version: 1, origin: null, originSequence: null);
    await _reportAsync(coordinator, key, deficit: 1, at: _t0);

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(origin, [preOrigin], [9], _t0.AddMinutes(1));

    await Assert.That(answer!.AllPresent).IsTrue();
    await Assert.That(answer.Adoptable).IsEqualTo(1).Because("the bundle can give the stored event its origin");
    await Assert.That(answer.MarkedUnchanged).IsEmpty().Because("adopting the origin changes the bucket; the reconcile decides");
  }

  [Test]
  public async Task RecordBundleAnswer_AnEventTheStoreRefusedToRestore_MarksItsBucketAtCommit_WithTheReasonAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var key = _key(origin, stream, await _normalizedTypeAsync(conn));
    // Versions 1 and 2 were truncated here; the survivor is all the stream holds.
    _ = await _storeEventAsync(conn, stream, version: 3, origin, originSequence: 3);
    await _reportAsync(coordinator, key, deficit: 1, at: _t0);
    var truncated = TrackedGuid.New().Value;
    await _rememberAsync(conn, truncated);
    await _storeAsync(_message(truncated, stream, origin, 1));

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(origin, [truncated], [1], _t0.AddMinutes(1));

    await Assert.That(answer!.RefusedTruncated).IsEqualTo(1);
    await Assert.That(answer.MarkedRefusedTruncated).IsEquivalentTo([key])
      .Because("restoring the event could bring back what the truncation removed on purpose; asking again cannot help");
    await Assert.That(answer.MarkedUnchanged).IsEmpty();
  }

  [Test]
  public async Task RecordBundleAnswer_ABucketWithATenant_IsKeyedWithItAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var key = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", await _normalizedTypeAsync(conn), stream);
    var stored = await _storeEventAsync(conn, stream, version: 1, origin, originSequence: 4, tenant: "tenant-a");
    await _reportAsync(coordinator, key, deficit: 1, at: _t0);

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(origin, [stored], [4], _t0.AddMinutes(1));

    await Assert.That(answer!.MarkedUnchanged).IsEquivalentTo([key]);
  }

  [Test]
  public async Task RecordBundleAnswer_AnEventNeitherStoreNorInboxHolds_CountsAsAbsentAsync() {
    var answer = await _coordinator().IntegrityRecordBundleAnswerAsync(
      TrackedGuid.New().Value, [TrackedGuid.New().Value], [1], _t0);

    await Assert.That(answer!.Children).IsEqualTo(1);
    await Assert.That(answer.Absent).IsEqualTo(1);
    await Assert.That(answer.AllPresent).IsFalse();
    await Assert.That(answer.MarkedUnchanged).IsEmpty();
  }

  // ===== #2029: a real cap =====

  [Test]
  public async Task Claim_PastTheAttemptBudget_StopsForGood_AndCountsAsExhaustedAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    await _reportAsync(coordinator, _key(origin, TrackedGuid.New().Value), deficit: 2, at: _t0);

    var first = await coordinator.IntegrityClaimRepairDrainAsync([origin], _t0.AddSeconds(1), _backoff, maxAttempts: 1, limit: 10);
    var muchLater = await coordinator.IntegrityClaimRepairDrainAsync([origin], _t0.AddDays(30), _backoff, maxAttempts: 1, limit: 10);
    var summary = await coordinator.GetIntegrityLedgerSummaryAsync(maxRepairAttempts: 1);

    await Assert.That(first.Count).IsEqualTo(1);
    await Assert.That(muchLater.Count).IsEqualTo(0)
      .Because("a bucket that spent its budget stops being requested; there is no terminal cadence");
    await Assert.That(summary.RepairExhausted).IsEqualTo(1L);
  }

  [Test]
  public async Task TryBeginRepair_PastTheAttemptBudget_IsRefusedForGoodAsync() {
    var coordinator = _coordinator();
    var key = _key(TrackedGuid.New().Value, Guid.Empty);

    var first = await coordinator.IntegrityTryBeginRepairAsync(key, _t0, _backoff, maxAttempts: 1);
    var muchLater = await coordinator.IntegrityTryBeginRepairAsync(key, _t0.AddDays(30), _backoff, maxAttempts: 1);

    await Assert.That(first).IsTrue();
    await Assert.That(muchLater).IsFalse();
  }

  [Test]
  public async Task TryBeginRepair_WhileItsOwnAnswerIsPending_IsRefusedAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var key = _key(origin, Guid.Empty);
    _ = await coordinator.IntegrityTryBeginRepairAsync(key, _t0, _backoff, MAX_ATTEMPTS);
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [key], 0, 100, _t0, _answerTimeout);

    var inFlight = await coordinator.IntegrityTryBeginRepairAsync(key, _t0.AddHours(1), _backoff, MAX_ATTEMPTS);
    var afterTimeout = await coordinator.IntegrityTryBeginRepairAsync(key, _t0 + _answerTimeout, _backoff, MAX_ATTEMPTS);

    await Assert.That(inFlight).IsFalse();
    await Assert.That(afterTimeout).IsTrue();
  }

  // ===== #2030: what resets the ladder =====

  [Test]
  public async Task Report_TheLocalDigestMoved_ResetsTheLadder_AndLiftsTheMarkAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var key = _key(origin, stream);
    await _reportAsync(coordinator, key, deficit: 3, at: _t0);
    _ = await _claimAsync(coordinator, origin, _t0.AddSeconds(1));
    _ = await coordinator.IntegrityMarkUnhealableBatchAsync(origin, [key], _t0.AddMinutes(1));

    await _reportAsync(coordinator, key, deficit: 2, at: _t0.AddMinutes(2), localLo: 5, localHi: 6);

    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<int>(conn, "SELECT repair_attempts FROM wh_integrity_ledger WHERE stream_id = $1", stream)).IsEqualTo(0);
    await Assert.That((await _claimAsync(coordinator, origin, _t0.AddMinutes(3))).Count).IsEqualTo(1)
      .Because("the receiver's own events changed, which is what repair acts on: a fresh ladder, and the mark no longer holds");
  }

  [Test]
  public async Task Report_OnlyTheOriginsDigestMoved_KeepsTheAttempts_AndTheMarkAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var key = _key(origin, stream);
    await _reportAsync(coordinator, key, deficit: 3, at: _t0);
    _ = await _claimAsync(coordinator, origin, _t0.AddSeconds(1));
    _ = await coordinator.IntegrityMarkUnhealableBatchAsync(origin, [key], _t0.AddMinutes(1));

    await _reportAsync(coordinator, key, deficit: 3, at: _t0.AddMinutes(2), originLo: 99, originHi: 98);

    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<int>(conn, "SELECT repair_attempts FROM wh_integrity_ledger WHERE stream_id = $1", stream)).IsEqualTo(1)
      .Because("new events on the origin's side do not change what a redelivery can do here");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT unhealable_at IS NOT NULL FROM wh_integrity_ledger WHERE stream_id = $1", stream)).IsTrue();
    await Assert.That((await _claimAsync(coordinator, origin, _t0.AddDays(1))).Count).IsEqualTo(0);
  }

  [Test]
  public async Task Report_TheDeficitGrew_ResetsTheLadder_AndLiftsTheMarkAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var key = _key(origin, stream);
    await _reportAsync(coordinator, key, deficit: 3, at: _t0);
    _ = await _claimAsync(coordinator, origin, _t0.AddSeconds(1));
    _ = await coordinator.IntegrityMarkUnhealableBatchAsync(origin, [key], _t0.AddMinutes(1));

    await _reportAsync(coordinator, key, deficit: 5, at: _t0.AddMinutes(2), originLo: 99, originHi: 98);

    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<int>(conn, "SELECT repair_attempts FROM wh_integrity_ledger WHERE stream_id = $1", stream)).IsEqualTo(0);
    await Assert.That((await _claimAsync(coordinator, origin, _t0.AddMinutes(3))).Count).IsEqualTo(1)
      .Because("more events are missing than when the mark was made: that is a new incident");
  }

  // ===== #2034: the type lane =====

  [Test]
  public async Task MarkAnswerPending_OnTheTypeLane_CoversThePerStreamRowsInsideItsWindowAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var inside = _key(origin, TrackedGuid.New().Value);
    var outside = _key(origin, TrackedGuid.New().Value);
    await _reportAsync(coordinator, inside, deficit: 1, at: _t0);
    await _reportAsync(coordinator, outside, deficit: 1, at: _t0);
    await coordinator.IntegrityStampRepairWindowsAsync(origin, [inside], 100, 500);
    await coordinator.IntegrityStampRepairWindowsAsync(origin, [outside], 600, 900);

    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [_key(origin, Guid.Empty)], 0, 550, _t0, _answerTimeout);
    var claimed = await _claimAsync(coordinator, origin, _t0.AddSeconds(1));

    await Assert.That(claimed.Select(c => c.StreamId)).IsEquivalentTo([outside.StreamId])
      .Because("the bulk request re-ships the window [0, 550], which answers the first row and not the second");
  }

  [Test]
  public async Task TryBeginRepair_OnTheTypeLane_StandsDown_WhilePerStreamRowsOfTheTypeAwaitTheirAnswerAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var perStream = _key(origin, TrackedGuid.New().Value);
    await _reportAsync(coordinator, perStream, deficit: 1, at: _t0);
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [perStream], null, null, _t0, _answerTimeout);

    var whilePending = await coordinator.IntegrityTryBeginRepairAsync(_key(origin, Guid.Empty), _t0.AddMinutes(1), _backoff, MAX_ATTEMPTS);
    var afterTimeout = await coordinator.IntegrityTryBeginRepairAsync(_key(origin, Guid.Empty), _t0 + _answerTimeout, _backoff, MAX_ATTEMPTS);

    await Assert.That(whilePending).IsFalse().Because("the per-stream requests already re-ship the type's events");
    await Assert.That(afterTimeout).IsTrue();
  }

  // ===== #2035 and #2029 gauges =====

  [Test]
  public async Task Summary_CountsBucketsAwaitingAnAnswer_AnsweredBuckets_AndTheIntegrityDeliveriesWaitingAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var pending = _key(origin, TrackedGuid.New().Value);
    await _reportAsync(coordinator, pending, deficit: 1, at: _t0);
    // Stamped far in the future, so the database clock the summary reads is inside the pending window.
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [pending], null, null, _t0.AddYears(100), _answerTimeout);
    await using var conn = await _openAsync();
    var answeredStream = TrackedGuid.New().Value;
    var answered = _key(origin, answeredStream, await _normalizedTypeAsync(conn));
    await _reportAsync(coordinator, answered, deficit: 1, at: _t0);
    var child = TrackedGuid.New().Value;
    await _storeAsync(_message(child, answeredStream, origin, 3));
    _ = await coordinator.IntegrityRecordBundleAnswerAsync(origin, [child], [3], _t0);
    _ = await coordinator.EnqueueIntegrityDeliveryAsync(TrackedGuid.New().Value, "test:delivery", "{}");
    _ = await coordinator.EnqueueIntegrityDeliveryAsync(TrackedGuid.New().Value, "test:delivery", "{\"n\":2}");

    var summary = await coordinator.GetIntegrityLedgerSummaryAsync(MAX_ATTEMPTS);

    await Assert.That(summary.AnswerPendingBuckets).IsEqualTo(1L);
    await Assert.That(summary.AnsweredBuckets).IsEqualTo(1L);
    await Assert.That(summary.IntegrityDeliveriesPending).IsEqualTo(2L);
    await Assert.That(summary.OldestIntegrityDeliveryAgeSeconds).IsGreaterThanOrEqualTo(0d);
  }

  [Test]
  public async Task Summary_WithNoIntegrityDeliveries_ReportsNoneWaitingAsync() {
    var summary = await _coordinator().GetIntegrityLedgerSummaryAsync(MAX_ATTEMPTS);

    await Assert.That(summary.IntegrityDeliveriesPending).IsEqualTo(0L);
    await Assert.That(summary.OldestIntegrityDeliveryAgeSeconds).IsEqualTo(0d);
  }

  // ===== Through the throughput gate, and during shutdown =====

  [Test]
  public async Task MarkAnswerPending_ThroughAThroughputGatedCoordinator_MarksAsync() {
    var origin = TrackedGuid.New().Value;
    var key = _key(origin, TrackedGuid.New().Value);
    await _reportAsync(_coordinator(), key, deficit: 1, at: _t0);
    using var gate = _gate();

    var marked = await _coordinator(gate).IntegrityMarkAnswerPendingAsync(origin, [key], null, null, _t0, _answerTimeout);

    await Assert.That(marked).IsEqualTo(1).Because("the gate bounds how many calls run at once; it does not change the answer");
  }

  [Test]
  public async Task RecordBundleAnswer_ThroughAThroughputGatedCoordinator_RecordsAsync() {
    using var gate = _gate();

    var answer = await _coordinator(gate).IntegrityRecordBundleAnswerAsync(
      TrackedGuid.New().Value, [TrackedGuid.New().Value], [1], _t0);

    await Assert.That(answer).IsNotNull();
    await Assert.That(answer!.Absent).IsEqualTo(1);
  }

  [Test]
  public async Task MarkAnswerPending_CanceledDuringShutdown_PropagatesInsteadOfReadingAsNothingMarkedAsync() {
    var origin = TrackedGuid.New().Value;
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    // Non-empty keys: an empty list returns before the call, which would never reach the arm under test.
    await Assert.That(async () => await _coordinator().IntegrityMarkAnswerPendingAsync(
        origin, [_key(origin, TrackedGuid.New().Value)], null, null, _t0, _answerTimeout, stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("0 marked reads as 'nothing was requested', so a swallowed shutdown would be taken as a settled answer");
  }

  [Test]
  public async Task RecordBundleAnswer_CanceledDuringShutdown_PropagatesInsteadOfReadingAsUnrecordedAsync() {
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    await Assert.That(async () => await _coordinator().IntegrityRecordBundleAnswerAsync(
        TrackedGuid.New().Value, [TrackedGuid.New().Value], [1], _t0, stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("null sends the reconcile on without the commit's view, which a shutdown is no reason to do");
  }

  // ===== Helpers =====

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinatorGate? gate = null) {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions(), gate: gate);
  }

  private static WorkCoordinatorGate _gate() =>
    new(maxConcurrent: 1, logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorGate>.Instance);

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, Guid stream, string eventType = "Contracts.AnswerProbe") =>
    new(origin, null, eventType, stream);

  private static async Task _reportAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, IntegrityRepairLedger.DivergenceKey key, long deficit,
      DateTimeOffset at, long originLo = 11, long originHi = 21, long localLo = 0, long localHi = 0) {
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      key.OriginServiceId, [new IntegrityReportObservation(key, originLo, originHi, localLo, localHi, IsDeficit: true, DeficitEvents: deficit)],
      at, _cooldown) ?? throw new InvalidOperationException("the report batch failed");
  }

  private static Task<IReadOnlyList<IntegrityRepairDrainItem>> _claimAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, Guid origin, DateTimeOffset at) =>
    coordinator.IntegrityClaimRepairDrainAsync([origin], at, _backoff, MAX_ATTEMPTS, limit: 10);

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static InboxMessage _message(Guid messageId, Guid streamId, Guid origin, long sequence) => new() {
    MessageId = messageId,
    HandlerName = "OrderPlacedHandler",
    Envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonElement.Parse("""{"reference":"R-1"}"""),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Outbox },
    },
    EnvelopeType = EnvelopeTypeNameHelper.Format(MESSAGE_TYPE),
    StreamId = streamId,
    IsEvent = true,
    Flags = EventFlags.None,
    Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
    MessageType = MESSAGE_TYPE,
    SourceServiceId = origin,
    SourceCommitSequence = sequence,
    Priority = 250,
    RestoreIfMissing = true,
  };

  private async Task _storeAsync(params InboxMessage[] messages) =>
    await _coordinator().StoreInboxMessagesAsync(messages, partitionCount: 100);

  /// <summary>An event stored as the emit chain stores a received one; a null origin is history from before origins were recorded.</summary>
  private static async Task<Guid> _storeEventAsync(
      NpgsqlConnection conn, Guid stream, int version, Guid? origin, long? originSequence, string? tenant = null) {
    var eventId = TrackedGuid.New().Value;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, flags, created_at,
         origin_service_id, origin_commit_sequence, scope)
      VALUES ($1, $2, $2, 'Upstream', normalize_event_type($3), $4, nextval('wh_commit_seq'), 0, NOW(), $5, $6,
              CASE WHEN $7::text IS NULL THEN NULL ELSE jsonb_build_object('t', $7::text) END)
      """;
    cmd.Parameters.Add(new NpgsqlParameter { Value = eventId });
    cmd.Parameters.Add(new NpgsqlParameter { Value = stream });
    cmd.Parameters.Add(new NpgsqlParameter { Value = MESSAGE_TYPE });
    cmd.Parameters.Add(new NpgsqlParameter { Value = version });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)origin ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)originSequence ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)tenant ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
    await cmd.ExecuteNonQueryAsync();
    return eventId;
  }

  /// <summary>The deduplication entry a first arrival leaves behind.</summary>
  private static async Task _rememberAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO wh_message_deduplication (message_id, first_seen_at, observation_count) VALUES ($1, NOW(), 1)";
    cmd.Parameters.Add(new NpgsqlParameter { Value = messageId });
    await cmd.ExecuteNonQueryAsync();
  }

  private static Task<string> _normalizedTypeAsync(NpgsqlConnection conn) =>
    _scalarAsync<string>(conn, "SELECT normalize_event_type($1)", MESSAGE_TYPE);

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES ($1, 'repair-svc', 'repair-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    cmd.Parameters.Add(new NpgsqlParameter { Value = instanceId });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _leaseAsync(NpgsqlConnection conn, Guid instanceId, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      UPDATE wh_inbox_state
      SET instance_id = $1, lease_expiry = NOW() + INTERVAL '5 minutes', attempts = attempts + 1
      WHERE message_id = $2
      """;
    cmd.Parameters.Add(new NpgsqlParameter { Value = instanceId });
    cmd.Parameters.Add(new NpgsqlParameter { Value = messageId });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _emitInboxChainAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _emit_event_store_chain_for_inbox($1, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    cmd.Parameters.Add(new NpgsqlParameter { Value = instanceId });
    await cmd.ExecuteScalarAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }
}
