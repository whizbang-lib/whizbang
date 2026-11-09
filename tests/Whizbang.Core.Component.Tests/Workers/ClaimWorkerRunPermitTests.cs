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
using Whizbang.Core.RunControl;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Component.Tests.Workers;

/// <summary>
/// The claim worker obeys the instance's one run permit (#2045): every claim is taken inside a hold, so no claim starts
/// while the permit is closed and the run-control path can wait for the claim in hand. Once nothing is in hand, the path
/// asks it to release the streams it leased and has not started, so peers take them over at once. Every wait is on the
/// coordinator receiving the call asserted.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ClaimWorker.cs</code-under-test>
[Category("Component")]
[Category("RunControl")]
public class ClaimWorkerRunPermitTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);

  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  /// <summary>Records what the permit said at each claim, and every release of unstarted leases.</summary>
  private sealed class Coordinator(WhizbangRunPermit permit, WorkBatch? batch = null) : IWorkCoordinator {
    private readonly TaskCompletionSource<(bool Running, int Held)> _firstClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _claims;
    public Task<(bool Running, int Held)> FirstClaim => _firstClaim.Task;
    public Task SecondClaim => _secondClaim.Task;
    public Task Registered => _registered.Task;
    public int Claims => Volatile.Read(ref _claims);
    public List<(Guid Instance, IReadOnlyList<Guid> Inbox, IReadOnlyList<Guid> Perspective)> Releases { get; } = [];

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      if (Interlocked.Increment(ref _claims) == 1) {
        _firstClaim.TrySetResult((permit.IsRunning, permit.Held));
      } else {
        _secondClaim.TrySetResult();
      }
      return Task.FromResult(batch ?? new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }

    public Task<UnstartedLeaseRelease> ReleaseUnstartedLeasesAsync(
        Guid instanceId, IReadOnlyList<Guid> inboxStreamIds, IReadOnlyList<Guid> perspectiveStreamIds,
        CancellationToken cancellationToken = default) {
      Releases.Add((instanceId, inboxStreamIds, perspectiveStreamIds));
      return Task.FromResult(new UnstartedLeaseRelease(inboxStreamIds.Count, perspectiveStreamIds.Count));
    }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      _registered.TrySetResult();
      return Task.FromResult(true);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static ClaimWorker _worker(
      IWorkCoordinator coordinator, Instance instance, WhizbangRunPermit permit,
      InboxDrainChannel? inboxDrain = null, PerspectiveDrainChannel? perspectiveDrain = null) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    var sp = services.BuildServiceProvider();
    return new ClaimWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: instance,
      notificationListener: new NoOpWorkNotificationListener(),
      options: Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 10, PollingMaxIntervalMilliseconds = 50 }),
      logger: NullLogger<ClaimWorker>.Instance,
      outboxChannel: new WorkChannelWriter(),
      inboxChannel: new InboxChannelWriter(),
      perspectiveChannel: new PerspectiveChannelWriter(),
      perspectiveDrainChannel: perspectiveDrain ?? new PerspectiveDrainChannel(),
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: inboxDrain ?? new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: NullSignalBus.Instance,
      runPermit: permit);
  }

  [Test]
  public async Task AClosedPermit_HoldsTheFirstClaim_AndEveryClaimIsTakenInsideAHoldAsync() {
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    var coordinator = new Coordinator(permit);
    var worker = _worker(coordinator, new Instance(), permit);

    await worker.StartAsync(CancellationToken.None);
    try {
      // The body ran: it registered the instance, which happens before the first claim.
      await coordinator.Registered.WaitAsync(_deadline);
      await permit.ChangeAsync(RunState.Running, CancellationToken.None);

      var (running, held) = await coordinator.FirstClaim.WaitAsync(_deadline);
      await Assert.That(running).IsTrue().Because("no claim is taken while the run permit is closed");
      await Assert.That(held).IsEqualTo(1).Because("the claim is a hold, so the run-control path waits for it");
      await Assert.That(((IObeysRunPermit)worker).RunPermit).IsSameReferenceAs(permit);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task ReleaseHeldWork_ReleasesTheLastClaimsStreamsThatNoDrainHasStartedAsync() {
    var permit = new WhizbangRunPermit();
    var instance = new Instance();
    var inboxIdle = Guid.CreateVersion7();
    var inboxDraining = Guid.CreateVersion7();
    var perspective = Guid.CreateVersion7();
    var batch = new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      InboxStreamIds = [inboxIdle, inboxDraining],
      PerspectiveStreamIds = [perspective],
    };
    var coordinator = new Coordinator(permit, batch);
    var inboxDrain = new InboxDrainChannel();
    inboxDrain.MarkDraining(inboxDraining);
    var worker = _worker(coordinator, instance, permit, inboxDrain);

    await worker.StartAsync(CancellationToken.None);
    try {
      await coordinator.SecondClaim.WaitAsync(_deadline);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
    // #2126: the exit's account of what the instance held counts the last claim's streams, before the path releases them.
    await Assert.That(((IReleasesHeldWork)worker).HeldLeases).IsEqualTo(3);
    // The loop may itself have released a stuck re-offer (#724); the path's release is the last one.
    var before = coordinator.Releases.Count;
    await ((IReleasesHeldWork)worker).ReleaseHeldWorkAsync(CancellationToken.None);

    await Assert.That(coordinator.Releases.Count).IsEqualTo(before + 1);
    var release = coordinator.Releases[^1];
    await Assert.That(release.Instance).IsEqualTo(instance.InstanceId);
    await Assert.That(release.Inbox).IsEquivalentTo([inboxIdle])
      .Because("a stream a drain has begun is finished, never released under it");
    await Assert.That(release.Perspective).IsEquivalentTo([perspective]);
  }

  [Test]
  public async Task ReleaseHeldWork_BeforeAnyClaim_ReleasesNothingAsync() {
    var permit = new WhizbangRunPermit();
    var coordinator = new Coordinator(permit);
    var worker = _worker(coordinator, new Instance(), permit);

    await ((IReleasesHeldWork)worker).ReleaseHeldWorkAsync(CancellationToken.None);

    await Assert.That(coordinator.Releases).IsEmpty();
    await Assert.That(((IReleasesHeldWork)worker).HeldLeases).IsEqualTo(0).Because("nothing was claimed, so nothing is held");
  }

  [Test]
  public async Task AStoppedPermit_EndsTheClaimLoopWithoutAClaimAsync() {
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Stopped, CancellationToken.None);
    var coordinator = new Coordinator(permit);
    var worker = _worker(coordinator, new Instance(), permit);

    await worker.StartAsync(CancellationToken.None);
    try {
      await worker.ExecuteTask!.WaitAsync(_deadline);
      await Assert.That(coordinator.Claims).IsEqualTo(0);
      await Assert.That(worker.ExecuteTask.Status).IsEqualTo(TaskStatus.RanToCompletion);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }
}
