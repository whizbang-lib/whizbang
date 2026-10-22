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

namespace Whizbang.Core.Component.Tests;

/// <summary>
/// The claim loop tells each claim which lifecycle phase this instance announces, and every registration it sends
/// carries that phase (#1638). A claim whose row records another phase (a transition write that was lost, or overwritten
/// by a beat built before it) is reported stale, and the registration the loop then sends writes the phase, so a ready
/// instance can never stay ineligible for work while it claims. Every wait is on the coordinator receiving the call it
/// asserts.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ClaimWorker.cs</code-under-test>
[Category("Component")]
public class ClaimWorkerLifecyclePhaseTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);

  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  /// <summary>A lifecycle in the middle of a transition: settled on one phase, announcing the next.</summary>
  private sealed class Lifecycle(LifecyclePhase settled, LifecyclePhase announced) : IWhizbangLifecycleState {
    public LifecyclePhase Phase => settled;
    public LifecyclePhase AnnouncedPhase => announced;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  /// <summary>
  /// Completes a signal with the first claim and with each registration. The first claim reports the registration
  /// stale when told to, so the loop registers again after it.
  /// </summary>
  private sealed class Coordinator(bool firstClaimStale) : IWorkCoordinator {
    private readonly TaskCompletionSource<ClaimWorkRequest> _firstClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<HeartbeatRequest> _initialRegistration = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<HeartbeatRequest> _refreshRegistration = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _registrations;
    public Task<ClaimWorkRequest> FirstClaim => _firstClaim.Task;
    public Task<HeartbeatRequest> InitialRegistration => _initialRegistration.Task;
    public Task<HeartbeatRequest> RefreshRegistration => _refreshRegistration.Task;

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      var first = _firstClaim.TrySetResult(request);
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        InstanceRegistrationStale = first && firstClaimStale
      });
    }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      if (Interlocked.Increment(ref _registrations) == 1) {
        _initialRegistration.TrySetResult(request);
      } else {
        _refreshRegistration.TrySetResult(request);
      }
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

  private static ClaimWorker _worker(IWorkCoordinator coordinator, IWhizbangLifecycleState? lifecycle) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    // Resolved from the claim's scope, as the coordinator is; a host without a lifecycle says nothing of its phase.
    if (lifecycle is not null) {
      services.AddSingleton(lifecycle);
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

  private static async Task<T> _runUntilAsync<T>(Coordinator coordinator, IWhizbangLifecycleState? lifecycle, Func<Coordinator, Task<T>> signal) {
    var worker = _worker(coordinator, lifecycle);
    await worker.StartAsync(CancellationToken.None);
    try {
      return await signal(coordinator).WaitAsync(_deadline);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  private static Lifecycle _enteringAcceptingCommands() => new(LifecyclePhase.Migrating, LifecyclePhase.AcceptingCommands);

  [Test]
  public async Task AClaim_CarriesThePhaseTheInstanceAnnouncesAsync() {
    var request = await _runUntilAsync(new Coordinator(firstClaimStale: false), _enteringAcceptingCommands(), c => c.FirstClaim);

    await Assert.That(request.LifecyclePhase).IsEqualTo(nameof(LifecyclePhase.AcceptingCommands))
      .Because("the announced phase is the one the transition records; the settled phase lags it while acks are awaited, "
        + "and a claim comparing the row against it would undo the transition");
  }

  [Test]
  public async Task AClaim_WithNoLifecycleRegistered_SaysNothingOfItsPhaseAsync() {
    var request = await _runUntilAsync(new Coordinator(firstClaimStale: false), lifecycle: null, c => c.FirstClaim);

    await Assert.That(request.LifecyclePhase).IsNull()
      .Because("a host without a lifecycle records no phase, and the claim then judges its row by liveness alone");
  }

  [Test]
  public async Task TheRegistrationBeforeTheFirstClaim_CarriesThePhaseAsync() {
    var request = await _runUntilAsync(new Coordinator(firstClaimStale: false), _enteringAcceptingCommands(), c => c.InitialRegistration);

    await Assert.That(request.LifecyclePhase).IsEqualTo(nameof(LifecyclePhase.AcceptingCommands))
      .Because("a registration that inserts the row without a phase would make a new instance read as an older build");
  }

  [Test]
  public async Task TheRegistrationAfterAStaleClaim_WritesThePhaseAsync() {
    var request = await _runUntilAsync(new Coordinator(firstClaimStale: true), _enteringAcceptingCommands(), c => c.RefreshRegistration);

    await Assert.That(request.LifecyclePhase).IsEqualTo(nameof(LifecyclePhase.AcceptingCommands))
      .Because("this is the write that repairs a lost transition: without the phase it would refresh the heartbeat and "
        + "leave the instance ineligible");
  }

  [Test]
  public async Task TheRegistrations_WithNoLifecycleRegistered_KeepTheRecordedPhaseAsync() {
    var request = await _runUntilAsync(new Coordinator(firstClaimStale: true), lifecycle: null, c => c.RefreshRegistration);

    await Assert.That(request.LifecyclePhase).IsNull()
      .Because("a registration without a phase keeps whatever the row records");
  }
}
