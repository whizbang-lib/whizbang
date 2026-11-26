// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2601: which fetched rows the drain warns about as leased without a failure, and what it tells the startup barrier.
/// Every fetched row's leases are recorded on the startup set while the barrier waits, so the cap's Warning can say how
/// often the queued row of a cursor still rewinding was leased; a row is warned about once its leases reach the bound
/// with no failure recorded, and once per process.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.LeasedWithoutFailure.cs</code-under-test>
public class PerspectiveWorkerLeasedWithoutFailureTests {
  private const string PERSPECTIVE = "Orders+Projection";

  private static StreamEventData _row(Guid streamId, int attempts, int failures) => new() {
    StreamId = streamId,
    EventId = Guid.CreateVersion7(),
    EventType = "Probe.Event, Probe",
    EventData = "{}",
    EventWorkId = Guid.CreateVersion7(),
    PerspectiveName = PERSPECTIVE,
    Attempts = attempts,
    Failures = failures,
  };

  [Test]
  public async Task RowsLeasedWithoutFailure_AreThoseAtTheBoundWithNoFailure_AndEveryRowsLeasesReachTheStartupSetAsync() {
    var stream = Guid.CreateVersion7();
    var startup = new StartupRewindSet([new RewindCursorInfo(stream, PERSPECTIVE, null, null)]);
    var atBound = _row(stream, attempts: 25, failures: 0);
    List<StreamEventData> rows = [atBound, _row(stream, attempts: 24, failures: 0), _row(Guid.CreateVersion7(), attempts: 90, failures: 2)];

    var stuck = PerspectiveWorker.RowsLeasedWithoutFailure(rows, bound: 25, startup);

    await Assert.That(stuck).IsEquivalentTo([atBound]);
    await Assert.That(startup.DescribeRemaining(10)).Contains("(queued row leased 25 times, 0 failures)")
      .Because("the cursor still rewinding names how often its queued row was leased");
  }

  [Test]
  public async Task RowsLeasedWithoutFailure_TurnedOff_AreNone_AndWithNoStartupSetNothingIsRecordedAsync() {
    List<StreamEventData> rows = [_row(Guid.CreateVersion7(), attempts: 500, failures: 0)];

    await Assert.That(PerspectiveWorker.RowsLeasedWithoutFailure(rows, bound: null, startup: null)).IsEmpty();
    await Assert.That(PerspectiveWorker.RowsLeasedWithoutFailure(rows, bound: 25, startup: null).Count).IsEqualTo(1);
  }

  [Test]
  public async Task FirstWarningOf_IsTrueOncePerRow_AndTheMemoryStartsOverAtItsCapAsync() {
    var warned = new ConcurrentDictionary<Guid, byte>();
    var (a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

    await Assert.That(PerspectiveWorker.FirstWarningOf(warned, a, cap: 2)).IsTrue();
    await Assert.That(PerspectiveWorker.FirstWarningOf(warned, a, cap: 2)).IsFalse().Because("a row is warned about once");
    await Assert.That(PerspectiveWorker.FirstWarningOf(warned, b, cap: 2)).IsTrue();
    await Assert.That(PerspectiveWorker.FirstWarningOf(warned, c, cap: 2)).IsTrue()
      .Because("at the cap the memory starts over, so it stays bounded");
    await Assert.That(warned.Count).IsEqualTo(1);
  }
}
