// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// The rule a replay folds a collective into a row by (#1970): through a handler whose apply completed, into every row;
/// partway or abandoned, into the rows up to its <c>through_id</c>; with no record, into no row while the collective is
/// queued or after a dead letter recorded that nothing was applied, and into every row otherwise.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Collective/CollectiveApplyReach.cs</code-under-test>
[Category("Collective")]
[Category("Shard4")]
public class CollectiveApplyReachTests {
  private const string ARCHIVE = "H.Archive:M";
  private const string REVIEW = "H.Review:M";
  private static readonly Guid _event = Guid.Parse("0f000000-0000-0000-0000-000000000000");
  private static readonly Guid _low = Guid.Parse("10000000-0000-0000-0000-000000000000");
  private static readonly Guid _through = Guid.Parse("20000000-0000-0000-0000-000000000000");
  private static readonly Guid _high = Guid.Parse("30000000-0000-0000-0000-000000000000");

  private static CollectiveApplyReach _reach(
      params (string? EntryKey, Guid? Through, bool Completed, bool Abandoned, bool Queued)[] rows) =>
    CollectiveApplyReach.From(rows.Select(r => (_event, r.EntryKey, r.Through, r.Completed, r.Abandoned, r.Queued)))[_event];

  [Test]
  public async Task ACompletedApply_ReachesEveryRowAsync() {
    var reach = _reach((ARCHIVE, _through, true, false, false));

    await Assert.That(reach.Reaches(ARCHIVE, _high)).IsTrue();
  }

  [Test]
  public async Task AnApplyPartway_ReachesTheRowsUpToItsThroughId_OnlyAsync() {
    var reach = _reach((ARCHIVE, _through, false, false, true), (null, null, false, false, true));

    await Assert.That(reach.Reaches(ARCHIVE, _low)).IsTrue();
    await Assert.That(reach.Reaches(ARCHIVE, _through)).IsTrue().Because("the batch that recorded through_id updated it");
    await Assert.That(reach.Reaches(ARCHIVE, _high)).IsFalse().Because("the live sink applies it to this row later");
    await Assert.That(reach.Queued).IsTrue();
  }

  [Test]
  public async Task AnAbandonedApply_ReachesTheRowsUpToItsThroughId_OnlyAsync() {
    var reach = _reach((ARCHIVE, _through, false, true, false), ("*", null, false, true, false));

    await Assert.That(reach.Reaches(ARCHIVE, _low)).IsTrue();
    await Assert.That(reach.Reaches(ARCHIVE, _high)).IsFalse().Because("the dead letter left this row without it");
    await Assert.That(reach.Queued).IsFalse();
  }

  [Test]
  public async Task ARecordWithNoThroughId_ThatIsNotComplete_ReachesNoRowAsync() {
    var reach = _reach((ARCHIVE, null, false, true, false));

    await Assert.That(reach.Reaches(ARCHIVE, _low)).IsFalse();
  }

  [Test]
  public async Task AHandlerWithNoRecord_WhileTheCollectiveIsQueued_ReachesNoRowAsync() {
    var reach = _reach((ARCHIVE, _through, true, false, false), (null, null, false, false, true));

    await Assert.That(reach.Reaches(REVIEW, _low)).IsFalse()
      .Because("the live sink has not started this handler's apply, and applies it to the replayed row afterwards");
  }

  [Test]
  public async Task AHandlerWithNoRecord_AfterADeadLetterRecordedNothingElseApplied_ReachesNoRowAsync() {
    var reach = _reach((ARCHIVE, _through, true, false, false), ("*", null, false, true, false));

    await Assert.That(reach.Reaches(REVIEW, _low)).IsFalse();
    await Assert.That(reach.Reaches(ARCHIVE, _high)).IsTrue().Because("the finished handler reached every row before the dead letter");
  }

  [Test]
  public async Task AHandlerWithNoRecord_OtherwiseReachesEveryRowAsync() {
    var reach = _reach((ARCHIVE, _through, true, false, false), ("*", null, false, false, false));

    await Assert.That(reach.Reaches(REVIEW, _high)).IsTrue()
      .Because("a handler applied before records existed, or whose record was swept, applied in full");
  }
}
