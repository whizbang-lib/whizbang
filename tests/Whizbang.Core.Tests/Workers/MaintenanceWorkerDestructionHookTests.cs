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
    private readonly DestructionResult _result;
    public int BeforeCalls { get; private set; }
    public int LastBatchSize { get; private set; }
    public RecordingHook(List<string> log, DestructionResult? result = null, bool throwOnBefore = false) {
      _log = log; _throwOnBefore = throwOnBefore; _result = result ?? DestructionResult.Proceed();
    }

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      BeforeCalls++;
      LastBatchSize = context.Targets.Count;
      _log.Add($"before:{context.Targets.Count}");
      if (_throwOnBefore) {
        throw new InvalidOperationException("hook blew up");
      }
      return ValueTask.FromResult(_result);
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      _log.Add($"after:{context.Targets.Count}");
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeCoordinator : IWorkCoordinator {
    private readonly List<string> _log;
    public List<EphemeralDestructionTarget> Targets { get; init; } = [];
    public int GetTargetsCallCount { get; private set; }
    public FakeCoordinator(List<string> log) { _log = log; }

    public List<(IReadOnlyList<Guid> Ids, DateTimeOffset Until)> Holds { get; } = [];

    public Task<IReadOnlyList<EphemeralDestructionTarget>> GetEphemeralBodiesAboutToReapAsync(CancellationToken cancellationToken = default) {
      GetTargetsCallCount++;
      return Task.FromResult<IReadOnlyList<EphemeralDestructionTarget>>(Targets);
    }

    public Task HoldEphemeralDestructionAsync(IReadOnlyList<Guid> eventIds, DateTimeOffset holdUntil, CancellationToken cancellationToken = default) {
      Holds.Add((eventIds, holdUntil));
      return Task.CompletedTask;
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
  public async Task RunMaintenanceOnce_HookRegistered_FiresOnceForTheBatchBeforeReapThenAfterAsync() {
    var log = new List<string>();
    // Two events, on different streams — one batched hook call must see both.
    var coord = new FakeCoordinator(log) {
      Targets = [
        new EphemeralDestructionTarget(Guid.NewGuid(), Guid.NewGuid(), "A"),
        new EphemeralDestructionTarget(Guid.NewGuid(), Guid.NewGuid(), "B"),
      ],
    };
    var hook = new RecordingHook(log);

    await _buildWorker(coord, hook).RunMaintenanceOnceAsync(CancellationToken.None);

    // Batched: exactly one OnBefore for the whole set, before the reap; one OnAfter, after it.
    await Assert.That(hook.BeforeCalls).IsEqualTo(1)
      .Because("The hook is invoked ONCE for the whole batch, not once per event.");
    await Assert.That(hook.LastBatchSize).IsEqualTo(2)
      .Because("The context carries every about-to-reap event this cycle.");
    await Assert.That(log.Count).IsEqualTo(3);
    await Assert.That(log[0]).IsEqualTo("before:2").Because("The pre-hook (batch of 2) commits before the reap.");
    await Assert.That(log[1]).IsEqualTo("reap");
    await Assert.That(log[2]).IsEqualTo("after:2").Because("The post-hook fires after the reap.");
    await Assert.That(coord.Holds.Count).IsEqualTo(0)
      .Because("Proceed sets no hold — the reap deletes the batch.");
  }

  [Test]
  public async Task RunMaintenanceOnce_HookCancels_HoldsBatchFarFuture_NoPostDestructionAsync() {
    var log = new List<string>();
    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();
    var coord = new FakeCoordinator(log) {
      Targets = [new EphemeralDestructionTarget(e1, Guid.NewGuid(), "A"), new EphemeralDestructionTarget(e2, Guid.NewGuid(), "B")],
    };

    await _buildWorker(coord, new RecordingHook(log, DestructionResult.Cancelled)).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Holds.Count).IsEqualTo(1).Because("Cancel holds the whole batch in one call.");
    await Assert.That(coord.Holds[0].Ids).IsEquivalentTo(new[] { e1, e2 });
    await Assert.That(coord.Holds[0].Until).IsEqualTo(DateTimeOffset.MaxValue)
      .Because("Cancel = a far-future hold (keep the data — the developer's leak-risk call).");
    await Assert.That(log).DoesNotContain("after:2")
      .Because("Nothing was destroyed (all held), so PostDestruction does not fire.");
  }

  [Test]
  public async Task RunMaintenanceOnce_HookDefers_HoldsBatchUntilInstant_NoPostDestructionAsync() {
    var log = new List<string>();
    var eventId = Guid.NewGuid();
    var until = DateTimeOffset.UtcNow.AddHours(2);
    var coord = new FakeCoordinator(log) {
      Targets = [new EphemeralDestructionTarget(eventId, Guid.NewGuid(), "A")],
    };

    await _buildWorker(coord, new RecordingHook(log, DestructionResult.Defer(until))).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Holds.Count).IsEqualTo(1);
    await Assert.That(coord.Holds[0].Until).IsEqualTo(until)
      .Because("Defer(until) reschedules the reap by holding the batch to that instant.");
    await Assert.That(log).DoesNotContain("after:1")
      .Because("A deferred body is not destroyed this cycle, so PostDestruction does not fire.");
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
    var coord = new FakeCoordinator(log) {
      Targets = [new EphemeralDestructionTarget(Guid.NewGuid(), Guid.NewGuid(), "T")],
    };

    // A throwing pre-hook must not take down the cycle; the reap still runs.
    await _buildWorker(coord, new RecordingHook(log, throwOnBefore: true)).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(log).Contains("before:1");
    await Assert.That(log).Contains("reap")
      .Because("A PreDestruction hook failure is logged and non-fatal — the maintenance cycle completes.");
  }
}
