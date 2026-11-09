// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2211: a whole-type request (a bulk backfill or a checkpoint gap, recorded on the type lane: the empty stream) whose
/// answer brings nothing new stops being asked. The request's window is stamped on the type lane when it leaves, and a
/// committed answer bundle that covered that window and stored nothing marks the type lane as a bucket repair cannot
/// change, as a per-stream bucket is marked. Before, the type lane was only recorded as answered, so the same whole-type
/// request was granted again on the ladder and the origin re-shipped events this service already held, every time.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/383_AWholeTypeAnswerThatBringsNothingStopsTheAsking.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#a-whole-type-answer-that-brings-nothing-stops-the-asking</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityTypeLaneAnswerSqlTests : EFCoreTestBase {
  private const string MESSAGE_TYPE = "Upstream.Contracts.ThingsAppliedCollectiveEvent, Upstream.Contracts";
  private static readonly DateTimeOffset _t0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _timeout = TimeSpan.FromHours(3);
  private readonly List<WorkCoordinationDbContext> _contexts = [];

  [Test]
  public async Task MarkAnswerPending_OnTheTypeLane_StampsTheRequestsWindowOnItAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();

    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);

    await Assert.That(await _scalarAsync<string>(conn,
      "SELECT window_from || '-' || window_until FROM wh_integrity_ledger WHERE stream_id = $1", Guid.Empty))
      .IsEqualTo("101-401").Because("the answer is judged against the window the request asked for");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnAnswerThatStoredNothing_CoveringTheTypeLanesRequest_MarksTheTypeLaneAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var (e1, e2) = (await _storeEventAsync(conn, origin, 150), await _storeEventAsync(conn, origin, 250));

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [e1, e2], [150, 250], new RedeliveryAnswerWindow(100, 400), _t0.AddMinutes(5));
    var granted = await coordinator.IntegrityTryBeginRepairAsync(lane, _t0.AddDays(2), TimeSpan.FromSeconds(1), 8);

    await Assert.That(answer!.MarkedUnchanged).Contains(lane)
      .Because("every event of the whole-type answer was already stored: asking for the type again re-ships the same events");
    await Assert.That(granted).IsFalse().Because("the bulk and checkpoint paths stop asking for the type");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnAnswerThatStoredSomething_LeavesTheTypeLaneRepairableAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var held = await _storeEventAsync(conn, origin, 150);
    var brought = TrackedGuid.New().Value;   // not in the store: the bundle stores it

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [held, brought], [150, 250], new RedeliveryAnswerWindow(100, 400), _t0.AddMinutes(5));

    await Assert.That(answer!.MarkedUnchanged).DoesNotContain(lane);
    await Assert.That(await _laneMarkedAsync(conn)).IsFalse();
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnAnswerNarrowerThanTheTypeLanesRequest_LeavesItRepairableAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var held = await _storeEventAsync(conn, origin, 250);

    _ = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [held], [250], new RedeliveryAnswerWindow(200, 400), _t0.AddMinutes(5));

    await Assert.That(await _laneMarkedAsync(conn)).IsFalse()
      .Because("the answer did not look at sequences 101 to 200, where the type's deficit may lie");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_ATypeLaneWithNoRequestedWindow_IsMarkedOnlyByAWholeHistoryAnswerAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    var held = await _storeEventAsync(conn, origin, 250);

    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], null, null, _t0, _timeout);
    _ = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [held], [250], new RedeliveryAnswerWindow(100, 400), _t0.AddMinutes(5));
    var windowedMarked = await _laneMarkedAsync(conn);
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], null, null, _t0.AddMinutes(6), _timeout);
    var whole = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [held], [250], new RedeliveryAnswerWindow(null, null), _t0.AddMinutes(7));

    await Assert.That(windowedMarked).IsFalse().Because("a type lane with no window was found over the whole history");
    await Assert.That(whole!.MarkedUnchanged).Contains(lane);
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_ATypeLaneNotAwaitingAnAnswer_IsNotMarkedByAnotherRequestsAnswerAsync() {
    var (coordinator, conn, origin, _) = await _arrangeAsync();
    var held = await _storeEventAsync(conn, origin, 250);

    // An answer to a request for named streams of the type: the type lane asked for nothing.
    _ = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [held], [250], new RedeliveryAnswerWindow(null, null), _t0.AddMinutes(5));

    await Assert.That(await _laneMarkedAsync(conn)).IsFalse()
      .Because("an answer for some streams of the type says nothing about the type's other streams");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnAnswerThatDoesNotSayItsWindow_LeavesTheTypeLaneRepairableAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var held = await _storeEventAsync(conn, origin, 250);

    _ = await coordinator.IntegrityRecordBundleAnswerAsync(origin, [held], [250], null, _t0.AddMinutes(5));

    await Assert.That(await _laneMarkedAsync(conn)).IsFalse()
      .Because("an origin that does not say what it answered cannot be held to the window the type lane asked for");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnotherTypesLane_IsNotMarkedByThisTypesAnswerAsync() {
    var (coordinator, conn, origin, _) = await _arrangeAsync();
    var otherLane = new IntegrityRepairLedger.DivergenceKey(origin, null, "Contracts.SomethingElse", Guid.Empty);
    await _reportAsync(coordinator, otherLane);
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [otherLane], 101, 401, _t0, _timeout);
    var held = await _storeEventAsync(conn, origin, 250);

    _ = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [held], [250], new RedeliveryAnswerWindow(100, 400), _t0.AddMinutes(5));

    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT unhealable_at IS NULL FROM wh_integrity_ledger WHERE event_type = 'Contracts.SomethingElse'")).IsTrue();
    await conn.DisposeAsync();
  }

  // ===== helpers =====

  private async Task<(EFCoreWorkCoordinator<WorkCoordinationDbContext> Coordinator, NpgsqlConnection Conn, Guid Origin,
      IntegrityRepairLedger.DivergenceKey Lane)> _arrangeAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var type = await _scalarAsync<string>(conn, "SELECT normalize_event_type($1)", MESSAGE_TYPE);
    var lane = new IntegrityRepairLedger.DivergenceKey(origin, null, type, Guid.Empty);
    await _reportAsync(coordinator, lane);
    return (coordinator, conn, origin, lane);
  }

  private static Task<bool> _laneMarkedAsync(NpgsqlConnection conn) =>
    _scalarAsync<bool>(conn, "SELECT unhealable_at IS NOT NULL FROM wh_integrity_ledger WHERE stream_id = $1", Guid.Empty);

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private static async Task _reportAsync(EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, IntegrityRepairLedger.DivergenceKey key) =>
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      key.OriginServiceId, [new IntegrityReportObservation(key, 11, 21, 0, 0, IsDeficit: true, DeficitEvents: 5)], _t0, TimeSpan.FromMinutes(60))
      ?? throw new InvalidOperationException("the report batch failed");

  // One collective event: its own stream, the type of the lane, this origin's sequence.
  private static async Task<Guid> _storeEventAsync(NpgsqlConnection conn, Guid origin, long originSequence) {
    var eventId = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, flags, created_at,
         origin_service_id, origin_commit_sequence)
      VALUES ($1, $2, $2, 'Upstream', normalize_event_type($3), 1, nextval('wh_commit_seq'), 0, NOW(), $4, $5)
      """;
    foreach (var arg in new object[] { eventId, stream, MESSAGE_TYPE, origin, originSequence }) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    await cmd.ExecuteNonQueryAsync();
    return eventId;
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
