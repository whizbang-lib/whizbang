using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Unit tests for F1 unify-now: ClaimWorker subscribes to the three work-available signals on the
/// bus, and any of them raises the wake semaphore so <c>_claimOnceAsync</c> runs. When the bus is
/// null the worker falls back to the pre-unify-now adaptive-poll behavior (covered by the existing
/// ClaimWorkerTests suite).
/// </summary>
public class ClaimWorkerBusWakeTests {
  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class CountingCoordinator : IWorkCoordinator {
    public int CallCount { get; private set; }
    public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      var c = Interlocked.Increment(ref _count);
      if (c == 1) { FirstCall.TrySetResult(); }
      if (c == 2) { SecondCall.TrySetResult(); }
      CallCount = c;
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }
    private int _count;

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
  }

  private static (ClaimWorker Worker, CountingCoordinator Coord, SignalBus Bus, CancellationTokenSource Cts) _create() {
    var coord = new CountingCoordinator();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var schemaGate = new SchemaReadyGate();
    schemaGate.MarkReady();

    var bus = new SignalBus(transports: [new InMemorySignalTransport()], pullSources: []);

    var worker = new ClaimWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new StubInstanceProvider(),
      notificationListener: new NoOpWorkNotificationListener(),
      schemaReadyGate: schemaGate,
      // Huge polling intervals: without the bus wake, the SecondCall.WaitAsync would time out
      // long before the adaptive-poll timer fires again.
      options: Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 300_000,
        PollingMaxIntervalMilliseconds = 300_000
      }),
      logger: NullLogger<ClaimWorker>.Instance,
      outboxChannel: new WorkChannelWriter(),
      inboxChannel: new InboxChannelWriter(),
      perspectiveChannel: new PerspectiveChannelWriter(),
      perspectiveDrainChannel: new PerspectiveDrainChannel(),
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: bus);
    return (worker, coord, bus, new CancellationTokenSource(TimeSpan.FromSeconds(15)));
  }

  [Test]
  public async Task WorkOutboxAvailableSignal_WakesClaimWorkerAsync() {
    var (worker, coord, bus, cts) = _create();
    try {
      await bus.StartAsync(cts.Token);
      await worker.StartAsync(cts.Token);

      await coord.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
      await bus.PublishAsync(new WorkOutboxAvailableSignal(), SignalTarget.Instance(Guid.NewGuid()), cts.Token);
      await coord.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

      await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(2)
        .Because("WorkOutboxAvailableSignal on the bus must wake ClaimWorker via the new subscription");
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      cts.Dispose();
    }
  }

  [Test]
  public async Task WorkInboxAvailableSignal_WakesClaimWorkerAsync() {
    var (worker, coord, bus, cts) = _create();
    try {
      await bus.StartAsync(cts.Token);
      await worker.StartAsync(cts.Token);

      await coord.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
      await bus.PublishAsync(new WorkInboxAvailableSignal(), SignalTarget.Instance(Guid.NewGuid()), cts.Token);
      await coord.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

      await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(2);
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      cts.Dispose();
    }
  }

  [Test]
  public async Task WorkPerspectiveAvailableSignal_WakesClaimWorkerAsync() {
    var (worker, coord, bus, cts) = _create();
    try {
      await bus.StartAsync(cts.Token);
      await worker.StartAsync(cts.Token);

      await coord.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
      await bus.PublishAsync(new WorkPerspectiveAvailableSignal(), SignalTarget.Instance(Guid.NewGuid()), cts.Token);
      await coord.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

      await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(2);
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      cts.Dispose();
    }
  }

  [Test]
  public async Task NoBusSignals_WorkerStaysParkedAsync() {
    var (worker, coord, bus, cts) = _create();
    try {
      await bus.StartAsync(cts.Token);
      await worker.StartAsync(cts.Token);
      await coord.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

      // No wake fires. The worker sits on _wake.WaitAsync until the test's 3s wait times out.
      var timedOut = false;
      try {
        await coord.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
      } catch (TimeoutException) {
        timedOut = true;
      }
      await Assert.That(timedOut).IsTrue()
        .Because("with bus wired and no signals, the worker must not poll on its own — that IS the F1 unify-now change");
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      cts.Dispose();
    }
  }
}
