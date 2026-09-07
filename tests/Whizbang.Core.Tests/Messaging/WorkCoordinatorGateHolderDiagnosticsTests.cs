using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Helpers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A saturated gate must say who holds it. A live stall read 50/50 held against an idle database
/// and nothing in the logs could name the holders, so the cause stayed a hypothesis. The gate now
/// tracks each holder by caller and age, exposes a snapshot, and names the oldest holders when an
/// acquire deadlines.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/WorkCoordinatorGate.cs</code-under-test>
public class WorkCoordinatorGateHolderDiagnosticsTests {
  [Test]
  public async Task SnapshotHolders_NamesEveryCurrentHolder_AndForgetsReleasedOnesAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 3, acquireTimeoutMilliseconds: 1000);
    var a = await gate.AcquireAsync(CancellationToken.None, caller: "CommitHandlerBatchAsync");
    var b = await gate.AcquireAsync(CancellationToken.None, caller: "ReportPerspectiveCompletionAsync");

    var holders = gate.SnapshotHolders();
    await Assert.That(holders.Select(h => h.Caller)).IsEquivalentTo(["CommitHandlerBatchAsync", "ReportPerspectiveCompletionAsync"])
      .Because("every held slot is attributable to the coordinator method that took it");
    await Assert.That(holders.All(h => h.HeldMs >= 0)).IsTrue();

    a.Dispose();
    await Assert.That(gate.SnapshotHolders().Select(h => h.Caller)).IsEquivalentTo(["ReportPerspectiveCompletionAsync"])
      .Because("a released slot leaves the snapshot with its releaser");
    b.Dispose();
    await Assert.That(gate.SnapshotHolders()).IsEmpty();
  }

  [Test]
  public async Task Deadline_NamesTheHoldersInTheWarningAsync() {
    var logger = new CapturingLogger<WorkCoordinatorGate>();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 2, acquireTimeoutMilliseconds: 100, logger: logger);
    var a = await gate.AcquireAsync(CancellationToken.None, caller: "CommitHandlerBatchAsync");
    var b = await gate.AcquireAsync(CancellationToken.None, caller: "CommitHandlerBatchAsync");
    try {
      var deadlined = await gate.AcquireAsync(CancellationToken.None, caller: "ClaimWorkAsync");
      deadlined.Dispose();

      var warning = logger.Snapshot().FirstOrDefault(e => e.Level == LogLevel.Warning);
      await Assert.That(warning).IsNotNull();
      await Assert.That(warning!.Message).Contains("CommitHandlerBatchAsync x2")
        .Because("the warning groups holders by caller with a count, which is what identifies a stuck method");
      await Assert.That(warning.Message).DoesNotContain("ClaimWorkAsync x")
        .Because("the caller that deadlined is not a holder");
    } finally {
      a.Dispose();
      b.Dispose();
    }
  }

  [Test]
  public async Task DisabledGate_HasNoHoldersAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 0);
    var pass = await gate.AcquireAsync(CancellationToken.None, caller: "Anything");
    await Assert.That(gate.SnapshotHolders()).IsEmpty()
      .Because("a disabled gate hands out no slots, so there is nothing to attribute");
    pass.Dispose();
  }
}
