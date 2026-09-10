using Microsoft.Extensions.Logging;
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

  /// <summary>Starts an interactive acquire without awaiting it, so the test can free a permit afterwards.</summary>
  private static Task<WorkCoordinatorGate.Releaser> _startInteractive(WorkCoordinatorGate gate) {
    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      return gate.AcquireAsync(CancellationToken.None, caller: "interactive").AsTask();
    }
  }

  private static async Task<(WorkCoordinatorGate.Releaser BulkA, WorkCoordinatorGate.Releaser BulkB, WorkCoordinatorGate.Releaser Reserved)> _exhaustAsync(WorkCoordinatorGate gate) {
    var bulkA = await gate.AcquireAsync(CancellationToken.None, caller: "bulk");
    var bulkB = await gate.AcquireAsync(CancellationToken.None, caller: "bulk");
    var reserved = await _startInteractive(gate);
    return (bulkA, bulkB, reserved);
  }

  [Test]
  public async Task Acquire_AnInteractiveCallerWaits_AndTakesTheSharedPermitThatFreesFirstAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 3, acquireTimeoutMilliseconds: 5000, interactiveReserve: 1);
    var (bulkA, bulkB, reserved) = await _exhaustAsync(gate);
    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(3);

    var waiting = _startInteractive(gate);
    await Assert.That(waiting.IsCompleted).IsFalse()
      .Because("every permit is held, so the interactive caller waits on the shared permits and the reserve at once");

    bulkA.Dispose();
    using var taken = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(3)
      .Because("the waiter took the shared permit the moment it freed");

    reserved.Dispose();
    using var reservedAgain = await _startInteractive(gate).WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(3)
      .Because("the losing reserve wait handed its permit straight back, so the reserve is whole for the next interactive caller");
    bulkB.Dispose();
  }

  [Test]
  public async Task Acquire_AnInteractiveCallerWaits_AndTakesTheReserveWhenItFreesFirstAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 3, acquireTimeoutMilliseconds: 5000, interactiveReserve: 1);
    var (bulkA, bulkB, reserved) = await _exhaustAsync(gate);
    var waiting = _startInteractive(gate);
    await Assert.That(waiting.IsCompleted).IsFalse();

    reserved.Dispose();
    using var taken = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(3)
      .Because("the reserve freed first and the waiter took it");

    bulkA.Dispose();
    using var bulkC = await gate.AcquireAsync(CancellationToken.None, caller: "bulk");
    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(3)
      .Because("the losing shared wait handed its permit back to the shared pool, where a bulk caller can take it");
    bulkB.Dispose();
  }

  [Test]
  public async Task Acquire_WithoutADeadline_AnInteractiveCallerWaitsUntilAPermitFreesAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 3, acquireTimeoutMilliseconds: 0, interactiveReserve: 1);
    var (bulkA, bulkB, reserved) = await _exhaustAsync(gate);
    var waiting = _startInteractive(gate);
    await Assert.That(waiting.IsCompleted).IsFalse();

    bulkA.Dispose();
    using var taken = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(3)
      .Because("with no deadline the waits are open-ended and the first permit to free is taken");

    reserved.Dispose();
    using var reservedAgain = await _startInteractive(gate).WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(3);
    bulkB.Dispose();
  }

  [Test]
  public async Task Acquire_AnInteractiveCallerPastTheDeadline_ProceedsWithoutASlot_AndTheWarningNamesTheHoldersAsync() {
    var logger = new _capturingLogger();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 3, acquireTimeoutMilliseconds: 100, logger: logger, interactiveReserve: 1);
    var (bulkA, bulkB, reserved) = await _exhaustAsync(gate);

    using var deadlined = await _startInteractive(gate);

    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(3)
      .Because("the deadlined caller proceeds without holding a slot rather than hanging");
    await Assert.That(logger.Warnings.Count).IsEqualTo(1);
    await Assert.That(logger.Warnings[0]).Contains("bulk x2")
      .Because("the warning groups the holders by caller so the stuck method is visible");
    bulkA.Dispose();
    bulkB.Dispose();
    reserved.Dispose();
  }

  [Test]
  public async Task Acquire_AnInteractiveCallerPastTheDeadline_WithoutALogger_StillProceedsAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 3, acquireTimeoutMilliseconds: 100, interactiveReserve: 1);
    var (bulkA, bulkB, reserved) = await _exhaustAsync(gate);

    using var deadlined = await _startInteractive(gate);

    await Assert.That(gate.SnapshotHolders().Count).IsEqualTo(3);
    bulkA.Dispose();
    bulkB.Dispose();
    reserved.Dispose();
  }

  private sealed class _capturingLogger : ILogger<WorkCoordinatorGate> {
    public List<string> Warnings { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        lock (Warnings) {
          Warnings.Add(formatter(state, exception));
        }
      }
    }
  }
}
