using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Offloads;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage-round-23 gap fill for <see cref="MaintenanceWorker"/>: the outer loop's cancellation
/// and continuation edges, the housekeeping forced-through log, the lifecycle-purge log, the
/// offload sweep's per-cycle batch ceiling, the stream-group cascade's empty-cascade and
/// missing-table skips, and the post-destruction hook's independent (and here, absent) second
/// resolution.
/// </summary>
public class MaintenanceWorkerCoverageTests {

  // PerspectiveStreamGroupRegistry is a process-wide static, shared with other test classes (e.g.
  // MaintenanceWorkerStreamGroupCascadeTests) that also serialize on the same NotInParallel key.
  // Only the two cascade tests below touch it, so the clear is scoped to those tests directly
  // (inside their [NotInParallel("PerspectiveStreamGroupRegistry")] window) rather than hung off
  // class-wide Before/After hooks — a class-wide clear would run for every test in this file,
  // including ones with no such key, racing against another class's registry mutations.

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

  /// <summary>A gate whose wait is already canceled the instant it is awaited, with no real delay
  /// or dependence on the caller's token — the deterministic way to exercise a cancellation that
  /// arrives while still waiting on schema readiness, without racing a real cancel against it.</summary>
  private sealed class ImmediatelyCanceledGate : ISchemaReadyGate {
    public bool IsReady => false;
    public Task WaitForReadyAsync(CancellationToken cancellationToken) => Task.FromCanceled(new CancellationToken(true));
    public void MarkReady() { }
  }

  /// <summary>
  /// Minimal <see cref="IWorkCoordinator"/> covering only the calls these tests drive; every other
  /// member relies on the interface's own safe no-op default. Hooks are optional so one fake serves
  /// several scenarios without per-test subclassing.
  /// </summary>
  private sealed class StubCoordinator : IWorkCoordinator {
    private int _syncCalls;
    private int _performCalls;

    public Func<int, Task>? SyncDebugRetentionSettingHook { get; init; }
    public Action<int>? OnPerformMaintenanceCall { get; init; }
    public ServiceBacklog? Backlog { get; init; }
    public int CleanupLifecycleCompletionsResult { get; init; }
    public List<EphemeralDestructionTarget> EphemeralBodiesAboutToReap { get; init; } = [];

    public int SyncCallCount => _syncCalls;
    public int PerformMaintenanceCallCount => _performCalls;
    public TimeSpan? CleanupLifecycleCompletionsRetentionSeen { get; private set; }

    public Task SyncDebugRetentionSettingAsync(bool debugMode, CancellationToken ct = default) {
      var n = Interlocked.Increment(ref _syncCalls);
      return SyncDebugRetentionSettingHook?.Invoke(n) ?? Task.CompletedTask;
    }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default) {
      var n = Interlocked.Increment(ref _performCalls);
      OnPerformMaintenanceCall?.Invoke(n);
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken ct = default)
      => ValueTask.FromResult(Backlog);

    public Task<int> CleanupLifecycleCompletionsAsync(TimeSpan retentionPeriod, CancellationToken ct = default) {
      CleanupLifecycleCompletionsRetentionSeen = retentionPeriod;
      return Task.FromResult(CleanupLifecycleCompletionsResult);
    }

    public Task<IReadOnlyList<EphemeralDestructionTarget>> GetEphemeralBodiesAboutToReapAsync(CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<EphemeralDestructionTarget>>(EphemeralBodiesAboutToReap);

    // Abstract members with no interface default.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default)
      => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(
        IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default)
      => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  // ============================================================
  // ExecuteAsync's own loop — cancellation vs. continuation edges
  // ============================================================

  [Test]
  [NotInParallel("WhizbangBackgroundServiceTests")]
  public async Task ExecuteAsync_SchemaGateWaitCanceled_StopsWithoutEnteringTheLoopAsync() {
    // If a cancellation that arrives while still waiting on schema readiness were swallowed into
    // the maintenance loop instead of stopping the worker, the very first cycle would run SQL
    // against a schema nobody has confirmed exists yet.
    var coord = new StubCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new ImmediatelyCanceledGate(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);

    await worker.StartAsync(CancellationToken.None);

    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue()
      .Because("the wait was canceled, not a tick failing, so the worker must exit promptly rather than hang");
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("a canceled readiness wait is a clean stop, not a fault");
    await Assert.That(coord.PerformMaintenanceCallCount).IsEqualTo(0)
      .Because("no maintenance cycle may run before schema readiness is confirmed");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [NotInParallel("WhizbangBackgroundServiceTests")]
  public async Task ExecuteAsync_CycleThrowsOperationCanceled_BreaksTheLoopInsteadOfRetryingAsync() {
    // A cycle torn down by cancellation (host shutdown mid-cycle) must stop the loop immediately.
    // If this were ever folded into the generic "log and retry next interval" path instead, a
    // shutting-down host would keep opening scopes and hitting the store on every interval while
    // it was already on its way out.
    using var cts = new CancellationTokenSource();
    var coord = new StubCoordinator {
      SyncDebugRetentionSettingHook = n => {
        if (n == 1) {
          return Task.FromException(new OperationCanceledException("simulated mid-cycle shutdown"));
        }
        // Safety valve only: correct behavior never reaches a second call, since the break must
        // fire after the first. If it ever does, stop the loop rather than spin.
        cts.Cancel();
        return Task.CompletedTask;
      },
    };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var logger = new CapturingLogger();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 0 }),
      logger);

