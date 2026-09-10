using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Priority;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The work coordinator gate reserves a slice of its permits for interactive callers (priority step 4).
/// Ordering alone does not protect latency when the shared resource is held by stalled bulk work: a batch of
/// background commits waiting on the database could take every permit, and the interactive command behind
/// them would wait on the gate. A caller's bucket is the ambient parent of the handling it runs in
/// (<see cref="PriorityContext"/>); non-interactive callers can never take the last reserved permits, while
/// interactive callers use the shared permits first and the reserve only when those are gone.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#bulkheads</docs>
[Category("Unit")]
public class WorkCoordinatorGateInteractiveReserveTests {

  [Test]
  public async Task Acquire_NonInteractiveCallers_NeverTakeTheReservedSliceAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 3, acquireTimeoutMilliseconds: 50, interactiveReserve: 1);
    using var first = await gate.AcquireAsync(CancellationToken.None);
    using var second = await gate.AcquireAsync(CancellationToken.None);

    using var third = await gate.AcquireAsync(CancellationToken.None);

    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(2)
      .Because("two permits are shared and one is reserved; a third non-interactive caller gets the degraded releaser, not the reserve");
  }

  [Test]
  public async Task Acquire_AnInteractiveCaller_TakesTheReserveWhenTheSharedPermitsAreGoneAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 3, acquireTimeoutMilliseconds: 50, interactiveReserve: 1);
    using var first = await gate.AcquireAsync(CancellationToken.None);
    using var second = await gate.AcquireAsync(CancellationToken.None);

    WorkCoordinatorGate.Releaser interactive;
    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      interactive = await gate.AcquireAsync(CancellationToken.None);
    }

    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(3)
      .Because("the interactive caller is what the reserve exists for; bulk work holding the shared permits cannot make it wait");
    interactive.Dispose();
    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(2)
      .Because("disposing returns the reserved permit to the reserve, not to the shared pool");

    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      using var again = await gate.AcquireAsync(CancellationToken.None);
      await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(3);
    }
  }

  [Test]
  public async Task Acquire_AnInteractiveCaller_UsesTheSharedPermitsFirstAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 3, acquireTimeoutMilliseconds: 50, interactiveReserve: 1);
    WorkCoordinatorGate.Releaser interactive;
    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      interactive = await gate.AcquireAsync(CancellationToken.None);
    }
    using var bulk = await gate.AcquireAsync(CancellationToken.None);

    using var refused = await gate.AcquireAsync(CancellationToken.None);

    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(2)
      .Because("the interactive caller took a shared permit while one was free, so the reserve is still whole and the bulk callers see the same two shared permits");
    interactive.Dispose();
  }

  [Test]
  public async Task Reserve_DefaultsToOneTenthOfThePermits_AndNeverTheWholeGateAsync() {
    using var ten = new WorkCoordinatorGate(maxConcurrent: 10);
    using var twenty = new WorkCoordinatorGate(maxConcurrent: 25);
    using var nine = new WorkCoordinatorGate(maxConcurrent: 9);
    using var two = new WorkCoordinatorGate(maxConcurrent: 2);
    using var one = new WorkCoordinatorGate(maxConcurrent: 1);

    await Assert.That(ten.InteractiveReserve).IsEqualTo(1);
    await Assert.That(twenty.InteractiveReserve).IsEqualTo(2).Because("one tenth, rounded down");
    await Assert.That(nine.InteractiveReserve).IsEqualTo(0)
      .Because("a gate under ten permits cannot spare a tenth; the operator can still configure one");
    await Assert.That(two.InteractiveReserve).IsEqualTo(0)
      .Because("a reserve that is half of a two-permit gate is a haircut, not a share (the holder diagnostics suite runs on such a gate)");
    await Assert.That(one.InteractiveReserve).IsEqualTo(0)
      .Because("a one-permit gate cannot reserve without starving every other caller; the reserve never takes the last shared permit");
  }
}
