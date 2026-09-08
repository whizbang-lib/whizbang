using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for PerspectiveMigrationWorker — verifies background rebuild processing.
/// Uses TaskCompletionSource-based signaling for deterministic synchronization
/// instead of Task.Delay, following the same pattern as PerspectiveWorkerCoverageTests.
/// </summary>
public class PerspectiveMigrationWorkerTests {

  /// <summary>
  /// Upper bound on every wait for a worker-emitted signal. Generous on purpose: it is a deadlock
  /// guard, not a timing assumption — the signals themselves are what the tests synchronize on.
  /// </summary>
  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

  [Test]
  public async Task ExecuteAsync_WithNoPendingRebuilds_CompletesQuicklyAsync() {
    // Arrange
    var rebuilder = new FakeRebuilder();
    // The fetch callback is the worker's own "the body ran" signal. StartAsync only SCHEDULES
    // ExecuteAsync onto the thread pool, so stopping without waiting for this would answer the
    // assertion below with a body that was never entered — "returned early on an empty list" and
    // "never started at all" would look identical.
    var fetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => {
        fetched.TrySetResult();
        return Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([]);
      },
      UpdateMigrationStatus = (_, _, _, _) => Task.CompletedTask
    };

    // Act
    await worker.StartAsync(CancellationToken.None);
    await fetched.Task.WaitAsync(_wait);
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0)
      .Because("an empty pending list must return before the rebuild loop — the sweep queried and "
             + "found nothing, which is different from the sweep never having queried");
  }

  [Test]
  public async Task ExecuteAsync_WithPendingRebuild_CallsRebuilderAsync() {
    // Arrange
    var rebuilder = new FakeRebuilder();
    var statusUpdates = new List<(string Key, int Status, string Desc)>();
    var workDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("OrderPerspective", "perspective:OrderPerspective")
      ]),
      UpdateMigrationStatus = (key, status, desc, _) => {
        statusUpdates.Add((key, status, desc));
        workDone.TrySetResult();
        return Task.CompletedTask;
      }
    };

    // Act — wait for status update signal instead of Task.Delay
    await worker.StartAsync(CancellationToken.None);
    await workDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(1);
    await Assert.That(rebuilder.LastPerspectiveName).IsEqualTo("OrderPerspective");
    await Assert.That(statusUpdates).Count().IsEqualTo(1);
    await Assert.That(statusUpdates[0].Status).IsEqualTo(2); // Updated
  }

  [Test]
  public async Task ExecuteAsync_WithFailedRebuild_RecordsFailureStatusAsync() {
    // Arrange
    var rebuilder = new FakeRebuilder { ShouldFail = true };
    var statusUpdates = new List<(string Key, int Status, string Desc)>();
    var workDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("FailingPerspective", "perspective:FailingPerspective")
      ]),
      UpdateMigrationStatus = (key, status, desc, _) => {
        statusUpdates.Add((key, status, desc));
        workDone.TrySetResult();
        return Task.CompletedTask;
      }
    };

    // Act — wait for status update signal
    await worker.StartAsync(CancellationToken.None);
    await workDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(statusUpdates).Count().IsEqualTo(1);
    await Assert.That(statusUpdates[0].Status).IsEqualTo(-1); // Failed
  }

  [Test]
  public async Task ExecuteAsync_WithNoCallbacks_CompletesGracefullyAsync() {
    // Arrange — callbacks not set, ExecuteAsync returns early
    var rebuilder = new FakeRebuilder();
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady());

    // Act — the unwired path touches nothing, so it emits no signal of its own. The settled state
    // of the worker's own task is the signal instead: awaiting it BEFORE anything cancels means it
    // can only finish by the body running to its early return. A body the thread pool never
    // dequeued would settle Canceled, which is exactly what this distinguishes.
    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_wait);
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("an unwired worker must RETURN, not fault and not be skipped — RanToCompletion is "
             + "the only settled state that proves the early return itself executed");
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0)
      .Because("with no callbacks there is nothing to enumerate, so no rebuild may be attempted");
  }

  [Test]
  public async Task ExecuteAsync_WithCancellation_StopsProcessingRemainingRebuildsAsync() {
    // Arrange
    var rebuilder = new FakeRebuilder();
    var cts = new CancellationTokenSource();
    var statusUpdates = new List<(string Key, int Status, string Desc)>();
    var workDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("First", "perspective:First"),
        new PendingMigrationRebuild("Second", "perspective:Second"),
        new PendingMigrationRebuild("Third", "perspective:Third")
      ]),
      UpdateMigrationStatus = (key, status, desc, _) => {
        statusUpdates.Add((key, status, desc));
        // Cancel after first rebuild completes so the foreach break triggers
        cts.Cancel();
        workDone.TrySetResult();
        return Task.CompletedTask;
      }
    };

    // Act — wait for the first status update (which also cancels)
    await worker.StartAsync(cts.Token);
    await workDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await worker.StopAsync(CancellationToken.None);

    // Assert — only the first rebuild should have been processed before cancellation kicked in
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(1);
    await Assert.That(statusUpdates).Count().IsEqualTo(1);
  }

  [Test]
  public async Task ExecuteAsync_WhenRebuilderThrows_CatchesExceptionAndUpdatesStatusAsync() {
    // Arrange — covers lines 63-67 (inner catch block with status update)
    var rebuilder = new ThrowingRebuilder();
    var statusUpdates = new List<(string Key, int Status, string Desc)>();
    var workDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("CrashPerspective", "perspective:CrashPerspective")
      ]),
      UpdateMigrationStatus = (key, status, desc, _) => {
        statusUpdates.Add((key, status, desc));
        workDone.TrySetResult();
        return Task.CompletedTask;
      }
    };

    // Act — wait for status update signal
    await worker.StartAsync(CancellationToken.None);
    await workDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await worker.StopAsync(CancellationToken.None);

    // Assert — exception caught, status updated to Failed
    await Assert.That(statusUpdates).Count().IsEqualTo(1);
    await Assert.That(statusUpdates[0].Status).IsEqualTo(-1);
    await Assert.That(statusUpdates[0].Desc).Contains("Boom");
  }

  [Test]
  public async Task ExecuteAsync_WhenRebuilderThrowsAndStatusUpdateFails_SwallowsBothExceptionsAsync() {
    // Arrange — the best-effort status update failing on top of a failed rebuild.
    var rebuilder = new ThrowingRebuilder();
    // The throwing callback is itself the proof that the rebuild loop reached the recovery path.
    // Without waiting on it, StopAsync cancels the stopping token first, the loop breaks at its own
    // cancellation check, and the double-failure path this test names is never executed.
    var statusUpdateAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("CrashPerspective", "perspective:CrashPerspective")
      ]),
      UpdateMigrationStatus = (_, _, _, _) => {
        statusUpdateAttempted.TrySetResult();
        throw new InvalidOperationException("Status update also failed");
      }
    };

    // Act
    await worker.StartAsync(CancellationToken.None);
    await statusUpdateAttempted.Task.WaitAsync(_wait);
    await worker.StopAsync(CancellationToken.None);

    // Assert — the worker's own task is the evidence: two failures in a row were swallowed rather
    // than escaping ExecuteAsync, where they would fault the hosted service during host startup.
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsTrue()
      .Because("StopAsync awaits ExecuteAsync, so the task must have settled by now");
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("neither the rebuild failure nor the status-update failure that follows it may "
             + "escape the best-effort catch and take the host down at startup");
  }

  [Test]
  public async Task ExecuteAsync_WhenGetPendingRebuildsThrows_CatchesOuterExceptionAsync() {
    // Arrange — a transient database failure enumerating pending rebuilds at startup.
    var rebuilder = new FakeRebuilder();
    // The throwing fetch is the signal that the sweep actually started. Stopping first would cancel
    // the stopping token before ExecuteAsync ever reached this call, so the outer catch this test
    // names would go unexercised while the test still passed.
    var enumerationAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => {
        enumerationAttempted.TrySetResult();
        throw new InvalidOperationException("Database unavailable");
      },
      UpdateMigrationStatus = (_, _, _, _) => Task.CompletedTask
    };

    // Act
    await worker.StartAsync(CancellationToken.None);
    await enumerationAttempted.Task.WaitAsync(_wait);
    await worker.StopAsync(CancellationToken.None);

    // Assert — worker swallowed the exception instead of faulting the host during startup
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("one bad startup query must degrade to 'this pass did nothing', not tear down "
             + "whatever else is starting alongside it");
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0)
      .Because("the fetch failed, so the rebuild loop must never have been entered");
  }

  [Test]
  public async Task ExecuteAsync_WithOnlyGetPendingRebuildsNull_ReturnsEarlyAsync() {
    // Arrange — covers the OR condition: GetPendingRebuilds is null but UpdateMigrationStatus is set
    var rebuilder = new FakeRebuilder();
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      UpdateMigrationStatus = (_, _, _, _) => Task.CompletedTask
    };

    // Act — awaiting the worker's task before anything cancels: the half-wired path emits no
    // signal of its own, so only a task that RAN to its early return can settle here.
    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_wait);
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("half-wired is as inert as unwired — the guard must return rather than dereference "
             + "the null fetch callback and fault startup");
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0)
      .Because("with no way to enumerate pending work, nothing may be rebuilt");
  }

  [Test]
  public async Task ExecuteAsync_WithOnlyUpdateMigrationStatusNull_ReturnsEarlyAsync() {
    // Arrange — covers the OR condition: UpdateMigrationStatus is null but GetPendingRebuilds is set
    var rebuilder = new FakeRebuilder();
    // The fetch callback must NOT be reached: the guard runs before it. So it doubles as the
    // negative assertion below rather than as a wait signal.
    var fetchCalled = false;
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => {
        Volatile.Write(ref fetchCalled, true);
        return Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([]);
      }
    };

    // Act — awaiting the worker's task before anything cancels, so only a body that RAN to its
    // early return can settle it.
    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_wait);
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the guard must return rather than run a sweep it has no way to record the result of");
    await Assert.That(Volatile.Read(ref fetchCalled)).IsFalse()
      .Because("a missing status callback must stop the sweep BEFORE the query — rebuilding with "
             + "no way to mark the migration Updated would repeat the rebuild on every restart");
    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0);
  }

  // --- Test Doubles ---

  private sealed class ThrowingRebuilder : IPerspectiveRebuilder {
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct) =>
        throw new InvalidOperationException("Boom");

    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct) =>
        throw new InvalidOperationException("Boom");

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct) =>
        throw new InvalidOperationException("Boom");

    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct) =>
        Task.FromResult<RebuildStatus?>(null);
  }

  private sealed class FakeRebuilder : IPerspectiveRebuilder {
    public int RebuildCount { get; private set; }
    public string? LastPerspectiveName { get; private set; }
    public bool ShouldFail { get; init; }

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct) {
      RebuildCount++;
      LastPerspectiveName = perspectiveName;
      return Task.FromResult(ShouldFail
        ? new RebuildResult(perspectiveName, 0, 0, TimeSpan.Zero, false, "Simulated failure")
        : new RebuildResult(perspectiveName, 5, 10, TimeSpan.FromSeconds(1), true, null));
    }

    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct) =>
        RebuildBlueGreenAsync(perspectiveName, ct);

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct) =>
        RebuildBlueGreenAsync(perspectiveName, ct);

    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct) =>
        Task.FromResult<RebuildStatus?>(null);
  }
}
