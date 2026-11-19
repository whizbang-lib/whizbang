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
/// The maintenance worker publishes the per-perspective policies for rows of archived streams on every tick, before the
/// sweep's idle gate (#1739): the archive reads them inside the database, where this process's options cannot reach, so
/// they must be in force soon after a start even on a service too busy for the sweep. Best-effort: a failed write keeps
/// the stored policies and is logged; cancellation propagates.
/// </summary>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class MaintenanceWorkerArchivedStreamRowsSyncTests {
  private sealed class SettingsCoordinator(ServiceBacklog? backlog, Exception? rowPolicySyncThrows = null) : IWorkCoordinator {
    public List<RetentionSetting> RowPolicySyncs { get; } = [];
    public int SweepCount { get; private set; }

    public Task SyncRetentionSettingsAsync(IReadOnlyList<RetentionSetting> settings, CancellationToken cancellationToken = default) {
      var policies = settings.Where(s => s.Key == ArchivedStreamRowPolicies.SETTING_KEY).ToList();
      if (policies.Count == 0) {
        return Task.CompletedTask;
      }
      RowPolicySyncs.AddRange(policies);
      return rowPolicySyncThrows is null ? Task.CompletedTask : Task.FromException(rowPolicySyncThrows);
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

  private sealed class ThrowingOptions : IOptions<ArchivedStreamRowsOptions> {
    public ArchivedStreamRowsOptions Value => throw new InvalidOperationException("Whizbang:Perspectives:ArchivedStreamRows:X is 'Shred'");
  }

  private static MaintenanceWorker _build(SettingsCoordinator coordinator, IOptions<ArchivedStreamRowsOptions>? options, ILogger<MaintenanceWorker>? logger = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    if (options is not null) {
      services.AddSingleton(options);
    }
    var sp = services.BuildServiceProvider();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      logger ?? NullLogger<MaintenanceWorker>.Instance, metrics: null,
      housekeeping: new HousekeepingCoordinator(new HousekeepingCoordinator.Settings { SettledCooldown = TimeSpan.Zero }));
  }

  private static ArchivedStreamRowsOptions _configured(string name, ArchivedStreamRowPolicy policy) {
    var options = new ArchivedStreamRowsOptions();
    options.Perspectives[name] = policy;
    return options;
  }

  [Test]
  public async Task EveryTick_PublishesThePolicies_EvenWhenTheSweepIsDeferredAsync() {
    var name = $"Tests.ArchivedRows.Tick+{Guid.NewGuid():N}";
    var busy = new SettingsCoordinator(new ServiceBacklog { UnprocessedInboxRows = 5_000, ActiveLeasedRows = 300 });
    var worker = _build(busy, Options.Create(_configured(name, ArchivedStreamRowPolicy.Drop)));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(busy.SweepCount).IsEqualTo(0).Because("control: the service is draining, so the sweep waits");
    await Assert.That(busy.RowPolicySyncs.Count).IsEqualTo(2).Because("an archive can run at any time, so the policies are published whether or not the sweep runs");
    await Assert.That(busy.RowPolicySyncs[0].Value.Split('\n')).Contains($"{name}=Drop");
  }

  [Test]
  public async Task NoOptionsRegistered_PublishesTheDeclaredPoliciesOnlyAsync() {
    var declared = $"Tests.ArchivedRows.Declared+{Guid.NewGuid():N}";
    ArchivedStreamRowPolicies.Register(declared, ArchivedStreamRowPolicy.Archive);
    var coordinator = new SettingsCoordinator(new ServiceBacklog());
    var worker = _build(coordinator, options: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.RowPolicySyncs.Single().Value.Split('\n')).Contains($"{declared}=Archive");
  }

  [Test]
  public async Task AFailedWrite_IsLogged_AndTheCycleRunsOnAsync() {
    var name = $"Tests.ArchivedRows.Failed+{Guid.NewGuid():N}";
    var coordinator = new SettingsCoordinator(new ServiceBacklog(), new InvalidOperationException("connection refused"));
    var logger = new ListLogger();
    var worker = _build(coordinator, Options.Create(_configured(name, ArchivedStreamRowPolicy.Drop)), logger);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.SweepCount).IsEqualTo(1).Because("a failed write keeps the stored policies; it is no reason to lose the cycle");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains($"{name}=Drop", StringComparison.Ordinal)))
      .IsTrue().Because("the warning names the policies that were not stored");
  }

  [Test]
  public async Task OptionsThatCannotBeBound_AreLogged_AndTheCycleRunsOnAsync() {
    var coordinator = new SettingsCoordinator(new ServiceBacklog());
    var logger = new ListLogger();
    var worker = _build(coordinator, new ThrowingOptions(), logger);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.RowPolicySyncs).IsEmpty();
    await Assert.That(coordinator.SweepCount).IsEqualTo(1);
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("policies not resolved", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task Cancellation_Propagates_FromTheWriteAsync() {
    var coordinator = new SettingsCoordinator(new ServiceBacklog(), new OperationCanceledException());
    var worker = _build(coordinator, Options.Create(new ArchivedStreamRowsOptions()));

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None)).Throws<OperationCanceledException>();
  }
}
