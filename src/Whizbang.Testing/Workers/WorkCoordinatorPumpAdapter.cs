using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Testing.Workers;

/// <summary>
/// Bridges an <see cref="IWorkCoordinator"/> fake (returns work via <see cref="IWorkCoordinator.ClaimWorkAsync"/>)
/// to a channel-mode <see cref="PerspectiveWorkerTestHarness"/>. Tests that have an existing fake
/// coordinator returning PerspectiveWork / PerspectiveStreamIds on each claim can run this pump alongside
/// their PerspectiveWorker — the worker reads from the harness channel exactly as if the production
/// <c>ClaimWorker</c> had claimed the work and routed it.
/// </summary>
/// <remarks>
/// Loops every <c>cycleDelayMs</c> milliseconds (20 by default) until canceled. Each cycle: invoke
/// <see cref="IWorkCoordinator.ClaimWorkAsync"/> with a stub request (the same call the production
/// ClaimWorker makes), then write every PerspectiveWork item and drain stream-id into the harness channels.
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
public static class WorkCoordinatorPumpAdapter {
  /// <summary>
  /// Run the pump loop. Stops on cancellation. Caller typically fires it as fire-and-forget:
  /// <c>_ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);</c>
  /// </summary>
  public static async Task RunPumpAsync(
    IWorkCoordinator coordinator,
    PerspectiveWorkerTestHarness harness,
    CancellationToken cancellationToken,
    int cycleDelayMs = 20) {
    ArgumentNullException.ThrowIfNull(coordinator);
    ArgumentNullException.ThrowIfNull(harness);

    var stubRequest = new ClaimWorkRequest(
      InstanceId: Guid.NewGuid(),
      ServiceName: "test-pump",
      HostName: "test-host",
      ProcessId: 0);

    try {
      while (!cancellationToken.IsCancellationRequested) {
        WorkBatch batch;
        try {
          batch = await coordinator.ClaimWorkAsync(stubRequest, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          break;
        }
        foreach (var work in batch.PerspectiveWork) {
          await harness.EnqueueWorkAsync(work, cancellationToken).ConfigureAwait(false);
        }
        foreach (var streamId in batch.PerspectiveStreamIds) {
          await harness.EnqueueDrainStreamAsync(streamId, cancellationToken).ConfigureAwait(false);
        }
        try {
          await Task.Delay(cycleDelayMs, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          break;
        }
      }
    } catch (OperationCanceledException) {
      // expected on shutdown
    }
  }
}
