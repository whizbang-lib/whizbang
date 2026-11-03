// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The maintenance worker publishes the stored-metadata settings on every tick, before the sweep's idle gate (#1725):
/// every write path decides each event's metadata from them, so they must be in force soon after a start even on a
/// service too busy for the sweep. Best-effort: a failed write keeps the stored settings and is logged; cancellation
/// propagates.
/// </summary>
/// <docs>data/event-store#stored-metadata</docs>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class MaintenanceWorkerEventStoreMetadataSyncTests {
  private sealed class SettingsCoordinator(ServiceBacklog? backlog, Exception? metadataSyncThrows = null) : IWorkCoordinator {
    public List<IReadOnlyList<RetentionSetting>> MetadataSyncs { get; } = [];
    public int SweepCount { get; private set; }

    public Task SyncRetentionSettingsAsync(IReadOnlyList<RetentionSetting> settings, CancellationToken cancellationToken = default) {
      if (!settings.Any(s => s.Key == EventStoreMetadataSettings.WRITE_FORM_KEY)) {
        return Task.CompletedTask;
      }
      MetadataSyncs.Add(settings);
      return metadataSyncThrows is null ? Task.CompletedTask : Task.FromException(metadataSyncThrows);
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

  private static MaintenanceWorker _build(SettingsCoordinator coordinator, IOptions<EventStoreMetadataOptions>? options, ILogger<MaintenanceWorker>? logger = null) {
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

  private static Dictionary<string, string> _values(IReadOnlyList<RetentionSetting> settings) =>
    settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);

  [Test]
  public async Task EveryTick_PublishesTheSettings_EvenWhenTheSweepIsDeferredAsync() {
    var busy = new SettingsCoordinator(new ServiceBacklog { UnprocessedInboxRows = 5_000, ActiveLeasedRows = 300 });
    var worker = _build(busy, Options.Create(new EventStoreMetadataOptions { WriteForm = EventStoreMetadataWriteForm.Slim, FullFor = ["MyApp.Payments"] }));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(busy.SweepCount).IsEqualTo(0).Because("control: the service is draining, so the sweep waits");
    await Assert.That(busy.MetadataSyncs.Count).IsEqualTo(2).Because("the write paths need the settings whether or not the sweep runs");
    var values = _values(busy.MetadataSyncs[0]);
    await Assert.That(values[EventStoreMetadataSettings.WRITE_FORM_KEY]).IsEqualTo("Slim");
    await Assert.That(values[EventStoreMetadataSettings.FULL_FOR_KEY]).Contains("MyApp.Payments");
  }

  [Test]
  public async Task NoOptionsRegistered_PublishesTheDefaultsAsync() {
    var coordinator = new SettingsCoordinator(new ServiceBacklog());
    var worker = _build(coordinator, options: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(_values(coordinator.MetadataSyncs.Single())[EventStoreMetadataSettings.WRITE_FORM_KEY]).IsEqualTo("Linked");
  }

  [Test]
  public async Task AFailedWrite_IsLogged_AndTheCycleRunsOnAsync() {
    var coordinator = new SettingsCoordinator(new ServiceBacklog(), new InvalidOperationException("connection refused"));
    var logger = new ListLogger();
    var worker = _build(coordinator, Options.Create(new EventStoreMetadataOptions()), logger);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.SweepCount).IsEqualTo(1).Because("a failed write keeps the stored settings; it is no reason to lose the cycle");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("metadata_write_form=Linked", StringComparison.Ordinal)))
      .IsTrue().Because("the warning names the settings that were not published");
  }

  [Test]
  public async Task OptionsThatFailValidation_AreLogged_AndTheCycleRunsOnAsync() {
    var coordinator = new SettingsCoordinator(new ServiceBacklog());
    var logger = new ListLogger();
    var invalid = new ThrowingOptions();
    var worker = _build(coordinator, invalid, logger);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.MetadataSyncs).IsEmpty();
    await Assert.That(coordinator.SweepCount).IsEqualTo(1);
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("settings not resolved", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task Cancellation_Propagates_FromTheWriteAsync() {
    var coordinator = new SettingsCoordinator(new ServiceBacklog(), new OperationCanceledException());
    var worker = _build(coordinator, Options.Create(new EventStoreMetadataOptions()));

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None)).Throws<OperationCanceledException>();
  }

  private sealed class ThrowingOptions : IOptions<EventStoreMetadataOptions> {
    public EventStoreMetadataOptions Value => throw new OptionsValidationException("x", typeof(EventStoreMetadataOptions), ["StagingDays must be zero or greater"]);
  }
}