    await worker.StartAsync(cts.Token);

    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue();
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse();
    await Assert.That(coord.SyncCallCount).IsEqualTo(1)
      .Because("the loop must break on the first cancellation rather than looping back for a second cycle");
    await Assert.That(logger.Snapshot().Any(e => e.EventId.Id == 4)).IsFalse()
      .Because("a shutdown-driven cancellation is not a tick failure and must not be logged as one");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [NotInParallel("WhizbangBackgroundServiceTests")]
  public async Task ExecuteAsync_CycleCompletesWithoutCancellation_LoopsBackForAnotherCycleAsync() {
    // A maintenance worker that quietly stopped after its first tick would never again reclaim
    // abandoned streams, dead letters, or stale rows for the rest of the process's life — silently,
    // since nothing throws. The loop has to actually run more than once when nothing cancels it.
    using var cts = new CancellationTokenSource();
    var coord = new StubCoordinator {
      OnPerformMaintenanceCall = n => {
        if (n == 2) {
          cts.Cancel();
        }
      },
    };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 0 }),
      NullLogger<MaintenanceWorker>.Instance);

    await worker.StartAsync(cts.Token);

    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue();
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse();
    await Assert.That(coord.PerformMaintenanceCallCount).IsEqualTo(2)
      .Because("a cycle that finished cleanly must loop back for another — stopping after one tick "
             + "would silently disable all periodic housekeeping for the rest of the process");

    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Housekeeping deferral-limit force-through
  // ============================================================

  [Test]
  public async Task RunMaintenanceOnceAsync_ServiceNeverSettles_ForcesTheSweepAndLogsItDistinctlyAsync() {
    // Cleanup has no deadline but it does have a limit: a permanently busy service must still get
    // its sweep, or space is never reclaimed. If the forced pass were not reported distinctly from
    // an ordinary settled run, an operator would have no way to notice a service that never once
    // went quiet.
    var logger = new CapturingLogger();
    var coord = new StubCoordinator {
      Backlog = new ServiceBacklog { UnprocessedInboxRows = 41, ActiveLeasedRows = 9 },
    };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var housekeeping = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings { MaxConsecutiveDeferrals = 0 });
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      logger,
      metrics: null,
      housekeeping: housekeeping);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.PerformMaintenanceCallCount).IsEqualTo(1)
      .Because("a deferral-limit force-through must still actually run the sweep, not just log about it");
    var forced = logger.Snapshot().Where(e => e.EventId.Id == 48).ToList();
    await Assert.That(forced).IsNotEmpty()
      .Because("the forced-through path has its own event id, distinct from a settled or a deferred run");
    await Assert.That(forced[0].Message).Contains("41");
    await Assert.That(forced[0].Message).Contains("9");
  }

  // ============================================================
  // Lifecycle-completion purge count logging
  // ============================================================

  [Test]
  public async Task RunMaintenanceOnceAsync_LifecyclePurgeRemovesRows_LogsHowManyAsync() {
    // wh_lifecycle_completions grows by one row per event forever unless this sweep runs. If rows
    // were actually purged but nothing said how many, an operator watching the logs after enabling
    // retention would have no way to confirm the sweep is doing anything at all.
    var logger = new CapturingLogger();
    var coord = new StubCoordinator { CleanupLifecycleCompletionsResult = 12 };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, LifecycleCompletionRetentionDays = 30 }),
      logger);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.CleanupLifecycleCompletionsRetentionSeen).IsEqualTo(TimeSpan.FromDays(30));
    var purged = logger.Snapshot().Where(e => e.EventId.Id == 51).ToList();
    await Assert.That(purged).IsNotEmpty()
      .Because("a purge that removed rows must be visible in the logs, not just the empty-case silence");
    await Assert.That(purged[0].Message).Contains("12");
    await Assert.That(purged[0].Message).Contains("30");
  }

  // ============================================================
  // Offload sweep: a full batch exactly at the per-cycle ceiling
  // ============================================================

  private sealed class OffloadCoordinator : IWorkCoordinator {
    public List<OffloadClaimRecord> Batch { get; init; } = [];
    public List<string> Removed { get; } = [];
    public int ScanCalls { get; private set; }

    public Task<bool> TryClaimOffloadSweepAsync(TimeSpan claimWindow, CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<OffloadClaimRecord>> GetExpiredOffloadClaimsAsync(
        TimeSpan olderThan, int batchSize, CancellationToken ct = default) {
      ScanCalls++;
      return Task.FromResult<IReadOnlyList<OffloadClaimRecord>>(Batch);
    }

    public Task RemoveOffloadClaimsAsync(IReadOnlyCollection<string> storageKeys, CancellationToken ct = default) {
      lock (Removed) { Removed.AddRange(storageKeys); }
      return Task.CompletedTask;
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default)
      => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(
        IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default)
      => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  private sealed class RecordingStore(string providerName) : IMessageBodyStore {
    public string ProviderName => providerName;
    public List<string> Deleted { get; } = [];

    public Task DeleteAsync(MessageBodyClaim claim, MessageBodyDeleteOptions? options = null, CancellationToken ct = default) {
      lock (Deleted) { Deleted.Add(claim.StorageKey); }
      return Task.CompletedTask;
    }

    public Task<MessageBodyClaim> UploadAsync(
        ReadOnlyMemory<byte> body, string contentType, MessageBodyUploadOptions? options = null,
        CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ReadOnlyMemory<byte>> DownloadAsync(
        MessageBodyClaim claim, MessageBodyDownloadOptions? options = null,
        CancellationToken ct = default) => throw new NotImplementedException();
  }

  private sealed class StaticOptionsMonitor(MessageBodyOffloadOptions value) : IOptionsMonitor<MessageBodyOffloadOptions> {
    public MessageBodyOffloadOptions CurrentValue => value;
    public MessageBodyOffloadOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<MessageBodyOffloadOptions, string?> listener) => null;
  }

  [Test]
  public async Task RunMaintenanceOnceAsync_OffloadBatchExactlyFillsTheCycleCeiling_StillDeletesTheBatchAsync() {
    // A batch that exactly reaches the per-cycle batch ceiling exercises the loop's normal
    // continue path rather than either early-exit condition (nothing left, or a partial batch). If
    // that path silently dropped the batch instead of processing it, blobs sitting exactly at the
    // ceiling would leak forever while the sweep kept reporting no errors.
    var coord = new OffloadCoordinator { Batch = [new OffloadClaimRecord("k1", "blob")] };
    var store = new RecordingStore("blob");
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IOptionsMonitor<MessageBodyOffloadOptions>>(
      new StaticOptionsMonitor(new MessageBodyOffloadOptions {
        PassiveExpiry = TimeSpan.FromHours(1),
        PassiveSweepBatchSize = 1,
        PassiveSweepMaxBatchesPerCycle = 1,
      }));
    services.AddKeyedSingleton<IMessageBodyStore>("blob", store);
    var sp = services.BuildServiceProvider();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.ScanCalls).IsEqualTo(1)
      .Because("the ceiling was reached on the very first batch, so the sweep must never scan a second time this cycle");
    await Assert.That(store.Deleted).Contains("k1")
      .Because("a full batch landing exactly on the ceiling must still be processed, not silently skipped");
    await Assert.That(coord.Removed).Contains("k1")
      .Because("the ledger row must be forgotten once its blob is actually gone, or the same claim re-appears every cycle");
  }

  // ============================================================
  // Stream-group cascade: empty cascade, and a target with no table
  // ============================================================

  private sealed class AnnouncerModel;
  private sealed class FollowerModel;
  private const string ANNOUNCER_TABLE = "wh_per_coverage_announcer";
  private const string FOLLOWER_TABLE = "wh_per_coverage_follower";

  private sealed class CascadeCoordinator : IWorkCoordinator {
    public List<PerspectiveRowRef> Journal { get; init; } = [];
    public List<PerspectiveTableName> Tables { get; init; } = [];
    public List<(string Table, IReadOnlyCollection<Guid> Ids)> Deleted { get; } = [];

    public Task<IReadOnlyList<PerspectiveRowRef>> DrainRowEvictionJournalAsync(int limit = 1000, CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<PerspectiveRowRef>>(Journal);

    public Task<IReadOnlyList<PerspectiveTableName>> GetPerspectiveTableNamesAsync(
        IReadOnlyCollection<string> clrTypeNames, CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<PerspectiveTableName>>(Tables);

    public Task<int> CascadeDeletePerspectiveRowsAsync(string tableName, IReadOnlyCollection<Guid> rowIds, CancellationToken ct = default) {
      lock (Deleted) { Deleted.Add((tableName, rowIds)); }
      return Task.FromResult(rowIds.Count);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default)
      => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(
        IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default)
      => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  private static MaintenanceWorker _buildCascadeWorker(CascadeCoordinator coord) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  [Test]
  [NotInParallel("PerspectiveStreamGroupRegistry")]
  public async Task RunMaintenanceOnceAsync_EvictionSeedIsAFollowerNotAnAnnouncer_ProducesNoCascadeAsync() {
    // A follower-only membership does not announce its own evictions — only an announcer's
    // own-origin eviction re-announces. If the empty-cascade case ever started deleting anyway, a
    // follower row being cleaned up on its own would incorrectly cascade to unrelated siblings that
    // never lost anything.
    PerspectiveStreamGroupRegistry.Clear();
    try {
      PerspectiveStreamGroupRegistry.Register(typeof(AnnouncerModel), "coverage-group", announce: true, follow: false, bridge: false);
      PerspectiveStreamGroupRegistry.Register(typeof(FollowerModel), "coverage-group", announce: false, follow: true, bridge: false);
      var coord = new CascadeCoordinator {
        Journal = { new PerspectiveRowRef(FOLLOWER_TABLE, Guid.CreateVersion7()) },
        Tables = [
          new(typeof(AnnouncerModel).FullName!, ANNOUNCER_TABLE),
          new(typeof(FollowerModel).FullName!, FOLLOWER_TABLE),
        ],
      };
      var worker = _buildCascadeWorker(coord);

      await worker.RunMaintenanceOnceAsync(CancellationToken.None);

      await Assert.That(coord.Deleted).IsEmpty()
        .Because("a follower-only eviction has nothing to announce, so nothing else may be cascaded from it");
    } finally {
      PerspectiveStreamGroupRegistry.Clear();
    }
  }

  [Test]
  [NotInParallel("PerspectiveStreamGroupRegistry")]
  public async Task RunMaintenanceOnceAsync_CascadeTargetsAModelWithNoRegisteredTableHere_SkipsItWithoutFailingAsync() {
    // A rolling deploy can leave this instance without a table for a sibling model a peer still
    // runs. The cascade computes the target from the shared membership graph regardless, so the
    // per-table lookup must skip it cleanly rather than crash — one out-of-date instance must never
    // take down maintenance for the whole fleet on every cycle.
    PerspectiveStreamGroupRegistry.Clear();
    try {
      PerspectiveStreamGroupRegistry.Register(typeof(AnnouncerModel), "coverage-group", announce: true, follow: false, bridge: false);
      PerspectiveStreamGroupRegistry.Register(typeof(FollowerModel), "coverage-group", announce: false, follow: true, bridge: false);
      var coord = new CascadeCoordinator {
        Journal = { new PerspectiveRowRef(ANNOUNCER_TABLE, Guid.CreateVersion7()) },
        // FollowerModel's table is deliberately absent, even though FollowerModel is registered.
        Tables = [new(typeof(AnnouncerModel).FullName!, ANNOUNCER_TABLE)],
      };
      var worker = _buildCascadeWorker(coord);

      await worker.RunMaintenanceOnceAsync(CancellationToken.None);

      await Assert.That(coord.Deleted).IsEmpty()
        .Because("the cascade computed a follower target this instance has no table mapping for; it "
               + "must be skipped rather than throwing and failing the whole cycle");
    } finally {
      PerspectiveStreamGroupRegistry.Clear();
    }
  }

  // ============================================================
  // Post-destruction hook resolved independently of the pre-destruction one
  // ============================================================

  private sealed class ProceedHook : IDestructionHook {
    public int AfterCalls { get; private set; }

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken ct = default)
      => ValueTask.FromResult(DestructionResult.Proceed());

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken ct = default) {
      AfterCalls++;
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  public async Task RunMaintenanceOnceAsync_HookUnavailableForThePostDestructionPhase_SkipsWithoutFailingTheCycleAsync() {
    // The pre- and post-destruction phases each resolve IDestructionHook independently rather than
    // sharing one reference across the cycle. If a hook is only available for part of a cycle (a
    // scoped registration torn down mid-flight, or a host reconfiguring hooks), the reap that
    // already committed must not be followed by a crash — a bare call here instead of the null
    // check would take the whole maintenance cycle down after the delete already happened.
    var hook = new ProceedHook();
    var callCount = 0;
    var coord = new StubCoordinator {
      EphemeralBodiesAboutToReap = [new EphemeralDestructionTarget(Guid.NewGuid(), Guid.NewGuid(), "coverage-event")],
    };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddTransient<IDestructionHook>(_ => ++callCount == 1 ? hook : null!);
    var sp = services.BuildServiceProvider();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      NullLogger<MaintenanceWorker>.Instance);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.PerformMaintenanceCallCount).IsEqualTo(1)
      .Because("the reap must still run to completion even though the post-destruction hook vanished afterward");
    await Assert.That(hook.AfterCalls).IsEqualTo(0)
      .Because("the second resolution returned null, so OnAfterDestructionAsync must never be reached");
  }
}
