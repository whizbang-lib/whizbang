using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tags;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for <see cref="CoalesceShipWorker"/> paths the primary suite
/// (<see cref="CoalesceShipWorkerTests"/>) doesn't reach: the schema-gate wait and the startup
/// recovery step being canceled before the loop ever ticks, the release-backstop's own logging
/// (both on startup recovery and on the per-tick backstop), and a cancellation arriving mid-fold
/// propagating instead of being treated as one group's isolated failure.
/// </summary>
public class CoalesceShipWorkerCoverageTests {
  private static readonly DateTimeOffset _testNow = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

  /// <summary>Records rendered log messages so the branch actually taken can be asserted.</summary>
  private sealed class _messageLogger : ILogger<CoalesceShipWorker> {
    private readonly List<string> _messages = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_messages) { _messages.Add(formatter(state, exception)); }
    }
    public bool Saw(string fragment) {
      lock (_messages) { return _messages.Any(m => m.Contains(fragment, StringComparison.Ordinal)); }
    }
  }

  /// <summary>A coordinator whose <c>ReleaseMaturedCoalesceAsync</c> throws instead of releasing.</summary>
  private sealed class _canceledOnReleaseCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public int ReleaseCalls { get; private set; }

    public Task<int> ReleaseMaturedCoalesceAsync(string group, CancellationToken cancellationToken = default) {
      ReleaseCalls++;
      throw new OperationCanceledException("shutdown during startup recovery");
    }
  }

  /// <summary>A coordinator whose release always reports rows released — for the LogReleasedMatured branch.</summary>
  private sealed class _releasingCoordinator(int releasedCount) : NoOpWorkCoordinator, IWorkCoordinator {
    public IReadOnlyList<CoalesceGroupStats> Stats { get; init; } = [];

    public Task<IReadOnlyList<CoalesceGroupStats>> GetPendingCoalesceGroupStatsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Stats);

    public Task<int> ReleaseMaturedCoalesceAsync(string group, CancellationToken cancellationToken = default) =>
      Task.FromResult(releasedCount);
  }

  /// <summary>A coordinator whose fetch (the first call inside a fold) always cancels.</summary>
  private sealed class _cancelingFoldCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<string> ReleasedGroups { get; } = [];
    public IReadOnlyList<CoalesceGroupStats> Stats { get; init; } = [];

    public Task<IReadOnlyList<CoalesceGroupStats>> GetPendingCoalesceGroupStatsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Stats);

    public Task<IReadOnlyList<OutboxMessage>> FetchPendingCoalesceAsync(string group, int limit, CancellationToken cancellationToken = default) =>
      throw new OperationCanceledException("shutdown mid-fold");

    public Task<int> ReleaseMaturedCoalesceAsync(string group, CancellationToken cancellationToken = default) {
      ReleasedGroups.Add(group);
      return Task.FromResult(0);
    }
  }

  // ── ExecuteAsync lifecycle: both quiet exits must return, never fault ───

  /// <summary>
  /// If this regressed to running startup recovery before the schema gate opened (or to
  /// faulting instead of returning), the shipper could fire SQL against tables migrations
  /// haven't created yet, or a routine shutdown-before-ready would read as a crash.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_StoppedWhileWaitingOnTheSchemaGate_ReturnsWithoutFaultingAsync(CancellationToken testToken) {
    var time = new FakeTimeProvider(_testNow);
    var coordinator = new NoOpWorkCoordinator();
    // Gate never marked ready — a host stopped mid-migration.
    var worker = _buildWorker(coordinator, _oneGroupResolver(time), time, gate: new SchemaReadyGate());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var executeTask = worker.ExecuteTask;
    await worker.StopAsync(CancellationToken.None);

    await executeTask!.WaitAsync(TimeSpan.FromSeconds(5), testToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(executeTask.IsCompleted).IsTrue()
      .Because("stopping while still waiting on the schema gate must let ExecuteAsync return promptly");
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("a host stopped before the schema exists must shut down cleanly, not report a crash");
  }

  /// <summary>
  /// If this regressed to letting a mid-recovery cancellation escape uncaught (or retry instead
  /// of stopping), a shutdown in progress would either crash-log on every deploy or keep hammering
  /// a coordinator that is already being torn down.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_StartupRecoveryCanceled_ReturnsWithoutFaultingAsync(CancellationToken testToken) {
    var time = new FakeTimeProvider(_testNow);
    var coordinator = new _canceledOnReleaseCoordinator();
    var worker = _buildWorker(coordinator, _oneGroupResolver(time), time);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var executeTask = worker.ExecuteTask;
    await executeTask!.WaitAsync(TimeSpan.FromSeconds(5), testToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask.IsCompleted).IsTrue()
      .Because("a cancellation during startup recovery must let the loop exit promptly rather than hang");
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("a canceled recovery ending ExecuteAsync as a fault would read as a crash on an ordinary shutdown");
    await Assert.That(coordinator.ReleaseCalls).IsEqualTo(1)
      .Because("the cancellation must stop recovery immediately rather than retrying past it");
  }

  // ── Release-backstop logging: both call sites ───────────────────────────

  /// <summary>
  /// If this regressed to staying silent on a non-zero release, an operator would have no
  /// visibility into rows quietly degrading to individual shipping on every restart.
  /// </summary>
  [Test]
  public async Task RunStartupRecoveryAsync_ReleasedRows_LogsHowManyAndForWhichGroupAsync() {
    var time = new FakeTimeProvider(_testNow);
    var coordinator = new _releasingCoordinator(releasedCount: 4);
    var logger = new _messageLogger();
    var worker = _buildWorker(coordinator, _oneGroupResolver(time), time, logger: logger);

    await worker.RunStartupRecoveryAsync(CancellationToken.None);

    await Assert.That(logger.Saw("Released 4")).IsTrue()
      .Because("a silent release would leave an operator with no evidence that backlog just "
             + "degraded to individual shipping on this restart");
    await Assert.That(logger.Saw("record-digest")).IsTrue()
      .Because("the log line must name WHICH group degraded, not just that something did");
  }

  /// <summary>
  /// The per-tick backstop is the LAST exit for rows a fold could not claim. If this regressed
  /// to staying silent, an operator would have no signal that rows are steadily degrading to
  /// individual shipping tick after tick.
  /// </summary>
  [Test]
  public async Task RunOnceAsync_ReleaseBackstopReleasesRows_LogsHowManyAndForWhichGroupAsync() {
    var time = new FakeTimeProvider(_testNow);
    var coordinator = new _releasingCoordinator(releasedCount: 2) {
      // PendingCount 0 keeps the fold pass a no-op (binding lookup short-circuits on
      // `PendingCount <= 0`) so only the backstop pass below is under test.
      Stats = [_stats("record-digest", count: 0, oldestAge: 500, newestAge: 500)],
    };
    var logger = new _messageLogger();
    var worker = _buildWorker(coordinator, _oneGroupResolver(time), time, logger: logger);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(logger.Saw("Released 2")).IsTrue()
      .Because("the per-tick release backstop is the last exit for rows a fold could not claim — "
             + "silence here removes the only signal that rows are degrading to individual shipping");
  }

  // ── Cancellation mid-fold must propagate, not isolate-and-continue ─────

  /// <summary>
  /// If this regressed to treating a mid-fold cancellation like an ordinary per-group failure
  /// (log and move on), a shutdown in progress would keep working through the rest of the tick
  /// — including running the release backstop — instead of winding down immediately.
  /// </summary>
  [Test]
  public async Task RunOnceAsync_FoldCanceledMidGroup_PropagatesRatherThanTreatingItAsAGroupFailureAsync() {
    var time = new FakeTimeProvider(_testNow);
    var coordinator = new _cancelingFoldCoordinator {
      Stats = [_stats("record-digest", count: 3, oldestAge: 40, newestAge: 20)],
    };
    var worker = _buildWorker(coordinator, _oneGroupResolver(time), time);

    await Assert.ThrowsAsync<OperationCanceledException>(async () => await worker.RunOnceAsync(CancellationToken.None));

    await Assert.That(coordinator.ReleasedGroups).IsEmpty()
      .Because("a cancellation mid-fold must abort the whole tick immediately — unlike an ordinary "
             + "fold failure (isolated per group, release backstop still runs), a shutdown in "
             + "progress must not keep doing more work on its way out");
  }

  // ── helpers ──────────────────────────────────────────────────────────────

  private static CoalesceGroupResolver _oneGroupResolver(FakeTimeProvider time, string group = "record-digest") {
    var tagOptions = new TagOptions();
    tagOptions.Coalesce(group, c => c.SlideSeconds = 15);
    return new CoalesceGroupResolver(tagOptions, time, () => []);
  }

  private static CoalesceGroupStats _stats(string group, long count, int oldestAge, int newestAge) => new() {
    Group = group,
    PendingCount = count,
    OldestCreatedAt = _testNow.AddSeconds(-oldestAge),
    NewestCreatedAt = _testNow.AddSeconds(-newestAge),
  };

  private static CoalesceShipWorker _buildWorker(
      IWorkCoordinator coordinator,
      CoalesceGroupResolver? resolver,
      FakeTimeProvider time,
      ISchemaReadyGate? gate = null,
      ILogger<CoalesceShipWorker>? logger = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton(new WorkCoordinatorOptions());
    var sp = services.BuildServiceProvider();

    return new CoalesceShipWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate ?? SchemaReadyGate.AlreadyReady(),
      new Whizbang.Core.Observability.ServiceInstanceProvider(),
      coalesceResolver: resolver,
      logger: logger,
      timeProvider: time);
  }
}
