// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The maintenance cycle publishes every configured retention setting in one write, before the sweep reads them.
/// </summary>
/// <remarks>
/// <para>
/// The write rides the cycle that already publishes <c>debug_mode</c>, so a configuration change takes effect
/// on the next cycle without a restart, and no new connection or timer is added. It is best-effort: the sweep
/// decides from what is stored, so a failed write means it keeps the previous values, which is never a reason to
/// lose the cycle. Cancellation is the exception; it is a host stopping.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IWorkCoordinator.cs</code-under-test>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerRetentionSyncTests {

  private static readonly string[] _syncThenSweep = ["sync", "maintenance"];
  private static readonly string[] _debugAndStuckInbox = ["debug_mode", "stuck_inbox_retention_days", "partition_maintenance_enabled"];

  private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

  private sealed class CapturingLogger : ILogger<MaintenanceWorker> {
    private readonly List<LogEntry> _entries = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_entries) { _entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception)); }
    }
    public List<LogEntry> Snapshot() { lock (_entries) { return [.. _entries]; } }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  /// <summary>A coordinator that overrides the retention sync, as both Postgres coordinators do.</summary>
  private class SyncingCoordinator : IWorkCoordinator {
    public Exception? SyncThrows { get; init; }
    public List<IReadOnlyList<RetentionSetting>> Syncs { get; } = [];
    public int DebugSyncCalls;
    public int MaintenanceCalls;

    public virtual Task SyncRetentionSettingsAsync(IReadOnlyList<RetentionSetting> settings, CancellationToken cancellationToken = default) {
      Syncs.Add(settings);
      return SyncThrows is not null ? Task.FromException(SyncThrows) : Task.CompletedTask;
    }

    public Task SyncDebugRetentionSettingAsync(bool debugMode, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref DebugSyncCalls);
      return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref MaintenanceCalls);
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }

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

  /// <summary>A coordinator that implements only the older debug-only sync, as a third-party engine might.</summary>
  private sealed class DebugOnlyCoordinator : IWorkCoordinator {
    public List<bool> DebugSyncs { get; } = [];

    public Task SyncDebugRetentionSettingAsync(bool debugMode, CancellationToken cancellationToken = default) {
      DebugSyncs.Add(debugMode);
      return Task.CompletedTask;
    }

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

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(
      IWorkCoordinator coord, MaintenanceWorkerOptions options, EphemeralOptions? ephemeral, bool debugMode = false) {
    var services = new ServiceCollection();
    services.AddSingleton(coord);
    services.AddSingleton(Options.Create(new WorkCoordinatorOptions { DebugMode = debugMode }));
    if (ephemeral is not null) {
      services.AddSingleton(Options.Create(ephemeral));
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new CapturingLogger();
    var worker = new MaintenanceWorker(sp.GetRequiredService<IServiceScopeFactory>(), gate, Options.Create(options), logger);
    return (worker, logger);
  }

  [Test]
  public async Task EachCycle_PublishesTheConfiguredValuesInOneWriteAsync() {
    var coord = new SyncingCoordinator();
    var options = new MaintenanceWorkerOptions { Retention = new MaintenanceRetentionOptions { DeduplicationDays = 3 } };
    var ephemeral = new EphemeralOptions { PointerPrune = new EphemeralPointerPruneOptions { Enabled = true } };
    var (worker, _) = _build(coord, options, ephemeral, debugMode: true);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Syncs.Count).IsEqualTo(2)
      .Because("published every cycle, so a configuration change lands without a restart and a stale value cannot outlive its option");
    var written = coord.Syncs[0].ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
    await Assert.That(written).IsEquivalentTo(new Dictionary<string, string> {
      ["debug_mode"] = "true",
      ["dedup_retention_days"] = "3",
      ["ephemeral_deep_maintenance_enabled"] = "true",
      ["partition_maintenance_enabled"] = "false",
    });
    await Assert.That(coord.DebugSyncCalls).IsEqualTo(0)
      .Because("debug_mode travels in the same single write; a second statement for it would be the duplicate this replaces");
  }

  [Test]
  public async Task TheWrite_HappensBeforeTheSweepReadsItAsync() {
    var order = new List<string>();
    var coord = new OrderRecordingCoordinator(order);
    var (worker, _) = _build(coord, new MaintenanceWorkerOptions(), new EphemeralOptions());

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(order).IsEquivalentTo(_syncThenSweep, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the sweep reads the stored values; publishing after it would apply each change one cycle late");
  }

  private sealed class OrderRecordingCoordinator(List<string> order) : SyncingCoordinator {
    public override Task SyncRetentionSettingsAsync(IReadOnlyList<RetentionSetting> settings, CancellationToken cancellationToken = default) {
      order.Add("sync");
      return Task.CompletedTask;
    }
    public override Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
      order.Add("maintenance");
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }
  }

  [Test]
  public async Task WithNoEphemeralOptionsRegistered_OnlyTheMaintenanceValuesArePublishedAsync() {
    var coord = new SyncingCoordinator();
    var options = new MaintenanceWorkerOptions { Retention = new MaintenanceRetentionOptions { StuckInboxDays = 2 } };
    var (worker, _) = _build(coord, options, ephemeral: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Syncs.Single().Select(s => s.Key).ToArray())
      .IsEquivalentTo(_debugAndStuckInbox)
      .Because("a host that never registered the ephemeral options has no opinion on those keys");
  }

  [Test]
  [Arguments(true, "true")]
  [Arguments(false, "false")]
  public async Task EachCycle_PublishesWhetherPartitionMaintenanceIsOnAsync(bool enabled, string literal) {
    var coord = new SyncingCoordinator();
    var options = new MaintenanceWorkerOptions { Partitioning = new MaintenancePartitioningOptions { Enabled = enabled } };
    var (worker, _) = _build(coord, options, ephemeral: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Syncs.Single().Single(s => s.Key == "partition_maintenance_enabled").Value).IsEqualTo(literal)
      .Because("the row sweep reads it to leave a partitioned wh_log to the partition drop, and takes it back when this is off");
  }

  [Test]
  public async Task AFailedWrite_IsLoggedWithTheKeysAndTheCycleContinuesAsync() {
    var coord = new SyncingCoordinator { SyncThrows = new InvalidOperationException("settings write failed") };
    var options = new MaintenanceWorkerOptions { Retention = new MaintenanceRetentionOptions { DeduplicationDays = 3 } };
    var (worker, logger) = _build(coord, options, new EphemeralOptions());

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.MaintenanceCalls).IsEqualTo(1)
      .Because("the sweep keeps the values already stored; a settings write is never a reason to lose the cycle");
    var warning = logger.Snapshot().Single(e => e.Exception is InvalidOperationException);
    await Assert.That(warning.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(warning.Message).Contains("debug_mode, dedup_retention_days")
      .Because("the log says WHAT failed to publish, so the operator knows which windows are still the old ones");
  }

  [Test]
  public async Task InvalidEphemeralOptions_AreLoggedAndTheCycleContinuesAsync() {
    var coord = new SyncingCoordinator();
    var ephemeral = new EphemeralOptions { PointerPrune = null! };
    var (worker, logger) = _build(coord, new MaintenanceWorkerOptions(), ephemeral);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.MaintenanceCalls).IsEqualTo(1);
    await Assert.That(coord.Syncs).IsEmpty();
    var warning = logger.Snapshot().Single(e => e.Exception is ArgumentNullException);
    await Assert.That(warning.Message).Contains("not resolved")
      .Because("no key was written, and the log must not claim a list it never built");
  }

  [Test]
  public async Task ACanceledWrite_PropagatesAsync() {
    var coord = new SyncingCoordinator { SyncThrows = new OperationCanceledException() };
    var (worker, logger) = _build(coord, new MaintenanceWorkerOptions(), new EphemeralOptions());

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("a canceled step is a stopping host; continuing would run a sweep the host is trying to stop");
    await Assert.That(coord.MaintenanceCalls).IsEqualTo(0);
    await Assert.That(logger.Snapshot().Any(e => e.Level == LogLevel.Warning)).IsFalse();
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AnEngineThatOnlySyncsDebugMode_StillReceivesItAsync(bool debugMode) {
    var coord = new DebugOnlyCoordinator();
    var (worker, _) = _build(coord, new MaintenanceWorkerOptions {
      Retention = new MaintenanceRetentionOptions { DeduplicationDays = 9 },
    }, new EphemeralOptions(), debugMode);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.DebugSyncs).IsEquivalentTo([debugMode])
      .Because("the default implementation hands debug_mode to the older method, so an engine written against it keeps "
             + "its debug retention instead of silently losing it");
  }

  [Test]
  public async Task TheDefaultSync_WithNoDebugEntry_DoesNothingAsync() {
    IWorkCoordinator coord = new DebugOnlyCoordinator();

    await coord.SyncRetentionSettingsAsync([new RetentionSetting("dedup_retention_days", "3", "integer", "x")]);

    await Assert.That(((DebugOnlyCoordinator)coord).DebugSyncs).IsEmpty()
      .Because("absent is not false; writing false would switch off debug retention nobody asked to change");
  }
}
