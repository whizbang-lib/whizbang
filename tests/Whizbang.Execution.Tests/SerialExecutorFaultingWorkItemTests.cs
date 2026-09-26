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
/// A work item whose execute delegate faults is recorded on the worker's activity and does not end
/// the worker loop.
/// </summary>
/// <remarks>
/// <para>
/// The public enqueue only ever assigns <c>_executeWithPooledStateAsync</c>, whose own try/catch/
/// finally covers its whole body, so nothing on the ordinary path can hand the worker a delegate
/// that throws out of the await. What the catch protects is the rest of that delegate — completing
/// the pooled source, resetting it, returning it to the pool — any of which throwing would end the
/// worker loop silently and hang every later caller.
/// </para>
/// <para>
/// <c>EnqueueFaultingForTestsAsync</c> is the second construction site that makes that reachable.
/// The test asserts both halves of the contract: the exception is recorded as defensive on the
/// worker's activity, and a later ordinary item still completes, which it can only do if the loop
/// survived the throw.
/// </para>
/// </remarks>
[Category("Execution")]
public class SerialExecutorFaultingWorkItemTests {

  [Test]
  [Timeout(30000)]
  public async Task ProcessWorkItems_DelegateThrows_RecordsDefensiveExceptionAndKeepsRunningAsync(
      CancellationToken cancellationToken) {
    // An ActivityListener is process-global and every other test that runs an executor produces an
    // activity with this same operation name, so the filter is on THIS test's trace: a parent
    // activity is started here, the worker below inherits the trace through the execution context
    // captured by StartAsync, and anything from a sibling test is ignored.
    var captured = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
    ActivityTraceId expectedTrace = default;
    using var probeSource = new ActivitySource("SerialExecutorFaultingWorkItemProbe");
    using var listener = new ActivityListener {
      ShouldListenTo = source =>
        source.Name is "Whizbang.Execution" or "SerialExecutorFaultingWorkItemProbe",
      Sample = (ref _) => ActivitySamplingResult.AllData,
      ActivityStopped = activity => {
        if (activity.OperationName == "SerialExecutor.ProcessWorkItems"
            && activity.TraceId == expectedTrace) {
          captured.TrySetResult(activity);
        }
      }
    };
    ActivitySource.AddActivityListener(listener);
    using var probe = probeSource.StartActivity("faulting-work-item");
    expectedTrace = probe!.TraceId;

    var executor = new SerialExecutor();
    await executor.StartAsync(cancellationToken);

    var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await executor.EnqueueFaultingForTestsAsync(
      _ => {
        ran.SetResult();
        throw new InvalidOperationException("pooled source teardown blew up");
      },
      cancellationToken);

    // The delegate signals before it throws, so the throw has already happened when this returns.
    await ran.Task;

    // The loop survived only if this ordinary item is read, run and completed.
    var afterTheThrow = await executor.ExecuteAsync<int>(
      _envelope("after-the-throw"),
      (_, _) => ValueTask.FromResult(7),
      null!,   // execution strategies do not read the policy context
      cancellationToken);

    await Assert.That(afterTheThrow).IsEqualTo(7)
      .Because("a delegate that throws out of the worker's await must be swallowed by the net rather "
        + "than end the loop; if the loop ended, this item is never read and the call hangs");

    // Ending the loop stops the activity the recorder wrote to.
    await executor.StopAsync(CancellationToken.None);

    var activity = await captured.Task;
    await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error)
      .Because("the exception is swallowed for the caller but must stay visible to an operator, or a "
        + "safety net that fires often looks exactly like one that never fires");
    await Assert.That(activity.GetTagItem("defensive.code")).IsEqualTo(true)
      .Because("the tag is what separates a defensive path from an ordinary error in a trace");
    await Assert.That(activity.GetTagItem("exception.message")).IsEqualTo("pooled source teardown blew up")
      .Because("the recorded exception has to be the one that escaped, or the trace names the wrong fault");
    await Assert.That(activity.StatusDescription).IsEqualTo("Unexpected exception escaped work item execution")
      .Because("the description is what tells an operator which net fired");

    await executor.DisposeAsync();
  }

  /// <summary>
  /// A work item whose token is already canceled when the worker reads it is finished without being
  /// run, and the loop carries on. The ordinary path completes such an item through the pooled
  /// source so the caller's await returns; an item enqueued through the test seam has no caller and
  /// no pooled state, so finishing it is nothing more than not running it.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ProcessWorkItems_TokenAlreadyCanceled_SkipsTheItemAndKeepsRunningAsync(
      CancellationToken cancellationToken) {
    var executor = new SerialExecutor();
    await executor.StartAsync(cancellationToken);

    var ran = false;
    using var alreadyCanceled = new CancellationTokenSource();
    await alreadyCanceled.CancelAsync();

    await executor.EnqueueFaultingForTestsAsync(
      _ => {
        ran = true;
        return ValueTask.CompletedTask;
      },
      alreadyCanceled.Token);

    // FIFO: this item is read after the canceled one, so its completion proves the worker already
    // decided what to do with the canceled item — no polling, no timeout standing in for a signal.
    var afterTheSkip = await executor.ExecuteAsync<int>(
      _envelope("after-the-skip"),
      (_, _) => ValueTask.FromResult(11),
      null!,   // execution strategies do not read the policy context
      cancellationToken);

    await Assert.That(afterTheSkip).IsEqualTo(11)
      .Because("the loop has to continue past an item it skips; if it ended, this item is never read "
        + "and the call hangs");
    await Assert.That(ran).IsFalse()
      .Because("a work item canceled while it sat in the channel must not run — the caller has "
        + "already been told it was canceled");

    await executor.StopAsync(CancellationToken.None);
    await executor.DisposeAsync();
  }

  /// <summary>A locally dispatched envelope, which is all the executor reads of it.</summary>
  private static MessageEnvelope<FaultProbeMessage> _envelope(string payload) =>
    new() {
      MessageId = MessageId.New(),
      Payload = new FaultProbeMessage(payload),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  private sealed record FaultProbeMessage(string Payload) : IMessage;
}
