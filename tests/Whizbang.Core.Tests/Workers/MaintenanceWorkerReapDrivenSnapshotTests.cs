using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the reap-driven ephemeral snapshot wiring (E1 (2-reap)): each maintenance tick, BEFORE
/// perform_maintenance reaps, the worker asks the coordinator for the (stream, perspective) pairs whose
/// consumed, aged ephemeral bodies are about to be reaped uncovered, and drives a bootstrap snapshot for
/// each via the runner — so the rewind floor survives the reap. Inert without a perspective runner registry.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class MaintenanceWorkerReapDrivenSnapshotTests {
  private sealed class FakeRunner : IPerspectiveRunner {
    public List<(Guid StreamId, string Perspective, Guid LastEventId)> BootstrapCalls { get; } = [];
    public Type PerspectiveType => typeof(object);
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) {
      BootstrapCalls.Add((streamId, perspectiveName, lastProcessedEventId));
      return Task.CompletedTask;
    }
    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private sealed class FakeRegistry(IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => new HashSet<LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class FakeCoordinator : IWorkCoordinator {
    public List<EphemeralSnapshotTarget> Pairs { get; init; } = [];
    public int QueryCallCount { get; private set; }
    public Task<IReadOnlyList<EphemeralSnapshotTarget>> GetEphemeralPairsNeedingSnapshotAsync(CancellationToken cancellationToken = default) {
      QueryCallCount++;
      return Task.FromResult<IReadOnlyList<EphemeralSnapshotTarget>>(Pairs);
    }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);

    // Unused IWorkCoordinator surface for this test.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private static MaintenanceWorker _buildWorker(FakeCoordinator coord, FakeRegistry? registry) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (registry is not null) {
      services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  [Test]
  public async Task RunMaintenanceOnce_ReapDrivenSnapshot_BootstrapsEachTargetPairAsync() {
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var coord = new FakeCoordinator { Pairs = [new EphemeralSnapshotTarget(streamId, "MyPerspective", eventId)] };
    var runner = new FakeRunner();

    await _buildWorker(coord, new FakeRegistry(runner)).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(runner.BootstrapCalls).Contains((streamId, "MyPerspective", eventId))
      .Because("The reap-driven step bootstraps a snapshot for each (stream, perspective) whose bodies are about to be reaped.");
  }

  [Test]
  public async Task RunMaintenanceOnce_NoRunnerRegistry_IsInertAsync() {
    // A non-perspective host has no runner registry — the reap-driven step must short-circuit BEFORE the
    // query (and not throw), so nothing runs against a missing registry.
    var coord = new FakeCoordinator { Pairs = [new EphemeralSnapshotTarget(Guid.NewGuid(), "P", Guid.NewGuid())] };
    await _buildWorker(coord, registry: null).RunMaintenanceOnceAsync(CancellationToken.None);
    await Assert.That(coord.QueryCallCount).IsEqualTo(0)
      .Because("Without a runner registry the reap-driven step short-circuits before querying — inert, not fatal.");
  }
}
