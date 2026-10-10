// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;

namespace Whizbang.Partitioning.Tests;

/// <summary>
/// The registration records how an instance reaches the database (#1254), so the partition assigner judges it by its
/// alive-lock where its peers can see one and by its heartbeat otherwise. One beat is driven directly against a fake
/// clock and an in-memory coordinator: no hosted loop, no threads, no I/O.
/// </summary>
[Category("Workers")]
public class HeartbeatConnectionModeTests {
  private sealed class Lifecycle : IWhizbangLifecycleState {
    public LifecyclePhase Phase => LifecyclePhase.Running;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  private sealed class Version : ILibraryVersionProvider {
    public string LibraryVersion => "1.0.0";
  }

  private sealed class DirectConnection : IInstanceConnectionModeSource {
    public InstanceConnectionMode ConnectionMode => InstanceConnectionMode.Direct;
    public bool IsAliveLockHeld => true;
  }

  /// <summary>Records the beats it is sent.</summary>
  private sealed class Coordinator : IWorkCoordinator {
    public List<HeartbeatRequest> Beats { get; } = [];
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      Beats.Add(request);
      return Task.FromResult(true);
    }
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static async Task<HeartbeatRequest> _beatAsync(IInstanceConnectionModeSource? source) {
    var coordinator = new Coordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    if (source is not null) {
      services.AddSingleton(source);
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new HeartbeatWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new ServiceInstanceProvider(Guid.CreateVersion7(), "svc", "host", processId: 1),
      schemaReadyGate: gate,
      options: Options.Create(new HeartbeatWorkerOptions()),
      logger: NullLogger<HeartbeatWorker>.Instance,
      lifecycleState: new Lifecycle(),
      libraryVersion: new Version(),
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      aliveLockSource: NullInstanceAliveLockSource.Instance,
      signalBus: NullSignalBus.Instance,
      timeProvider: new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1)));

    _ = await worker.TickForTestsAsync(CancellationToken.None);
    return coordinator.Beats.Single();
  }

  [Test]
  public async Task Beat_WithADirectConnection_RegistersDirectAsync() {
    await Assert.That((await _beatAsync(new DirectConnection())).ConnectionMode).IsEqualTo(InstanceConnectionMode.Direct);
  }

  [Test]
  public async Task Beat_WithTheNullSource_RegistersPooledAsync() {
    await Assert.That((await _beatAsync(NullInstanceConnectionModeSource.Instance)).ConnectionMode)
      .IsEqualTo(InstanceConnectionMode.Pooled);
  }

  [Test]
  public async Task Beat_WithNoSourceRegistered_RegistersPooledAsync() {
    await Assert.That((await _beatAsync(null)).ConnectionMode).IsEqualTo(InstanceConnectionMode.Pooled)
      .Because("an instance whose driver cannot say is judged by its heartbeat");
  }

  [Test]
  public async Task NullSource_IsPooledWithNoLockAsync() {
    await Assert.That(NullInstanceConnectionModeSource.Instance.ConnectionMode).IsEqualTo(InstanceConnectionMode.Pooled);
    await Assert.That(NullInstanceConnectionModeSource.Instance.IsAliveLockHeld).IsFalse();
  }
}
