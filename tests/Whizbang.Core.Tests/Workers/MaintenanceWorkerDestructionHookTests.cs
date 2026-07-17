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
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the E2-2 destruction-hook wiring in the maintenance cycle: a registered <see cref="IDestructionHook"/>
/// fires <c>OnBeforeDestructionAsync</c> for each about-to-reap ephemeral body BEFORE the reap
/// (<c>PerformMaintenanceAsync</c>), then <c>OnAfterDestructionAsync</c> detached AFTER it. Inert (no query,
/// no hook calls) when no hook is registered; a throwing hook is non-fatal.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class MaintenanceWorkerDestructionHookTests {
  private sealed class RecordingHook : IDestructionHook {
    private readonly List<string> _log;
    private readonly bool _throwOnBefore;
    public RecordingHook(List<string> log, bool throwOnBefore = false) { _log = log; _throwOnBefore = throwOnBefore; }

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      _log.Add($"before:{context.EventIds[0]}");
      if (_throwOnBefore) {
        throw new InvalidOperationException("hook blew up");
      }
      return ValueTask.FromResult(DestructionResult.Proceed());
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      _log.Add($"after:{context.EventIds[0]}");
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeCoordinator : IWorkCoordinator {
    private readonly List<string> _log;
    public List<EphemeralDestructionTarget> Targets { get; init; } = [];
    public int GetTargetsCallCount { get; private set; }
    public FakeCoordinator(List<string> log) { _log = log; }

    public Task<IReadOnlyList<EphemeralDestructionTarget>> GetEphemeralBodiesAboutToReapAsync(CancellationToken cancellationToken = default) {
      GetTargetsCallCount++;
      return Task.FromResult<IReadOnlyList<EphemeralDestructionTarget>>(Targets);
    }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default) {
      _log.Add("reap");
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }

    // Unused IWorkCoordinator surface.
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

  private static MaintenanceWorker _buildWorker(FakeCoordinator coord, IDestructionHook? hook) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (hook is not null) {
      services.AddSingleton(hook);
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
  public async Task RunMaintenanceOnce_HookRegistered_FiresBeforeReapThenAfterAsync() {
    var log = new List<string>();
    var eventId = Guid.NewGuid();
    var coord = new FakeCoordinator(log) {
      Targets = [new EphemeralDestructionTarget(eventId, Guid.NewGuid(), "T")],
    };

    await _buildWorker(coord, new RecordingHook(log)).RunMaintenanceOnceAsync(CancellationToken.None);

    // The pre-hook commits before the reap; the post-hook fires after it.
    await Assert.That(log).IsEquivalentTo(new[] { $"before:{eventId}", "reap", $"after:{eventId}" });
  }

  [Test]
  public async Task RunMaintenanceOnce_NoHook_IsInert_DoesNotQueryTargetsAsync() {
    var log = new List<string>();
    var coord = new FakeCoordinator(log) {
      Targets = [new EphemeralDestructionTarget(Guid.NewGuid(), Guid.NewGuid(), "T")],
    };

    await _buildWorker(coord, hook: null).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.GetTargetsCallCount).IsEqualTo(0)
      .Because("Without a registered IDestructionHook the destruction step short-circuits before querying — inert.");
    await Assert.That(log.Count).IsEqualTo(1);
    await Assert.That(log[0]).IsEqualTo("reap")
      .Because("The reap still runs; nothing fires around it.");
  }

  [Test]
  public async Task RunMaintenanceOnce_HookThrowsOnBefore_IsNonFatal_ReapStillRunsAsync() {
    var log = new List<string>();
    var eventId = Guid.NewGuid();
    var coord = new FakeCoordinator(log) {
      Targets = [new EphemeralDestructionTarget(eventId, Guid.NewGuid(), "T")],
    };

    // A throwing pre-hook must not take down the cycle; the reap still runs.
    await _buildWorker(coord, new RecordingHook(log, throwOnBefore: true)).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(log).Contains("before:" + eventId);
    await Assert.That(log).Contains("reap")
      .Because("A PreDestruction hook failure is logged and non-fatal — the maintenance cycle completes.");
  }
}
