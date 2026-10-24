// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Startup;
using Whizbang.Core.Tests.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// An accepted heartbeat says the instance's row exists, so the startup stage held since before it did, or since a
/// write was lost, is written then (#1636). A refused beat (the instance was evicted) says nothing of the kind.
/// </summary>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <code-under-test>src/Whizbang.Core/Workers/HeartbeatWorker.cs</code-under-test>
[Category("Unit")]
public class HeartbeatWorkerStartupStageTests {
  [Test]
  public async Task AnAcceptedBeat_TellsTheStageReporterTheRowExistsAsync() {
    var (worker, stages) = _worker(accepted: true);

    await worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(stages.Calls).IsEquivalentTo(["Registered"]);
  }

  [Test]
  public async Task ARefusedBeat_TellsTheStageReporterNothingAsync() {
    var (worker, stages) = _worker(accepted: false);

    await worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(stages.Calls).IsEmpty()
      .Because("an evicted instance's row is gone, and nothing should be written to it");
  }

  private static (HeartbeatWorker Worker, RecordingStageReporter Stages) _worker(bool accepted) {
    var stages = new RecordingStageReporter();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new BeatCoordinator(accepted));
    services.AddSingleton<IStartupStageReporter>(stages);
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new HeartbeatWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
      schemaReadyGate: gate,
      options: Options.Create(new HeartbeatWorkerOptions { IntervalSeconds = 30, SlowIntervalSeconds = 60 }),
      logger: NullLogger<HeartbeatWorker>.Instance,
      lifecycleState: HeartbeatTestDependencies.LifecycleState,
      libraryVersion: HeartbeatTestDependencies.Version,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      aliveLockSource: NullInstanceAliveLockSource.Instance,
      signalBus: NullSignalBus.Instance,
      timeProvider: new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    return (worker, stages);
  }

  private sealed class BeatCoordinator(bool accepted) : IWorkCoordinator {
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(accepted);
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
