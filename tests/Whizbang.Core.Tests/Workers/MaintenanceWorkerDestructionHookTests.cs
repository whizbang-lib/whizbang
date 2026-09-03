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
    private readonly Exception? _beforeThrows;
    private readonly Exception? _afterThrows;
    private readonly DestructionResult _result;
    public int BeforeCalls { get; private set; }
    public int LastBatchSize { get; private set; }
    public RecordingHook(List<string> log, DestructionResult? result = null, bool throwOnBefore = false,
        Exception? beforeThrows = null, Exception? afterThrows = null) {
      _log = log; _throwOnBefore = throwOnBefore; _result = result ?? DestructionResult.Proceed();
      _beforeThrows = beforeThrows; _afterThrows = afterThrows;
    }

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      BeforeCalls++;
      LastBatchSize = context.Targets.Count;
      _log.Add($"before:{context.Targets.Count}");
      if (_beforeThrows is not null) {
        throw _beforeThrows;
      }
      if (_throwOnBefore) {
        throw new InvalidOperationException("hook blew up");
      }
      return ValueTask.FromResult(_result);
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      _log.Add($"after:{context.Targets.Count}");
      return _afterThrows is not null ? ValueTask.FromException(_afterThrows) : ValueTask.CompletedTask;
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

    public List<(IReadOnlyList<Guid> Ids, DateTimeOffset Until, int Max, Whizbang.Core.Lifecycle.OnDestroyFailure Policy)> Failures { get; } = [];
    public int FailureAttemptToReturn { get; set; } = 1;

    public Task<int> RecordDestructionFailureAsync(IReadOnlyList<Guid> eventIds, DateTimeOffset retryHoldUntil, int maxRetries, Whizbang.Core.Lifecycle.OnDestroyFailure onFailure = Whizbang.Core.Lifecycle.OnDestroyFailure.RetryThenForcedDelete, CancellationToken cancellationToken = default) {
      Failures.Add((eventIds, retryHoldUntil, maxRetries, onFailure));
      return Task.FromResult(FailureAttemptToReturn);
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
  public async Task RunMaintenanceOnce_HookThrows_RecordsFailureForRetry_NoPostDestructionAsync() {
    var log = new List<string>();
    var e1 = Guid.NewGuid();
    var coord = new FakeCoordinator(log) {
      Targets = [new EphemeralDestructionTarget(e1, Guid.NewGuid(), "T")],
      FailureAttemptToReturn = 1,   // first attempt — under the cap
    };

    // A throwing pre-hook must not take down the cycle (the reap still runs), and E2-5 records the failure so
    // the batch is retried instead of failing open.
    await _buildWorker(coord, new RecordingHook(log, throwOnBefore: true)).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(log).Contains("before:1");
    await Assert.That(log).Contains("reap")
      .Because("A PreDestruction hook failure is non-fatal — the maintenance cycle completes.");
    await Assert.That(coord.Failures.Count).IsEqualTo(1)
      .Because("A throwing hook records a destruction failure (retryable) instead of failing open.");
    await Assert.That(coord.Failures[0].Ids).IsEquivalentTo(new[] { e1 });
    await Assert.That(coord.Failures[0].Max).IsEqualTo(5)
      .Because("The default MaxDestructionRetries (5) is passed so the coordinator can force-delete past the cap.");
    await Assert.That(coord.Failures[0].Until).IsGreaterThan(DateTimeOffset.UtcNow)
      .Because("The batch is held for a backoff (now + DestructionRetryBackoffSeconds) so it retries next cycle.");
    await Assert.That(log).DoesNotContain("after:1")
      .Because("PostDestruction does not fire when the pre-hook failed.");
  }

  [Test]
  public async Task RunMaintenanceOnce_HookCancelled_DoesNotRecordADestructionFailureAsync() {
    // The companion to HookThrows_RecordsFailureForRetry. A hook that throws could not judge the
    // batch, so recording a failure is right: the batch retries under a bounded cap and is
    // force-deleted past it. A shutdown judged nothing — counting an attempt spends part of a
    // budget that ends in a forced delete, and a few restarts across a deploy would walk
    // ephemeral bodies toward that end without a hook ever having been consulted.
    var log = new List<string>();
    var coord = new FakeCoordinator(log) {
      Targets = [new EphemeralDestructionTarget(Guid.NewGuid(), Guid.NewGuid(), "T")],
      FailureAttemptToReturn = 1,
    };

    await Assert.That(async () => await _buildWorker(
        coord, new RecordingHook(log, beforeThrows: new OperationCanceledException()))
      .RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("shutdown travels rather than being folded into the batch's retry accounting");
    await Assert.That(coord.Failures).IsEmpty()
      .Because("no hook judged this batch, so none of its retry budget may be spent — the cap ends "
             + "in a forced delete and this would walk it there unattended");
    await Assert.That(log).DoesNotContain("reap")
      .Because("the reap comes after the hook; a stopping host must not run it");
  }

  [Test]
  public async Task RunMaintenanceOnce_PostDestructionHookCancelled_StopsAfterTheReapAsync() {
    // The post-destruction hook runs AFTER the bodies are gone, so by the time a shutdown lands
    // here the destructive work is committed and stopping cannot undo it. What the cancellation
    // buys is the rest of the cycle: the steps that follow take locks and open connections on a
    // host that has asked to stop.
    var log = new List<string>();
    var coord = new FakeCoordinator(log) {
      Targets = [new EphemeralDestructionTarget(Guid.NewGuid(), Guid.NewGuid(), "T")],
    };

    await Assert.That(async () => await _buildWorker(
        coord, new RecordingHook(log, afterThrows: new OperationCanceledException()))
      .RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>();
    await Assert.That(log).Contains("reap")
      .Because("the reap ran before the hook — the cancellation cannot and should not undo it");
    await Assert.That(coord.Failures).IsEmpty()
      .Because("the batch was destroyed successfully; recording a destruction failure for it "
             + "would retry work that is already done");
  }
}
