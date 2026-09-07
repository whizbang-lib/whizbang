using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for <see cref="PerspectiveMigrationWorker.ExecuteAsync"/> paths not exercised by
/// <see cref="PerspectiveMigrationWorkerTests"/>: the missing-callbacks early return, a schema-ready
/// wait that gets canceled by shutdown, the best-effort status update swallowing its own exception
/// after a rebuild already failed, and the outer catch around the pending-rebuild fetch itself.
/// </summary>
/// <remarks>
/// This is a one-shot startup sweep — <c>ExecuteAsync</c> runs once and returns, it does not loop.
/// Every catch here exists so a single bad startup condition (schema never ready, DB unavailable,
/// double failure updating status) degrades to "this pass did nothing" instead of tearing down the
/// host or crashing the background service infrastructure during startup.
/// </remarks>
public class PerspectiveMigrationWorkerCoverageTests {

  // If a migrated-in-background perspective is silently never rebuilt because the hosting
  // infrastructure never wired GetPendingRebuilds/UpdateMigrationStatus, downstream reads keep
  // observing the pre-migration shape forever with no error anywhere — this early return exists
  // so an unwired worker is inert rather than half-functional.
  // The worker is registered unconditionally but its two callbacks are wired by the hosting
  // infrastructure, so a host that registers the service without that wiring is a real
  // configuration, not a hypothetical. It must return quietly: dereferencing a null callback here
  // would fault ExecuteAsync and take the host down during startup, and the resulting
  // NullReferenceException names neither the worker nor the setting that was missed.
  [Test]
  public async Task ExecuteAsync_CallbacksNotWired_ReturnsWithoutFaultingAsync() {
    var worker = new PerspectiveMigrationWorker(
      rebuilder: new ThrowingRebuilder(),
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady());

    await worker.StartAsync(CancellationToken.None);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.IsCompleted).IsTrue()
      .Because("StopAsync awaits ExecuteAsync, so the task must have settled by now");
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("an unwired worker must be inert, not a startup crash that names neither the "
             + "worker nor the missing wiring");
  }

  [Test]
  public async Task ExecuteAsync_BothCallbacksNull_ReturnsWithoutTouchingRebuilderAsync() {
    var rebuilder = new FakeRebuilder();
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady());

    await worker.StartAsync(CancellationToken.None);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0)
      .Because("with neither callback wired, ExecuteAsync must return immediately rather than crash on a null callback invocation.");
  }

  // If host shutdown cancels the schema-ready wait, the worker must abandon the run cleanly
  // instead of either hanging StopAsync or barreling ahead to query tables that were never
  // confirmed ready — either failure mode turns a clean shutdown into a startup-ordering bug.
  [Test]
  public async Task ExecuteAsync_SchemaReadyWaitCanceled_ReturnsWithoutProcessingAsync() {
    var rebuilder = new FakeRebuilder();
    var neverReadyGate = new SchemaReadyGate(); // MarkReady() is never called
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: neverReadyGate) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("ShouldNotRun", "perspective:ShouldNotRun")
      ]),
      UpdateMigrationStatus = (_, _, _, _) => Task.CompletedTask
    };

    // StartAsync returns once ExecuteAsync is parked on the never-completing schema wait;
    // StopAsync cancels that wait and awaits ExecuteAsync's real completion (BackgroundService's
    // own await, not a bare "StartAsync returned" check) — a deterministic signal, not a race.
    await worker.StartAsync(CancellationToken.None);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0)
      .Because("a canceled schema-ready wait must abandon the run before touching any rebuild — never process pending migrations against a schema that was never confirmed ready.");
  }

  // Two failures in a row (rebuild throws, then the best-effort status update ALSO throws) must
  // not propagate out of ExecuteAsync. If it did, the background service's unhandled-exception
  // path would tear down this one-shot startup sweep mid-loop, abandoning every remaining pending
  // migration in the batch with nothing but a torn-down host to show for it.
  [Test]
  public async Task ExecuteAsync_RebuilderThrowsAndStatusUpdateAlsoThrows_SwallowsBothAsync() {
    var statusUpdateAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var rebuilder = new ThrowingRebuilder();
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
        new PendingMigrationRebuild("CrashPerspective", "perspective:CrashPerspective")
      ]),
      UpdateMigrationStatus = (_, _, _, _) => {
        // The failing callback is itself the signal that the loop body ran. Without waiting on it,
        // StopAsync cancels the stopping token before the pending-migration loop reaches its first
        // iteration, the loop breaks at its own cancellation check, and the test passes while the
        // status-update path it names is never executed.
        statusUpdateAttempted.TrySetResult();
        throw new InvalidOperationException("status update also failed");
      }
    };

    await worker.StartAsync(CancellationToken.None);
    await statusUpdateAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await worker.StopAsync(CancellationToken.None);

    // Assert the worker's own task, not that this line was reached: StopAsync awaits ExecuteAsync,
    // so by here the task has settled and its state is the actual evidence that the best-effort
    // catch swallowed BOTH the rebuild failure and the status-update failure that follows it. A
    // fault escaping would take down the host during startup.
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsTrue()
      .Because("StopAsync awaits ExecuteAsync, so the task must have settled by now");
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("neither the rebuild failure nor the status-update failure after it may escape "
             + "ExecuteAsync's best-effort catch and fault the host's startup");
  }

  // If enumerating pending rebuilds itself throws (a transient DB error at startup), the outer
  // catch must keep that from crashing the host during startup — this is a one-shot sweep, so
  // the only alternative to catching here is letting a single bad startup query take down
  // whatever else is starting alongside it.
  [Test]
  public async Task ExecuteAsync_GetPendingRebuildsThrows_SwallowsTheOuterExceptionAsync() {
    var enumerationAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var rebuilder = new FakeRebuilder();
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: SchemaReadyGate.AlreadyReady()) {
      GetPendingRebuilds = _ => {
        // Same reasoning as the status-update test: the throwing callback is the signal that the
        // sweep actually started. Stopping without waiting for it cancels the stopping token
        // first, so ExecuteAsync exits before reaching this call and the outer catch this test
        // names never runs.
        enumerationAttempted.TrySetResult();
        throw new InvalidOperationException("database unavailable");
      },
      UpdateMigrationStatus = (_, _, _, _) => Task.CompletedTask
    };

    await worker.StartAsync(CancellationToken.None);
    await enumerationAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(rebuilder.RebuildCount).IsEqualTo(0)
      .Because("the fetch failure must be swallowed before ever reaching the rebuild loop, and must not propagate past ExecuteAsync.");
  }

  // ===== fakes =====

  private sealed class FakeRebuilder : IPerspectiveRebuilder {
    public int RebuildCount { get; private set; }

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) {
      RebuildCount++;
      return Task.FromResult(new RebuildResult(perspectiveName, 1, 1, TimeSpan.Zero, true, null));
    }

    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) =>
      RebuildBlueGreenAsync(perspectiveName, ct);

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) =>
      RebuildBlueGreenAsync(perspectiveName, ct);

    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<RebuildStatus?>(null);
  }

  private sealed class ThrowingRebuilder : IPerspectiveRebuilder {
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) =>
      throw new InvalidOperationException("rebuild boom");

    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) =>
      throw new InvalidOperationException("rebuild boom");

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) =>
      throw new InvalidOperationException("rebuild boom");

    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<RebuildStatus?>(null);
  }
}
