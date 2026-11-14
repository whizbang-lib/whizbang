// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// One failed maintenance task does not skip the tasks after it (#2174).
/// </summary>
/// <remarks>
/// <para>
/// The maintenance call, the stuck-row sentinel, the exhausted-inbox conclusion, the reap-driven snapshots and the
/// destruction read each used to propagate a failure out of the cycle, so every sweep after them was skipped: when the
/// maintenance call timed out on a large backlog, a service ran none of the perspective row sweeps, the
/// lifecycle-completion cleanup, the offload sweep, bloat detection, the sentinel or the registered steps for days.
/// None of them depended on it.
/// </para>
/// <para>
/// Every task of the cycle now runs through one isolation, which logs the failed task by name with its exception and
/// goes on. The one dependency is kept: when the destruction read fails, the reap is given an empty set, so nothing a
/// destruction hook did not see is deleted.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
/// <docs>operations/infrastructure/maintenance#a-failed-task-does-not-stop-the-cycle</docs>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerTaskIsolationTests {
  private const string MAINTENANCE_FAILED = "maintenance call timed out";

  private sealed class Step(List<string> log) : IMaintenanceStep {
    public string Name => "registered-step";
    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
      log.Add("step");
      return Task.CompletedTask;
    }
  }

  private sealed class Hook : IDestructionHook {
    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default)
      => ValueTask.FromResult(DestructionResult.Proceed());
    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default)
      => ValueTask.CompletedTask;
  }

  /// <summary>A coordinator whose chosen calls throw, recording every call it receives.</summary>
  private sealed class FailingCoordinator(List<string> log) : IWorkCoordinator {
    public HashSet<string> Throws { get; } = new(StringComparer.Ordinal);
    public CancellationTokenSource? CancelOnSnapshotRead { get; init; }
    public IReadOnlyList<MaintenanceResult> Results { get; init; } = [];
    public IReadOnlyCollection<Guid>? ReapOnly { get; private set; }
    public bool ReapScoped { get; private set; }

    private Task<T> _call<T>(string name, T result) {
      log.Add(name);
      if (name == "snapshot_pairs" && CancelOnSnapshotRead is { } cts) {
        cts.Cancel();
        return Task.FromException<T>(new OperationCanceledException(cts.Token));
      }
      return Throws.Contains(name) ? Task.FromException<T>(new InvalidOperationException(name + " failed")) : Task.FromResult(result);
    }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default)
      => Throws.Contains("perform_maintenance")
        ? _log(Task.FromException<IReadOnlyList<MaintenanceResult>>(new TimeoutException(MAINTENANCE_FAILED)))
        : _log(Task.FromResult(Results));

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(
        IReadOnlyCollection<Guid>? ephemeralReapEventIds, CancellationToken cancellationToken = default) {
      ReapScoped = true;
      ReapOnly = ephemeralReapEventIds;
      return PerformMaintenanceAsync(cancellationToken);
    }

    private Task<IReadOnlyList<MaintenanceResult>> _log(Task<IReadOnlyList<MaintenanceResult>> task) {
      log.Add("perform_maintenance");
      return task;
    }

    public Task<IReadOnlyList<EphemeralSnapshotTarget>> GetEphemeralPairsNeedingSnapshotAsync(CancellationToken cancellationToken = default)
      => _call<IReadOnlyList<EphemeralSnapshotTarget>>("snapshot_pairs", []);
    public Task<IReadOnlyList<EphemeralDestructionTarget>> GetEphemeralBodiesAboutToReapAsync(CancellationToken cancellationToken = default)
      => _call<IReadOnlyList<EphemeralDestructionTarget>>("destruction_read", []);
    public Task<int> CleanupLifecycleCompletionsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
      => _call("lifecycle_cleanup", 0);
    public Task<IReadOnlyList<TableRewriteCandidate>> GetTablesNeedingRewriteAsync(CancellationToken cancellationToken = default)
      => _call<IReadOnlyList<TableRewriteCandidate>>("bloat_scan", []);
    public Task<IReadOnlyList<StuckRow>> FindStuckOutboxRowsAsync(int maxAttempts, int limit, CancellationToken cancellationToken = default)
      => _call<IReadOnlyList<StuckRow>>("sentinel_outbox", []);
    public Task<IReadOnlyList<StuckRow>> FindStuckInboxRowsAsync(int maxAttempts, int limit, CancellationToken cancellationToken = default)
      => _call<IReadOnlyList<StuckRow>>("stuck_inbox", []);

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  private static (MaintenanceWorker Worker, CapturingLogger<MaintenanceWorker> Logger) _build(
      FailingCoordinator coordinator, List<string> log, bool withHook = false) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IMaintenanceStep>(new Step(log));
    services.AddSingleton(Options.Create(new InboxDispatchWorkerOptions { MaxInboxAttempts = 5 }));
    services.AddSingleton<Whizbang.Core.Perspectives.IPerspectiveRunnerRegistry>(new EmptyRunnerRegistry());
    if (withHook) {
      services.AddSingleton<IDestructionHook>(new Hook());
    }
    var sp = services.BuildServiceProvider();
    var logger = new CapturingLogger<MaintenanceWorker>();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, ConcludeExhaustedInboxRows = true }),
      logger);
    return (worker, logger);
  }

  private sealed class EmptyRunnerRegistry : Whizbang.Core.Perspectives.IPerspectiveRunnerRegistry {
    public Whizbang.Core.Perspectives.IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<Whizbang.Core.Perspectives.PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => new HashSet<LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  /// <summary>For each task that fails, the tasks after it in the cycle that must still have run.</summary>
  private static readonly Dictionary<string, string[]> _laterTasks = new(StringComparer.Ordinal) {
    ["snapshot_pairs"] = ["perform_maintenance", "lifecycle_cleanup", "bloat_scan", "sentinel_outbox", "step"],
    ["perform_maintenance"] = ["lifecycle_cleanup", "bloat_scan", "sentinel_outbox", "stuck_inbox", "step"],
    ["sentinel_outbox"] = ["stuck_inbox", "step"],
    ["stuck_inbox"] = ["step"],
  };

  [Test]
  [Arguments("snapshot_pairs")]
  [Arguments("perform_maintenance")]
  [Arguments("sentinel_outbox")]
  [Arguments("stuck_inbox")]
  public async Task AFailedTask_IsLoggedByName_AndEveryLaterTaskStillRunsAsync(string failed) {
    var log = new List<string>();
    var coordinator = new FailingCoordinator(log);
    coordinator.Throws.Add(failed);
    var (worker, logger) = _build(coordinator, log);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(log).Contains(failed);
    foreach (var later in _laterTasks[failed]) {
      await Assert.That(log).Contains(later).Because($"{later} does not depend on {failed}, so its failure must not skip it");
    }
    var warnings = logger.Snapshot().Where(e => e.Level == LogLevel.Warning && e.Exception is not null).ToList();
    await Assert.That(warnings).IsNotEmpty().Because("what failed is logged with its exception, never swallowed silently");
    await Assert.That(warnings.All(w => w.Exception!.Message.StartsWith(failed, StringComparison.Ordinal)
                                     || w.Exception.Message == MAINTENANCE_FAILED)).IsTrue();
  }

  [Test]
  public async Task TheMaintenanceCallFailing_NamesItInTheWarningAsync() {
    var log = new List<string>();
    var coordinator = new FailingCoordinator(log);
    coordinator.Throws.Add("perform_maintenance");
    var (worker, logger) = _build(coordinator, log);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    var warning = logger.Snapshot().Single(e => e.Level == LogLevel.Warning && e.Exception is not null);
    await Assert.That(warning.Message).Contains("perform_maintenance");
    await Assert.That(warning.Exception!.Message).IsEqualTo(MAINTENANCE_FAILED);
  }

  [Test]
  public async Task TheDestructionReadFailing_GivesTheReapNothing_AndTheCycleGoesOnAsync() {
    var log = new List<string>();
    var coordinator = new FailingCoordinator(log);
    coordinator.Throws.Add("destruction_read");
    var (worker, logger) = _build(coordinator, log, withHook: true);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.ReapScoped).IsTrue().Because("the reap is told which bodies it may delete");
    await Assert.That(coordinator.ReapOnly!.Count).IsEqualTo(0)
      .Because("a body the destruction hook did not see is never reaped: the hook runs before the delete");
    await Assert.That(log).Contains("lifecycle_cleanup");
    await Assert.That(log).Contains("step");
    await Assert.That(logger.Snapshot().Single(e => e.Level == LogLevel.Warning && e.Exception is not null).Message)
      .Contains("ephemeral_destruction_hooks");
  }

  [Test]
  public async Task ATaskTheDatabaseReportsFailed_IsLoggedAsAWarningByNameAsync() {
    var log = new List<string>();
    var coordinator = new FailingCoordinator(log) {
      Results = [
        new MaintenanceResult("purge_completed_outbox", 3, 1.5, "ok"),
        new MaintenanceResult("purge_old_deduplication", 0, 2.5, "failed: canceling statement due to lock timeout"),
      ],
    };
    var (worker, logger) = _build(coordinator, log);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    var warnings = logger.Snapshot().Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1).Because("only the failed task is a warning; the ok one is information");
    await Assert.That(warnings[0]).Contains("purge_old_deduplication");
    await Assert.That(warnings[0]).Contains("canceling statement due to lock timeout");
  }

  [Test]
  public async Task Cancellation_StillStopsTheCycleAsync() {
    var log = new List<string>();
    using var cts = new CancellationTokenSource();
    var coordinator = new FailingCoordinator(log) { CancelOnSnapshotRead = cts };
    var (worker, _) = _build(coordinator, log);

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(cts.Token)).Throws<OperationCanceledException>()
      .Because("shutdown is not a task failure: it propagates instead of being logged and skipped past");
    await Assert.That(log).DoesNotContain("perform_maintenance");
  }
}
