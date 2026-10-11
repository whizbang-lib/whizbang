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
/// The claim loop and the partition assignment (#1254), on the worker's own thread: each claim presents the cached
/// assignment's version, and a claim that reports it stale marks the cache stale. Every wait is on the signal for the
/// exact transition asserted (the coordinator receiving a claim, the cache being marked stale).
/// </summary>
[Category("Component")]
public class ClaimWorkerPartitionAssignmentTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);
  private static readonly PartitionAssignmentVersion _version = new(9, 4);

  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  /// <summary>A cache that always presents one version and signals when it is marked stale.</summary>
  private sealed class Source(PartitionAssignmentVersion? version) : IPartitionAssignmentSource {
    private readonly TaskCompletionSource _markedStale = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task MarkedStale => _markedStale.Task;
    public PartitionAssignment? Current => null;
#pragma warning disable CS0067 // The claim loop never subscribes; the interface requires the event.
    public event Action<PartitionAssignment?>? OnAssignmentChanged;
#pragma warning restore CS0067
    public void MarkStale() => _markedStale.TrySetResult();
    public ValueTask<PartitionAssignmentVersion?> ForClaimAsync(Guid instanceId, CancellationToken cancellationToken) => new(version);
  }

  /// <summary>Records each claim request and completes a signal on the first; can report the assignment stale.</summary>
  private sealed class Coordinator(bool reportStale) : IWorkCoordinator {
    private readonly TaskCompletionSource<ClaimWorkRequest> _firstClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ClaimWorkRequest> FirstClaim => _firstClaim.Task;

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      _firstClaim.TrySetResult(request);
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [], PartitionAssignmentStale = reportStale });
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

  private static ClaimWorker _worker(IWorkCoordinator coordinator, IPartitionAssignmentSource? source) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    // The claim resolves the assignment from its scope, as it resolves the coordinator; a host without one ranks itself.
    if (source is not null) {
      services.AddSingleton(source);
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

  [Test]
  public async Task AClaim_PresentsTheCachedAssignmentsVersionAsync() {
    var coordinator = new Coordinator(reportStale: false);
    var worker = _worker(coordinator, new Source(_version));

    await worker.StartAsync(CancellationToken.None);
    try {
      var request = await coordinator.FirstClaim.WaitAsync(_deadline);

      await Assert.That(request.PartitionAssignment).IsEqualTo(_version);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task AClaimThatReportsTheAssignmentStale_MarksTheCacheStaleAsync() {
    var coordinator = new Coordinator(reportStale: true);
    var source = new Source(_version);
    var worker = _worker(coordinator, source);

    await worker.StartAsync(CancellationToken.None);
    try {
      await source.MarkedStale.WaitAsync(_deadline);

      await Assert.That(source.MarkedStale.IsCompletedSuccessfully).IsTrue();
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task WithoutAnAssignmentSource_AClaimRanksItselfAsync() {
    var coordinator = new Coordinator(reportStale: true);
    var worker = _worker(coordinator, source: null);

    await worker.StartAsync(CancellationToken.None);
    try {
      var request = await coordinator.FirstClaim.WaitAsync(_deadline);

      await Assert.That(request.PartitionAssignment).IsNull()
        .Because("with no assigner configured every claim ranks itself, as before");
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }
}
