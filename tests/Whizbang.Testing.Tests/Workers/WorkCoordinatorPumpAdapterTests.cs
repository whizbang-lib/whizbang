using Whizbang.Core.Messaging;
using Whizbang.Testing.Tests.TestSupport;
using Whizbang.Testing.Workers;

namespace Whizbang.Testing.Tests.Workers;

/// <summary>
/// Shutdown behavior of <see cref="WorkCoordinatorPumpAdapter.RunPumpAsync"/>. Callers fire the
/// pump and forget it, so a cancellation arriving anywhere in a cycle has to end the loop quietly
/// rather than spin on or fault an unobserved task.
/// </summary>
public class WorkCoordinatorPumpAdapterTests {

  private static PerspectiveWork _work() => new() {
    WorkId = Guid.NewGuid(),
    StreamId = Guid.NewGuid(),
    PerspectiveName = "probe-perspective"
  };

  [Test]
  public async Task RunPumpAsync_WhenTheClaimIsCanceled_StopsAfterThatClaimAsync() {
    // A store that aborts the claim mid-flight must end the pump, not start another cycle: the
    // loop's own token is still live here, so anything other than breaking out spins forever
    // against a coordinator that is going away.
    var coordinator = new ScriptedWorkCoordinator(_ =>
      throw new OperationCanceledException("claim aborted"));
    var harness = new PerspectiveWorkerTestHarness();
    using var cts = new CancellationTokenSource();

    await WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);

    await Assert.That(coordinator.Claims).IsEqualTo(1);
    await Assert.That(harness.ChannelWriter.Reader.TryRead(out _)).IsFalse();
    await Assert.That(cts.IsCancellationRequested).IsFalse();
  }

  [Test]
  public async Task RunPumpAsync_WhenCancellationArrivesWhileHandingOffWork_EndsQuietlyAsync() {
    // Shutdown between the claim and the hand-off is the realistic race: the batch is already in
    // hand when the token trips, and the channel write is what observes it. The pump is launched
    // fire-and-forget, so that cancellation must not escape as a faulted, unobserved task.
    using var cts = new CancellationTokenSource();
    var coordinator = new ScriptedWorkCoordinator(_ => {
      cts.Cancel();
      return new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [_work()]
      };
    });
    var harness = new PerspectiveWorkerTestHarness();

    await WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);

    await Assert.That(coordinator.Claims).IsEqualTo(1);
    await Assert.That(harness.ChannelWriter.Reader.TryRead(out _)).IsFalse()
      .Because("work claimed after shutdown began must not reach the worker's channel");
  }

  [Test]
  public async Task RunPumpAsync_ForwardsClaimedWorkAndDrainStreamsToTheHarnessChannelsAsync() {
    // The pump's whole job: what the coordinator hands back on a claim shows up on the channels
    // the worker reads, per-event work and drain stream ids alike.
    using var cts = new CancellationTokenSource();
    var work = _work();
    var streamId = Guid.NewGuid();
    var coordinator = new ScriptedWorkCoordinator(attempt => {
      if (attempt > 1) {
        throw new OperationCanceledException("second claim aborted");
      }
      return new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [work],
        PerspectiveStreamIds = [streamId]
      };
    });
    var harness = new PerspectiveWorkerTestHarness();

    await WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token, cycleDelayMs: 0);

    await Assert.That(harness.ChannelWriter.Reader.TryRead(out var forwarded)).IsTrue();
    await Assert.That(forwarded!.WorkId).IsEqualTo(work.WorkId);
    await Assert.That(harness.DrainChannel.Reader.TryRead(out var forwardedStream)).IsTrue();
    await Assert.That(forwardedStream).IsEqualTo(streamId);
  }
}
