using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The contract the channel-consumer loop's wake keeps: a wait that ends in cancellation stops the
/// loop, a wait that completes lets it take the next batch, and a wait that fails for any other
/// reason is not the loop's to swallow.
/// </summary>
/// <remarks>
/// Asserted on <see cref="PerspectiveWorker.AwaitConsumerWakeAsync"/> directly rather than through
/// a running worker because the loop composes its wake with <see cref="Task.WhenAny(Task[])"/>,
/// which reports a canceled source as its result instead of faulting — so no worker, however it is
/// shut down, can drive the cancellation arm. The guard is still what stands between a wait that
/// does end canceled and a consumer task torn down with nobody watching, which is why it is pinned
/// here instead of removed. The last test below pins that <c>WhenAny</c> property itself, because
/// it is the reason this seam exists.
/// </remarks>
public partial class PerspectiveWorkerDeepPathChannelTests {

  [Test]
  public async Task AwaitConsumerWakeAsync_WaitEndsCanceled_StopsTheLoopAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var keepRunning = await PerspectiveWorker.AwaitConsumerWakeAsync(Task.FromCanceled(cts.Token));

    await Assert.That(keepRunning).IsFalse()
      .Because("A wake that ends in cancellation is shutdown: the loop stops rather than letting the exception out");
  }

  [Test]
  public async Task AwaitConsumerWakeAsync_WaitCompletes_LetsTheLoopTakeTheNextBatchAsync() {
    var keepRunning = await PerspectiveWorker.AwaitConsumerWakeAsync(Task.CompletedTask);

    await Assert.That(keepRunning).IsTrue()
      .Because("A wake that completed is work (or a tick) waiting — the loop must go on to read it");
  }

  [Test]
  public async Task AwaitConsumerWakeAsync_WaitFailsForAnotherReason_IsNotSwallowedAsync() {
    var defect = new InvalidOperationException("the wake itself is broken");

    await Assert.That(async () => await PerspectiveWorker.AwaitConsumerWakeAsync(Task.FromException(defect)))
      .ThrowsExactly<InvalidOperationException>()
      .Because("Only cancellation is a shutdown signal; anything else is a defect the loop must not hide");
  }

  [Test]
  public async Task AwaitConsumerWakeAsync_ComposedWithWhenAny_NeverSeesCancellationAsync() {
    // The invariant the loop relies on: WhenAny reports a canceled source as its RESULT, so the
    // composed wake completes normally and the cancellation arm above cannot be driven from the
    // loop. If this ever stops holding, the loop's wake starts throwing and this test says so.
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    var canceled = Task.FromCanceled(cts.Token);
    var neverCompletes = new TaskCompletionSource().Task;

    var keepRunning = await PerspectiveWorker.AwaitConsumerWakeAsync(Task.WhenAny(canceled, neverCompletes));

    await Assert.That(keepRunning).IsTrue()
      .Because("WhenAny completes with the canceled task as its result rather than faulting");
  }
}
