using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Testing.Workers;

/// <summary>
/// Bridges a legacy IWorkCoordinator fake (returns work via ProcessWorkBatchAsync) to a
/// channel-mode <see cref="PerspectiveWorkerTestHarness"/>. Tests that have an existing fake
/// coordinator returning PerspectiveWork on each call can run this pump alongside their
/// PerspectiveWorker — the worker reads from the harness channel as if ClaimWorker had
/// claimed the work in production.
/// </summary>
/// <remarks>
/// Loops every <c>cycleDelayMs</c> milliseconds (20 by default) until cancelled.
/// Each cycle: invoke ProcessWorkBatchAsync on the coordinator with a stub request, write
/// every PerspectiveWork item into the harness channel.
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

    var instanceId = Guid.NewGuid();
    var claimRequest = new ClaimWorkRequest(
      InstanceId: instanceId,
      ServiceName: "test-pump",
      HostName: "test-host",
      ProcessId: 0,
      MaxStreams: 1000,
      PartitionCount: 100,
      LeaseSeconds: 300);

    try {
      while (!cancellationToken.IsCancellationRequested) {
        WorkBatch batch;
        try {
          batch = await coordinator.ClaimWorkAsync(claimRequest, cancellationToken).ConfigureAwait(false);
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
