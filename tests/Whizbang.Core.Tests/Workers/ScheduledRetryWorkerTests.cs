using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// v0.502 slice B.4 — unit tests for <see cref="ScheduledRetryWorker"/>. Verifies the
/// worker's role as a low-cadence NOTIFY emitter:
/// <list type="bullet">
///   <item><description>Cycles invoke <see cref="IWorkCoordinator.NotifyScheduledRetryDueAsync"/>
///   on the configured cadence.</description></item>
///   <item><description>The returned stream count is accumulated into
///   <see cref="ScheduledRetryWorker.TotalStreamsWoken"/>.</description></item>
///   <item><description>When <see cref="ScheduledRetryWorkerOptions.Enabled"/>=false, the
///   worker doesn't call the coordinator.</description></item>
/// </list>
/// </summary>
[NotInParallel(Order = 200)]
public class ScheduledRetryWorkerTests {

  private sealed class CountingCoordinator : IWorkCoordinator {
    public int Calls;
    public int ReturnValue { get; set; }
    public TaskCompletionSource FirstCallSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondCallSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<int> NotifyScheduledRetryDueAsync(CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref Calls);
      if (n == 1) { FirstCallSignal.TrySetResult(); } else if (n == 2) { SecondCallSignal.TrySetResult(); }
      return Task.FromResult(ReturnValue);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  }

  private sealed class ImmediateSchemaGate : ISchemaReadyGate {
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void MarkReady() { }
    public bool IsReady => true;
  }

  private static (ScheduledRetryWorker Worker, CountingCoordinator Coord) _newWorker(
      int pollIntervalSeconds, int returnValue = 0, bool enabled = true) {
    var coord = new CountingCoordinator { ReturnValue = returnValue };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var worker = new ScheduledRetryWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new ImmediateSchemaGate(),
      Options.Create(new ScheduledRetryWorkerOptions {
        Enabled = enabled,
        PollIntervalSeconds = pollIntervalSeconds,
      }),
      NullLogger<ScheduledRetryWorker>.Instance);
    return (worker, coord);
  }

  [Test]
  public async Task WakeCycle_InvokesNotifyScheduledRetryDueAsync() {
    var (worker, coord) = _newWorker(pollIntervalSeconds: 1, returnValue: 3);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(coord.Calls).IsGreaterThanOrEqualTo(2)
      .Because("worker should invoke the coordinator's wake method on each cycle");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task TotalStreamsWoken_AccumulatesReturnedCountAsync() {
    var (worker, coord) = _newWorker(pollIntervalSeconds: 1, returnValue: 4);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(worker.TotalStreamsWoken).IsGreaterThanOrEqualTo(8)
      .Because("each cycle accumulates the return value of NotifyScheduledRetryDueAsync (4 × 2 cycles = 8)");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Disabled_NeverInvokesCoordinatorAsync() {
    var (worker, coord) = _newWorker(pollIntervalSeconds: 1, returnValue: 5, enabled: false);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Give it more than two intervals' worth of time to fire if it were going to.
    await Task.Delay(TimeSpan.FromSeconds(2));

    await Assert.That(coord.Calls).IsEqualTo(0)
      .Because("disabled worker must not call the coordinator");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task TotalNotifyCycles_IncrementsEvenWhenZeroStreamsAreDueAsync() {
    var (worker, coord) = _newWorker(pollIntervalSeconds: 1, returnValue: 0);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(worker.TotalNotifyCycles).IsGreaterThanOrEqualTo(2)
      .Because("cycles count regardless of whether streams were woken");
    await Assert.That(worker.TotalStreamsWoken).IsEqualTo(0)
      .Because("when nothing is due, total streams woken stays zero");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
