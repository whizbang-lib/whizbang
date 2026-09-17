using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Execution;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Execution.Tests;

/// <summary>
/// A drain that is already waiting on the worker when a stop cancels it completes rather than
/// faulting its caller.
/// </summary>
/// <remarks>
/// <para>
/// <c>DrainAsync</c> completes the channel writer and then awaits the worker task. Those two steps
/// straddle a lock: everything up to the await is synchronous, so a caller that does not await the
/// returned task has the drain suspended on the worker with the channel already completed. If a
/// stop lands in that window it cancels the worker token, the worker's next
/// <c>WaitToReadAsync</c> observes the canceled token ahead of "done writing", and the worker task
/// ends canceled. The drain's await then throws.
/// </para>
/// <para>
/// The catch that swallows it is commented "Should never happen - kept as safety net", and the test
/// the member's <c>tests</c> tag points at deliberately cannot reach it: it stops first, so the
/// drain sees <c>State.Stopped</c> and returns before awaiting anything. That left the real
/// interleaving untested, which is the one a host actually produces when a shutdown overlaps a
/// drain.
/// </para>
/// <para>
/// The interleaving here is deterministic, not timed. Each step waits on a signal or on the
/// synchronous part of a call; nothing sleeps and nothing polls.
/// </para>
/// </remarks>
[Category("Execution")]
public class SerialExecutorDrainAfterStopTests {

  [Test]
  [Timeout(30000)]
  public async Task DrainAsync_WorkerCanceledWhileTheDrainAwaitsIt_CompletesAndRecordsItAsync(
      CancellationToken cancellationToken) {
    // The recorder writes to the activity, which only exists while something is listening. An
    // ActivityListener is process-global and every other test that drains an executor produces an
    // activity with this same operation name, so the filter is on THIS test's trace: a parent
    // activity is started here, the drain below is created on this context and inherits the trace,
    // and anything from a sibling test is ignored. Without that the assertions below can be handed
    // another test's clean drain and fail on it.
    var captured = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
    ActivityTraceId expectedTrace = default;
    using var probeSource = new ActivitySource("SerialExecutorDrainAfterStopProbe");
    using var listener = new ActivityListener {
      ShouldListenTo = source =>
        source.Name is "Whizbang.Execution" or "SerialExecutorDrainAfterStopProbe",
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
      ActivityStopped = activity => {
        if (activity.OperationName == "SerialExecutor.DrainAsync"
            && activity.TraceId == expectedTrace) {
          captured.TrySetResult(activity);
        }
      }
    };
    ActivitySource.AddActivityListener(listener);
    using var probe = probeSource.StartActivity("drain-after-stop");
    expectedTrace = probe!.TraceId;

    var executor = new SerialExecutor();
    await executor.StartAsync(cancellationToken);

    // Park the worker inside a handler so the channel is not empty when the drain completes it.
    // Without this the completed writer resolves the worker's pending read to "no more items", the
    // loop ends normally, and the drain never has a canceled worker to await.
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var inflight = executor.ExecuteAsync<int>(
      _envelope("drain-after-stop"),
      async (_, _) => {
        entered.SetResult();
        return await release.Task;
      },
      null!,   // execution strategies do not read the policy context
      cancellationToken).AsTask();
    await entered.Task;

    // Not awaited: the synchronous half runs now, so the state check passes and the writer is
    // completed, and the task is left suspended on the worker.
    var drain = executor.DrainAsync(cancellationToken);
    // Also not awaited: its synchronous half flips the state and marks the worker token canceled.
    var stop = executor.StopAsync(CancellationToken.None);

    release.SetResult(42);
    var result = await inflight;

    await drain;
    await stop;

    await Assert.That(result).IsEqualTo(42)
      .Because("work that was already running has to finish and reach its caller; a stop overlapping a "
        + "drain must not lose a result that was in flight");
    await Assert.That(drain.IsCompletedSuccessfully).IsTrue()
      .Because("the drain awaited a worker that a concurrent stop canceled, and it has to absorb that "
        + "rather than hand an OperationCanceledException to a caller who asked only to drain");

    var activity = await captured.Task;
    await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error)
      .Because("the cancellation is swallowed for the caller but must stay visible to an operator, or a "
        + "safety net that fires often looks exactly like one that never fires");
    await Assert.That(activity.GetTagItem("defensive.code")).IsEqualTo(true)
      .Because("the tag is what separates a defensive path from an ordinary error in a trace");

    await executor.DisposeAsync();
  }

  /// <summary>A locally dispatched envelope, which is all the executor reads of it.</summary>
  private static MessageEnvelope<DrainProbeMessage> _envelope(string payload) =>
    new MessageEnvelope<DrainProbeMessage> {
      MessageId = MessageId.New(),
      Payload = new DrainProbeMessage(payload),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  private sealed record DrainProbeMessage(string Payload) : IMessage;
}
