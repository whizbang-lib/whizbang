// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Component.Tests;

/// <summary>
/// The claim loop tells each claim whether this instance holds its alive-lock (#1286), so a direct instance on the
/// slow heartbeat cadence is not asked to re-register between beats; its peers rank it by the lock. Every wait is on
/// the coordinator receiving the claim.
/// </summary>
[Category("Component")]
public class ClaimWorkerAliveLockTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);

  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class AliveLock(bool held) : IInstanceAliveLockSource {
    public bool IsAliveLockHeld => held;
  }

  /// <summary>Completes a signal with the first claim request it receives.</summary>
  private sealed class Coordinator : IWorkCoordinator {
    private readonly TaskCompletionSource<ClaimWorkRequest> _firstClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ClaimWorkRequest> FirstClaim => _firstClaim.Task;

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      _firstClaim.TrySetResult(request);
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static ClaimWorker _worker(IWorkCoordinator coordinator, IInstanceAliveLockSource? aliveLock) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    // The claim resolves the lock source from its scope, as it resolves the coordinator; a host without one holds none.
    if (aliveLock is not null) {
      services.AddSingleton(aliveLock);
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new ClaimWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new Instance(),
      notificationListener: new NoOpWorkNotificationListener(),
      schemaReadyGate: gate,
      options: Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 10, PollingMaxIntervalMilliseconds = 50 }),
      logger: NullLogger<ClaimWorker>.Instance,
      outboxChannel: new WorkChannelWriter(),
      inboxChannel: new InboxChannelWriter(),
      perspectiveChannel: new PerspectiveChannelWriter(),
      perspectiveDrainChannel: new PerspectiveDrainChannel(),
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: NullSignalBus.Instance);
  }

  private static async Task<ClaimWorkRequest> _firstClaimAsync(IInstanceAliveLockSource? aliveLock) {
    var coordinator = new Coordinator();
    var worker = _worker(coordinator, aliveLock);
    await worker.StartAsync(CancellationToken.None);
    try {
      return await coordinator.FirstClaim.WaitAsync(_deadline);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task AClaim_WhileTheAliveLockIsHeld_SaysSoAsync() {
    var request = await _firstClaimAsync(new AliveLock(held: true));

    await Assert.That(request.AliveLockHeld).IsTrue()
      .Because("a direct instance holding its lock is live by the lock, so the claim must not ask it to re-register");
  }

  [Test]
  public async Task AClaim_WithoutTheAliveLock_SaysSoAsync() {
    var request = await _firstClaimAsync(new AliveLock(held: false));

    await Assert.That(request.AliveLockHeld).IsFalse();
  }

  [Test]
  public async Task AClaim_WithNoAliveLockSourceRegistered_HoldsNoLockAsync() {
    var request = await _firstClaimAsync(aliveLock: null);

    await Assert.That(request.AliveLockHeld).IsFalse()
      .Because("a host without a lock source is judged by its heartbeat, as before");
  }
}
