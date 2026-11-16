// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2149: a bundle that carried only part of a stream's answer names the stream, and the commit-time record never marks
/// that stream's bucket as one repair cannot change, however wide the window it claims. Its other buckets are judged by
/// the window as before.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/410_ABundleMarksOnlyStreamsItCarriedWhole.sql</code-under-test>
public partial class IntegrityAnswerCoverageSqlTests {
  [Test]
  public async Task RecordBundleAnswer_AStreamTheBundleCarriedOnlyInPart_IsAnsweredButStaysRepairableAsync() {
    var (coordinator, conn, origin, stream, _) = await _arrangeAsync();
    var stored = await _storeEventAsync(conn, stream, origin, originSequence: 4);

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [stored], [4], new RedeliveryAnswerWindow(null, null) { PartialStreamIds = [stream] }, _t0.AddMinutes(5));

    await Assert.That(answer!.AllPresent).IsTrue().Because("precondition: the bundle stored nothing");
    await Assert.That(answer.MarkedUnchanged).IsEmpty()
      .Because("the stream's missing events may be in another bundle of the answer, or beyond the origin's cap");
    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT unhealable_at IS NULL AND answered_at IS NOT NULL FROM wh_integrity_ledger WHERE stream_id = $1", stream))
      .IsTrue().Because("the answer is still recorded; only the mark is withheld");
    await conn.DisposeAsync();
  }

  [Test]
  public async Task RecordBundleAnswer_AnotherStreamCarriedInPart_DoesNotStopTheMarkOfAStreamCarriedWholeAsync() {
    var (coordinator, conn, origin, stream, key) = await _arrangeAsync();
    var stored = await _storeEventAsync(conn, stream, origin, originSequence: 4);

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [stored], [4], new RedeliveryAnswerWindow(null, null) { PartialStreamIds = [TrackedGuid.New().Value] },
      _t0.AddMinutes(5));

    await Assert.That(answer!.MarkedUnchanged).IsEquivalentTo([key])
      .Because("this stream came whole in a whole-history answer that stored nothing: asking again re-ships the same events");
    await conn.DisposeAsync();
  }
}
