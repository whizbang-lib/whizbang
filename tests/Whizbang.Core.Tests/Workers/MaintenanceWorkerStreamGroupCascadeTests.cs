using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// Covers the stream-group cascade: when a row is evicted from an announcing perspective,
/// the rows that follow it in the same group are evicted too.
/// </summary>
/// <remarks>
/// Serialises on its own key and clears the registry around every test —
/// PerspectiveStreamGroupRegistry is process-wide static, so a membership left behind
/// would change what an unrelated cascade test computes.
/// </remarks>
[NotInParallel("PerspectiveStreamGroupRegistry")]
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerStreamGroupCascadeTests {

  private sealed class AnnouncerModel;
  private sealed class FollowerModel;

  private const string ANNOUNCER_TABLE = "wh_per_announcer";
  private const string FOLLOWER_TABLE = "wh_per_follower";

  [Before(Test)]
  public void ClearRegistry() => PerspectiveStreamGroupRegistry.Clear();

  [After(Test)]
  public void ClearRegistryAfter() => PerspectiveStreamGroupRegistry.Clear();

  private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

  private sealed class CapturingLogger : ILogger<MaintenanceWorker> {
    private readonly List<LogEntry> _entries = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_entries) { _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception)); }
    }
    public List<LogEntry> Snapshot() { lock (_entries) { return [.. _entries]; } }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  private sealed class CascadeCoordinator : IWorkCoordinator {
    public List<PerspectiveRowRef> Journal { get; init; } = [];
    public List<PerspectiveTableName> Tables { get; init; } = [];
    public Exception? DrainThrows { get; init; }
    public List<(string Table, IReadOnlyCollection<Guid> Ids)> Deleted { get; } = [];
    public List<PerspectiveRowRef> Requeued { get; } = [];
    public List<PerspectiveRowDestructionTarget> RowsById { get; init; } = [];
    public List<(PerspectiveRowRef Row, DateTimeOffset Until)> Held { get; } = [];
    public int FailuresRecorded;

    public Task<IReadOnlyList<PerspectiveRowDestructionTarget>> GetPerspectiveRowsByIdsAsync(
        string clrTypeName, string tableName, IReadOnlyCollection<Guid> rowIds,
        CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<PerspectiveRowDestructionTarget>>(RowsById);

    public Task HoldPerspectiveRowDestructionAsync(
        IReadOnlyCollection<PerspectiveRowRef> rows, DateTimeOffset holdUntil, CancellationToken ct = default) {
      lock (Held) { Held.AddRange(rows.Select(r => (r, holdUntil))); }
      return Task.CompletedTask;
    }

    public Task<int> RecordPerspectiveRowDestructionFailureAsync(
        IReadOnlyCollection<PerspectiveRowRef> rows, TimeSpan retryBackoff, int maxRetries,
        OnDestroyFailure onDestroyFailure, CancellationToken ct = default) {
      Interlocked.Increment(ref FailuresRecorded);
      return Task.FromResult(1);
    }

    public Task<IReadOnlyList<PerspectiveRowRef>> DrainRowEvictionJournalAsync(
        int limit = 1000, CancellationToken ct = default)
      => DrainThrows is not null
        ? Task.FromException<IReadOnlyList<PerspectiveRowRef>>(DrainThrows)
        : Task.FromResult<IReadOnlyList<PerspectiveRowRef>>(Journal);

    public Task<IReadOnlyList<PerspectiveTableName>> GetPerspectiveTableNamesAsync(
        IReadOnlyCollection<string> clrTypeNames, CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<PerspectiveTableName>>(Tables);

    public Task<int> CascadeDeletePerspectiveRowsAsync(
        string tableName, IReadOnlyCollection<Guid> rowIds, CancellationToken ct = default) {
      lock (Deleted) { Deleted.Add((tableName, rowIds)); }
      return Task.FromResult(rowIds.Count);
    }

    public Task RequeueRowEvictionsAsync(
        IReadOnlyCollection<PerspectiveRowRef> rows, CancellationToken ct = default) {
      lock (Requeued) { Requeued.AddRange(rows); }
      return Task.CompletedTask;
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] m, int partitionCount, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private sealed class FollowerGuard(
      PerspectiveRowDecision? verdict = null, Exception? throws = null)
      : IPerspectiveRowDestructionGuard {
    public IReadOnlyCollection<Type> GuardedModels => [typeof(FollowerModel)];
    public int AfterCalls;

    public ValueTask<IReadOnlyDictionary<Guid, PerspectiveRowDecision>> OnBeforeReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> targets, CancellationToken ct = default) {
      if (throws is not null) {
        return ValueTask.FromException<IReadOnlyDictionary<Guid, PerspectiveRowDecision>>(throws);
      }
      var map = new Dictionary<Guid, PerspectiveRowDecision>();
      if (verdict is { } v) {
        foreach (var t in targets) { map[t.RowId] = v; }
      }
      return ValueTask.FromResult<IReadOnlyDictionary<Guid, PerspectiveRowDecision>>(map);
    }

    public ValueTask OnAfterReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> released, CancellationToken ct = default) {
      Interlocked.Increment(ref AfterCalls);
      return ValueTask.CompletedTask;
    }
  }

  private static PerspectiveRowDestructionTarget _followerTarget(Guid id) => new(
    typeof(FollowerModel).FullName!, FOLLOWER_TABLE, id, null,
    JsonDocument.Parse("{}").RootElement, "cascade");

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(
      CascadeCoordinator coord, IPerspectiveRowDestructionGuard? guard = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (guard is not null) {
      services.AddSingleton(guard);
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new CapturingLogger();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      logger);
    return (worker, logger);
  }

  private static void _registerGroup() {
    PerspectiveStreamGroupRegistry.Register(
      typeof(AnnouncerModel), "orders", announce: true, follow: false, bridge: false);
    PerspectiveStreamGroupRegistry.Register(
      typeof(FollowerModel), "orders", announce: false, follow: true, bridge: false);
  }

  private static List<PerspectiveTableName> _tables() => [
    new(typeof(AnnouncerModel).FullName!, ANNOUNCER_TABLE),
    new(typeof(FollowerModel).FullName!, FOLLOWER_TABLE),
  ];

  [Test]
  public async Task WithNoGroupsRegistered_NothingCascadesAsync() {
    // No memberships means no closure to compute; the journal must not even be drained.
    var coord = new CascadeCoordinator {
      Journal = { new PerspectiveRowRef(ANNOUNCER_TABLE, Guid.CreateVersion7()) },
      Tables = _tables(),
    };
    var (worker, _) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Deleted).IsEmpty();
  }

  [Test]
  public async Task WithAnEmptyJournal_NothingCascadesAsync() {
    _registerGroup();
    var coord = new CascadeCoordinator { Tables = _tables() };
    var (worker, _) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Deleted).IsEmpty();
  }

  [Test]
  public async Task AnEvictionSeedInAnUnknownTable_IsIgnoredAsync() {
    // The journal can name a table this host has no perspective for — a rolling deploy
    // where a peer runs a model this one does not. It is skipped, not cascaded blindly.
    _registerGroup();
    var coord = new CascadeCoordinator {
      Journal = { new PerspectiveRowRef("wh_per_unknown", Guid.CreateVersion7()) },
      Tables = _tables(),
    };
    var (worker, _) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Deleted).IsEmpty();
  }

  [Test]
  public async Task AnAnnouncerEviction_CascadesToItsFollowerAsync() {
    _registerGroup();
    var rowId = Guid.CreateVersion7();
    var coord = new CascadeCoordinator {
      Journal = { new PerspectiveRowRef(ANNOUNCER_TABLE, rowId) },
      Tables = _tables(),
    };
    var (worker, _) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Deleted.Any(d => d.Table == FOLLOWER_TABLE))
      .IsTrue()
      .Because("the follower shares the group, so evicting the announcer evicts it too");
  }

  [Test]
  public async Task DrainFailing_DoesNotFailTheCycleAsync() {
    _registerGroup();
    var coord = new CascadeCoordinator {
      Tables = _tables(),
      DrainThrows = new InvalidOperationException("journal drain failed"),
    };
    var (worker, logger) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e => e.Exception is InvalidOperationException)).IsTrue();
    await Assert.That(coord.Deleted).IsEmpty();
  }

  [Test]
  public async Task DrainCanceled_StopsTheCycleInsteadOfContinuingAsync() {
    // The companion to DrainFailing_DoesNotFailTheCycle. A journal drain that fails leaves the
    // seeds in place for the next cycle to recompute the closure, so losing this pass costs
    // nothing but time. A canceled drain is a stopping host, and the cascade that follows issues
    // deletes across follower tables — work that should not start on the way down.
    _registerGroup();
    var coord = new CascadeCoordinator {
      Tables = _tables(),
      DrainThrows = new OperationCanceledException(),
    };
    var (worker, logger) = _build(coord);

    // The catch is FILTERED on the token: an OperationCanceledException with nothing canceled
    // behind it is just a drain that threw an unfortunate type, and belongs in the wide arm.
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("the seeds survive either way; what must not survive is a cascade of deletes "
             + "starting on a host that has been asked to stop");
    await Assert.That(coord.Deleted).IsEmpty();
    await Assert.That(logger.Snapshot().Any(e => e.Exception is OperationCanceledException)).IsFalse()
      .Because("a shutdown logged as a drain failure is noise on every deploy");
  }

  // --- Guarded cascade -------------------------------------------------------
  // A follower can carry its own destruction guard. The cascade must consult it rather
  // than delete on the announcer's authority alone, so the same verdicts apply here as on
  // the direct reap path.

  [Test]
  public async Task GuardedFollower_ProceedVerdict_IsCascadeDeletedAsync() {
    _registerGroup();
    var seed = Guid.CreateVersion7();
    var followerRow = Guid.CreateVersion7();
    var coord = new CascadeCoordinator {
      Journal = { new PerspectiveRowRef(ANNOUNCER_TABLE, seed) },
      Tables = _tables(),
      RowsById = { _followerTarget(followerRow) },
    };
    var guard = new FollowerGuard(PerspectiveRowDecision.Proceed());
    var (worker, _) = _build(coord, guard);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Deleted.Any(d => d.Table == FOLLOWER_TABLE)).IsTrue();
  }

  [Test]
  public async Task GuardedFollower_CancelVerdict_IsHeldNotDeletedAsync() {
    _registerGroup();
    var followerRow = Guid.CreateVersion7();
    var coord = new CascadeCoordinator {
      Journal = { new PerspectiveRowRef(ANNOUNCER_TABLE, Guid.CreateVersion7()) },
      Tables = _tables(),
      RowsById = { _followerTarget(followerRow) },
    };
    var (worker, _) = _build(coord, new FollowerGuard(PerspectiveRowDecision.Cancel()));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Deleted.Any(d => d.Table == FOLLOWER_TABLE)).IsFalse();
    await Assert.That(coord.Held.Any(h => h.Row.RowId == followerRow)).IsTrue();
  }

  [Test]
  public async Task GuardedFollower_DeferVerdict_RequeuesTheSeedAsync() {
    // A deferred follower means the cascade is unfinished, so the seed goes back on the
    // journal — otherwise the eviction is forgotten and the follower never catches up.
    _registerGroup();
    var seed = Guid.CreateVersion7();
    var coord = new CascadeCoordinator {
      Journal = { new PerspectiveRowRef(ANNOUNCER_TABLE, seed) },
      Tables = _tables(),
      RowsById = { _followerTarget(Guid.CreateVersion7()) },
    };
    var until = DateTimeOffset.UtcNow.AddHours(1);
    var (worker, _) = _build(coord, new FollowerGuard(PerspectiveRowDecision.Defer(until)));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Requeued).IsNotEmpty();
  }

  [Test]
  public async Task GuardedFollower_GuardThrowing_RecordsAFailureAndRequeuesAsync() {
    _registerGroup();
    var coord = new CascadeCoordinator {
      Journal = { new PerspectiveRowRef(ANNOUNCER_TABLE, Guid.CreateVersion7()) },
      Tables = _tables(),
      RowsById = { _followerTarget(Guid.CreateVersion7()) },
    };
    var (worker, logger) = _build(
      coord, new FollowerGuard(throws: new InvalidOperationException("guard failed")));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.FailuresRecorded).IsEqualTo(1);
    await Assert.That(coord.Deleted.Any(d => d.Table == FOLLOWER_TABLE)).IsFalse();
    await Assert.That(logger.Snapshot().Any(e => e.Exception is InvalidOperationException)).IsTrue();
  }

  [Test]
  public async Task UnguardedFollower_IsCascadeDeletedDirectlyAsync() {
    _registerGroup();
    var coord = new CascadeCoordinator {
      Journal = { new PerspectiveRowRef(ANNOUNCER_TABLE, Guid.CreateVersion7()) },
      Tables = _tables(),
    };
    var (worker, _) = _build(coord, guard: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Deleted.Any(d => d.Table == FOLLOWER_TABLE)).IsTrue();
  }

  /// <summary>
  /// A guard that cancels the cycle from inside its own verdict, then throws cancellation.
  /// </summary>
  /// <remarks>
  /// Cancelling before the call would be a weaker test: the cycle has cancellation checks of its
  /// own ahead of the cascade block, and one of those would throw first — the arm under test would
  /// never be entered and the assertion would pass on the wrong exception.
  /// </remarks>
  private sealed class CancellingFollowerGuard(CancellationTokenSource stopping)
      : IPerspectiveRowDestructionGuard {
    public IReadOnlyCollection<Type> GuardedModels => [typeof(FollowerModel)];

    public ValueTask<IReadOnlyDictionary<Guid, PerspectiveRowDecision>> OnBeforeReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> targets, CancellationToken ct = default) {
      stopping.Cancel();
      return ValueTask.FromException<IReadOnlyDictionary<Guid, PerspectiveRowDecision>>(
        new OperationCanceledException());
    }

    public ValueTask OnAfterReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> released, CancellationToken ct = default)
      => ValueTask.CompletedTask;
  }

  [Test]
  public async Task GuardedFollower_CanceledDuringShutdown_DoesNotBurnADestructionAttemptAsync() {
    // The companion to the guard-failure test above. A guard that FAILED leaves rows unjudged, so
    // recording a destruction failure and burning a retry attempt is right. A shutdown leaves them
    // unjudged too, but it is not a verdict about the rows: MaxDestructionRetries is bounded and
    // OnDestroyFailure decides what happens at the end of it, so counting an attempt here lets a
    // few restarts during a deploy spend a cascaded row's whole budget without a guard ever having
    // returned an opinion.
    _registerGroup();
    using var stopping = new CancellationTokenSource();
    var coord = new CascadeCoordinator {
      Journal = { new PerspectiveRowRef(ANNOUNCER_TABLE, Guid.CreateVersion7()) },
      Tables = _tables(),
      RowsById = { _followerTarget(Guid.CreateVersion7()) },
    };
    var (worker, logger) = _build(coord, new CancellingFollowerGuard(stopping));

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("the host asked to stop, and the rest of the cycle takes the same locks the "
             + "shutdown path needs");
    await Assert.That(coord.FailuresRecorded).IsEqualTo(0)
      .Because("no guard returned a verdict, so no cascaded row may spend part of a retry budget "
             + "it will need when the host comes back");
    await Assert.That(logger.Snapshot().Any(e => e.Level == LogLevel.Warning)).IsFalse()
      .Because("a shutdown logged as a guard failure is noise on every deploy, and it buries the "
             + "observer errors this log exists to surface");
  }
}
