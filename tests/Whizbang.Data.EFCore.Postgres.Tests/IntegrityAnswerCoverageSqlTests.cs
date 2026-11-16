// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A redelivery answer marks a ledger bucket as one repair cannot change only when it covered the bucket's deficit
/// (#2143): its window includes the bucket's compared window (a whole-history bucket needs a whole-history answer), or it
/// carried the bucket's named missing event (#2103). Any other bucket is recorded as answered and stays repairable. Rows
/// an earlier release marked wrongly are re-armed once by migration 357.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/357_AnAnswerMarksOnlyWhatItCovered.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
[Category("Integration")]
[Category("Shard5")]
public partial class IntegrityAnswerCoverageSqlTests : EFCoreTestBase {
  private const string MESSAGE_TYPE = "Upstream.Contracts.OrderPlaced, Upstream.Contracts";
  private static readonly DateTimeOffset _t0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);
  private readonly List<WorkCoordinationDbContext> _contexts = [];

  // ===== the commit-time record =====

  [Test]
  public async Task RecordBundleAnswer_AWindowedAnswer_LeavesAWholeHistoryBucketRepairable_ButAnsweredAsync() {
    var (coordinator, conn, origin, stream, key) = await _arrangeAsync();
    var stored = await _storeEventAsync(conn, stream, origin, originSequence: 304);

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [stored], [304], new RedeliveryAnswerWindow(300, 400), _t0.AddMinutes(5));

    await Assert.That(answer!.AllPresent).IsTrue().Because("precondition: the bundle stored nothing");
    await Assert.That(answer.MarkedUnchanged).IsEmpty()
      .Because("the bucket's deficit is its whole history, and the answer looked at sequences 301 to 400 only");
    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT unhealable_at IS NULL AND answered_at IS NOT NULL FROM wh_integrity_ledger WHERE stream_id = $1", stream))
      .IsTrue().Because("the answer is still recorded, as today; only the mark is withheld");
    await Assert.That(key.StreamId).IsEqualTo(stream);
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AWholeHistoryAnswerThatStoredNothing_MarksAWholeHistoryBucketAsync() {
    var (coordinator, conn, origin, stream, key) = await _arrangeAsync();
    var stored = await _storeEventAsync(conn, stream, origin, originSequence: 4);

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [stored], [4], new RedeliveryAnswerWindow(null, null), _t0.AddMinutes(5));

    await Assert.That(answer!.MarkedUnchanged).IsEquivalentTo([key]);
    await conn.DisposeAsync();
  }

  /// <summary>The ledger stamps [since, until) as window_from = since, window_until = until; a request asks for the
  /// exclusive floor since - 1 and the inclusive ceiling until - 1. Stamped [5, 11) is covered by (4, 10] or wider.</summary>
  [Test]
  [Arguments(4L, 10L, true)]
  [Arguments(3L, 20L, true)]
  [Arguments(null, 10L, true)]
  [Arguments(5L, 10L, false)]
  [Arguments(4L, 9L, false)]
  public async Task RecordBundleAnswer_AWindowedBucket_IsMarkedOnlyByAnAnswerThatCoversItsWindowAsync(
      long? from, long? to, bool marked) {
    var (coordinator, conn, origin, stream, key) = await _arrangeAsync();
    await coordinator.IntegrityStampRepairWindowsAsync(origin, [key], 5, 11);
    var stored = await _storeEventAsync(conn, stream, origin, originSequence: 7);

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [stored], [7], new RedeliveryAnswerWindow(from, to), _t0.AddMinutes(5));

    await Assert.That(answer!.MarkedUnchanged.Count).IsEqualTo(marked ? 1 : 0);
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnAnswerCarryingTheNamedMissingEvent_MarksHoweverNarrowItsWindowAsync() {
    var (coordinator, conn, origin, stream, key) = await _arrangeAsync();
    var stored = await _storeEventAsync(conn, stream, origin, originSequence: 304);
    await ((IWorkCoordinator)coordinator).IntegrityRecordMissingEventsAsync(origin, [key], [stored]);

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [stored], [304], new RedeliveryAnswerWindow(303, 304), _t0.AddMinutes(5));

    await Assert.That(answer!.MarkedUnchanged).IsEquivalentTo([key])
      .Because("the bucket's one missing event was in the answer and is already stored: asking again changes nothing (#2103)");
    await conn.DisposeAsync();
  }

  // ===== the reconcile's mark =====

  [Test]
  public async Task MarkUnhealable_WithAWindow_MarksOnlyTheRowsTheAnswerCoveredAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var (whole, windowed, named) = (_key(origin), _key(origin), _key(origin));
    foreach (var key in new[] { whole, windowed, named }) {
      await _reportAsync(coordinator, key);
    }
    await coordinator.IntegrityStampRepairWindowsAsync(origin, [windowed], 301, 401);
    var missing = TrackedGuid.New().Value;
    await ((IWorkCoordinator)coordinator).IntegrityRecordMissingEventsAsync(origin, [named], [missing]);

    var marked = await coordinator.IntegrityMarkUnhealableBatchAsync(
      origin, [whole, windowed, named], new RedeliveryAnswerWindow(300, 400), [missing], _t0.AddMinutes(5));
    var wholeAnswer = await coordinator.IntegrityMarkUnhealableBatchAsync(
      origin, [whole], new RedeliveryAnswerWindow(null, null), [], _t0.AddMinutes(6));

    await Assert.That(marked).IsEquivalentTo([windowed, named])
      .Because("the windowed row lies inside the answer and the named row's event was in it; the whole-history row does not");
    await Assert.That(wholeAnswer).IsEquivalentTo([whole]);
  }

  [Test]
  public async Task MarkUnhealable_WithAWindow_UnreachableStore_AnswersUnsupportedAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    await using (var conn = await _openAsync()) {
      await using var drop = conn.CreateCommand();
      drop.CommandText = "DROP FUNCTION wh_integrity_mark_unhealable_covered_batch";
      await drop.ExecuteNonQueryAsync();
    }

    var marked = await coordinator.IntegrityMarkUnhealableBatchAsync(
      origin, [_key(origin)], new RedeliveryAnswerWindow(1, 2), [], _t0);

    await Assert.That(marked).IsNull().Because("a failed mark is logged and marks nothing, so repair keeps asking");
  }

  // ===== the correction (357) =====

  [Test]
  public async Task Correction_ReArmsWholeHistoryRowsMarkedWithoutAnAttemptOfTheirOwn_OnceAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var (wrong, attempted, windowed, later) = (_key(origin), _key(origin), _key(origin), _key(origin));
    foreach (var key in new[] { wrong, attempted, windowed }) {
      await _reportAsync(coordinator, key);
    }
    await coordinator.IntegrityStampRepairWindowsAsync(origin, [windowed], 301, 401);
    _ = await coordinator.IntegrityMarkUnhealableBatchAsync(origin, [wrong, attempted, windowed], _t0.AddMinutes(1));
    await using var conn = await _openAsync();
    await _executeAsync(conn, "UPDATE wh_integrity_ledger SET repair_attempts = 1, answered_at = $1 WHERE stream_id = $2",
      _t0, attempted.StreamId);
    await _executeAsync(conn, "UPDATE wh_integrity_ledger SET answered_at = $1", _t0.AddMinutes(1));
    // The test database ran 357 when it was made; this test runs the correction as a database that has not.
    await _executeAsync(conn, "DELETE FROM wh_settings WHERE setting_key = 'integrity_rearm_narrow_answers_version'");

    var reArmed = await _scalarAsync<int>(conn, "SELECT wh_integrity_rearm_narrow_answers()");
    await _reportAsync(coordinator, later);
    _ = await coordinator.IntegrityMarkUnhealableBatchAsync(origin, [later], _t0.AddMinutes(2));
    var again = await _scalarAsync<int>(conn, "SELECT wh_integrity_rearm_narrow_answers()");

    await Assert.That(reArmed).IsEqualTo(1);
    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT unhealable_at IS NULL AND answered_at IS NULL FROM wh_integrity_ledger WHERE stream_id = $1", wrong.StreamId))
      .IsTrue().Because("a whole-history bucket marked before any repair of its own was marked by another request's answer");
    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_integrity_ledger WHERE unhealable_at IS NOT NULL AND stream_id = ANY($1)",
      new[] { attempted.StreamId, windowed.StreamId, later.StreamId })).IsEqualTo(3L)
      .Because("a row with an attempt of its own, a windowed row, and anything marked after the correction ran are left alone");
    await Assert.That(again).IsEqualTo(0).Because("the correction runs once per database");
  }

  // ===== helpers =====

  private async Task<(EFCoreWorkCoordinator<WorkCoordinationDbContext> Coordinator, NpgsqlConnection Conn, Guid Origin, Guid Stream,
      IntegrityRepairLedger.DivergenceKey Key)> _arrangeAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var conn = await _openAsync();
    var type = await _scalarAsync<string>(conn, "SELECT normalize_event_type($1)", MESSAGE_TYPE);
    var key = new IntegrityRepairLedger.DivergenceKey(origin, null, type, stream);
    await _reportAsync(coordinator, key);
    return (coordinator, conn, origin, stream, key);
  }

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

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin) =>
    new(origin, null, "Contracts.CoverageProbe", TrackedGuid.New().Value);

  private static async Task _reportAsync(EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, IntegrityRepairLedger.DivergenceKey key) =>
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      key.OriginServiceId, [new IntegrityReportObservation(key, 11, 21, 0, 0, IsDeficit: true, DeficitEvents: 1)], _t0, _cooldown)
      ?? throw new InvalidOperationException("the report batch failed");

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<Guid> _storeEventAsync(NpgsqlConnection conn, Guid stream, Guid origin, long originSequence) {
    var eventId = TrackedGuid.New().Value;
    await _executeAsync(conn, """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, flags, created_at,
         origin_service_id, origin_commit_sequence)
      VALUES ($1, $2, $2, 'Upstream', normalize_event_type($3), 1, nextval('wh_commit_seq'), 0, NOW(), $4, $5)
      """, eventId, stream, MESSAGE_TYPE, origin, originSequence);
    return eventId;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(arg is Guid[] ids
        ? new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid }
        : new NpgsqlParameter { Value = arg });
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(arg is Guid[] ids
        ? new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid }
        : new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }
}
