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
/// #2311: a whole-type answer that spans several bundles is judged as a whole. A bundle the origin says more of the
/// answer follows (<see cref="RedeliveryAnswerWindow.Continues"/>) that changed nothing leaves the type lane awaiting the
/// rest, so only the answer's last bundle can mark it, and only when no bundle before it brought anything: a bundle that
/// stored something answers the type lane, which then no later bundle of the answer can mark.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/431_AWholeTypeAnswerIsJudgedWhole.sql</code-under-test>
public partial class IntegrityTypeLaneAnswerSqlTests {
  [Test]
  public async Task RecordBundleAnswer_AMultiBundleAnswerWhoseFirstBundleChangedNothing_DoesNotMarkTheTypeLaneBeforeItsLastBundleAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var (held, alsoHeld) = (await _storeEventAsync(conn, origin, 150), await _storeEventAsync(conn, origin, 200));
    var brought = TrackedGuid.New().Value;   // not in the store: the answer's second bundle stores it

    var first = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [held], [150], new RedeliveryAnswerWindow(100, 400) { Continues = true }, _t0.AddMinutes(5));
    var markedAfterTheFirst = await _laneMarkedAsync(conn);
    var awaitingAfterTheFirst = await _laneAwaitingAsync(conn);
    _ = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [alsoHeld, brought], [200, 250], new RedeliveryAnswerWindow(100, 400), _t0.AddMinutes(6));

    await Assert.That(first!.MarkedUnchanged).DoesNotContain(lane)
      .Because("the rest of the answer was still to come, and its next bundle stored what the type was missing");
    await Assert.That(markedAfterTheFirst).IsFalse();
    await Assert.That(awaitingAfterTheFirst).IsTrue()
      .Because("the type lane waits for the whole answer, not for its first bundle");
    await Assert.That(await _laneMarkedAsync(conn)).IsFalse()
      .Because("the answer as a whole brought something new, so the type is still worth asking for");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AMultiBundleAnswerThatChangedNothingAnywhere_MarksTheTypeLaneOnItsLastBundleAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var (e1, e2) = (await _storeEventAsync(conn, origin, 150), await _storeEventAsync(conn, origin, 250));

    var first = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [e1], [150], new RedeliveryAnswerWindow(100, 400) { Continues = true }, _t0.AddMinutes(5));
    var last = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [e2], [250], new RedeliveryAnswerWindow(100, 400), _t0.AddMinutes(6));

    await Assert.That(first!.MarkedUnchanged).DoesNotContain(lane);
    await Assert.That(last!.MarkedUnchanged).Contains(lane)
      .Because("every bundle of the whole-type answer found its events already stored: asking again re-ships them");
    await Assert.That(await _laneAwaitingAsync(conn)).IsFalse().Because("the last bundle answered the type lane");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnEarlierBundleThatStoredSomething_LeavesTheTypeLaneUnmarkedByTheLastAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var (alsoHeld, held) = (await _storeEventAsync(conn, origin, 120), await _storeEventAsync(conn, origin, 250));
    var brought = TrackedGuid.New().Value;   // not in the store: the answer's first bundle stores it

    _ = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [alsoHeld, brought], [120, 150], new RedeliveryAnswerWindow(100, 400) { Continues = true }, _t0.AddMinutes(5));
    var last = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [held], [250], new RedeliveryAnswerWindow(100, 400), _t0.AddMinutes(6));

    await Assert.That(last!.MarkedUnchanged).DoesNotContain(lane)
      .Because("an earlier bundle of the same answer brought something new, which answered the type lane");
    await Assert.That(await _laneMarkedAsync(conn)).IsFalse();
    await conn.DisposeAsync();
  }

  private static Task<bool> _laneAwaitingAsync(NpgsqlConnection conn) =>
    _scalarAsync<bool>(conn, "SELECT answer_pending_until IS NOT NULL FROM wh_integrity_ledger WHERE stream_id = $1", Guid.Empty);
}
