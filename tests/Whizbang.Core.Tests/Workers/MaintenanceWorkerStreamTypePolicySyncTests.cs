// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The maintenance worker stores the configured append-to-archived policies on every tick, before the sweep's idle gate
/// (#1738): a background write to an archived stream is refused inside the store, which reads the stored policy, so a
/// policy has to be in force soon after a start even on a service too busy for the sweep. Best-effort: a failed write
/// keeps the stored set and is logged; cancellation propagates.
/// </summary>
/// <docs>fundamentals/events/archive-store#the-policy-per-stream-type</docs>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class MaintenanceWorkerStreamTypePolicySyncTests {
  private sealed class PolicyCoordinator(ServiceBacklog? backlog, Exception? syncThrows = null) : IWorkCoordinator {
    public List<IReadOnlyList<StreamTypePolicy>> Syncs { get; } = [];
    public int SweepCount { get; private set; }

    public Task SyncStreamTypePoliciesAsync(IReadOnlyList<StreamTypePolicy> policies, CancellationToken cancellationToken = default) {
      Syncs.Add(policies);
      return syncThrows is null ? Task.CompletedTask : Task.FromException(syncThrows);
    }

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(backlog);

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
      SweepCount++;
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class ListLogger : ILogger<MaintenanceWorker> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private static MaintenanceWorker _build(PolicyCoordinator coordinator, StreamLifecycleOptions lifecycle, ILogger<MaintenanceWorker>? logger = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton(Options.Create(lifecycle));
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), gate, Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      logger ?? NullLogger<MaintenanceWorker>.Instance, metrics: null,
      housekeeping: new HousekeepingCoordinator(new HousekeepingCoordinator.Settings { SettledCooldown = TimeSpan.Zero }));
  }

  private static StreamLifecycleOptions _lifecycle() {
    var options = new StreamLifecycleOptions();
    options.Types["Shop.Order"] = new StreamTypeLifecycleOptions { AppendToArchived = AppendToArchivedPolicy.AutoRestore };
    return options;
  }

  [Test]
  public async Task EveryTick_StoresTheConfiguredPolicies_EvenWhenTheSweepIsDeferredAsync() {
    var busy = new PolicyCoordinator(new ServiceBacklog { UnprocessedInboxRows = 5_000, ActiveLeasedRows = 300 });
    var worker = _build(busy, _lifecycle());

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(busy.SweepCount).IsEqualTo(0).Because("the service is draining, so the sweep waits");
    await Assert.That(busy.Syncs.Count).IsEqualTo(2)
      .Because("the policies are stored on every tick, ahead of the idle gate, so a busy service still applies them");
    await Assert.That(busy.Syncs[0]).IsEquivalentTo([new StreamTypePolicy("Shop.Order", AppendToArchivedPolicy.AutoRestore)]);
  }

  [Test]
  public async Task NoTypesConfigured_StoresAnEmptySet_SoAStaleRowIsRemovedAsync() {
    var coordinator = new PolicyCoordinator(new ServiceBacklog());
    var worker = _build(coordinator, new StreamLifecycleOptions());

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Syncs).Count().IsEqualTo(1);
    await Assert.That(coordinator.Syncs[0]).IsEmpty()
      .Because("an empty configuration is a configuration: a type someone removed must stop restoring");
  }

  [Test]
  public async Task AFailedWrite_IsLogged_AndTheCycleRunsOnAsync() {
    var coordinator = new PolicyCoordinator(new ServiceBacklog(), new InvalidOperationException("connection refused"));
    var logger = new ListLogger();
    var worker = _build(coordinator, _lifecycle(), logger);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.SweepCount).IsEqualTo(1).Because("a failed policy write keeps the stored set; it is no reason to lose the cycle");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("Shop.Order", StringComparison.Ordinal)))
      .IsTrue().Because("the warning names the types whose policy was not stored");
  }

  [Test]
  public async Task Cancellation_Propagates_FromThePolicyWriteAsync() {
    var coordinator = new PolicyCoordinator(new ServiceBacklog(), new OperationCanceledException());
    var worker = _build(coordinator, _lifecycle());

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None)).Throws<OperationCanceledException>();
  }
}
