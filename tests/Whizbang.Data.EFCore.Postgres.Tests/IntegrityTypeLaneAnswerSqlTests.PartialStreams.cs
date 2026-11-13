// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2149: a whole-type answer bundle that carried one of the type's streams only in part has not shown that the type has
/// nothing missing, so it never marks the type lane, whatever window it claims.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/410_ABundleMarksOnlyStreamsItCarriedWhole.sql</code-under-test>
public partial class IntegrityTypeLaneAnswerSqlTests {
  [Test]
  public async Task RecordBundleAnswer_ABundleThatCarriedAStreamOfTheTypeInPart_LeavesTheTypeLaneRepairableAsync() {
    var (coordinator, conn, origin, lane) = await _arrangeAsync();
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [lane], 101, 401, _t0, _timeout);
    var (e1, e2) = (await _storeEventAsync(conn, origin, 150), await _storeEventAsync(conn, origin, 250));
    var split = await _scalarAsync<Guid>(conn, "SELECT stream_id FROM wh_event_store WHERE event_id = $1", e2);

    var answer = await coordinator.IntegrityRecordBundleAnswerAsync(
      origin, [e1, e2], [150, 250], new RedeliveryAnswerWindow(100, 400) { PartialStreamIds = [split] }, _t0.AddMinutes(5));

    await Assert.That(answer!.MarkedUnchanged).DoesNotContain(lane)
      .Because("the rest of that stream is in another bundle, which may yet store what the type is missing");
    await Assert.That(await _laneMarkedAsync(conn)).IsFalse();
    await conn.DisposeAsync();
  }
}
