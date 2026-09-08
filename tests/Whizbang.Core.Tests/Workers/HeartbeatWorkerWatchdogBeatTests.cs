using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The worker-side arms of the watchdog (#722), driven by a fake clock: a beat forced because the last
/// accepted one approached the threshold is counted and logged as a watchdog beat, and a beat that took
/// a fast interval or more is counted and logged as slow, because a slow beat means the database is
/// slow for the work it reports on as well.
/// </summary>
/// <docs>fundamentals/workers/instance-liveness</docs>
[Category("Unit")]
public class HeartbeatWorkerWatchdogBeatTests {
  private sealed class ListLogger : ILogger<HeartbeatWorker> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => Entries.Add((logLevel, formatter(state, exception)));
  }

  private sealed class ScriptedHeartbeatCoordinator(Func<Task<bool>> onBeat) : IWorkCoordinator {
    public int Beats { get; private set; }
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      Beats++;
      return onBeat();
    }
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private static (HeartbeatWorker Worker, ListLogger Logger, ScriptedHeartbeatCoordinator Coordinator) _worker(
      FakeTimeProvider clock, Func<Task<bool>> onBeat, HeartbeatWorkerOptions? options = null) {
    var coordinator = new ScriptedHeartbeatCoordinator(onBeat);
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new ListLogger();
    var worker = new HeartbeatWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
      schemaReadyGate: gate,
      options: Options.Create(options ?? new HeartbeatWorkerOptions { IntervalSeconds = 30, SlowIntervalSeconds = 60 }),
      logger: logger,
      lifecycleState: HeartbeatTestDependencies.LifecycleState,
      libraryVersion: HeartbeatTestDependencies.Version,
      timeProvider: clock);
    return (worker, logger, coordinator);
  }

  [Test]
  public async Task Tick_LastAcceptedBeatOneLeadFromTheThreshold_BeatsAsAWatchdogAndLogsItAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var (worker, logger, coordinator) = _worker(clock, () => Task.FromResult(true));
    var options = new HeartbeatWorkerOptions { IntervalSeconds = 30, SlowIntervalSeconds = 60 };
    var deadline = HeartbeatLivenessThreshold.StaleThreshold(options) - HeartbeatLivenessThreshold.WatchdogLead(options);

    var first = await worker.TickForTestsAsync(CancellationToken.None);
    // Nothing beat since; the clock reaches the deadline before the next regular beat happened.
    clock.Advance(deadline);
    var second = await worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(first.Reason).IsEqualTo(HeartbeatBeatReason.Initial);
    await Assert.That(second.Reason).IsEqualTo(HeartbeatBeatReason.Watchdog)
      .Because("the last accepted beat is one lead from the stale threshold, so the writer forces a beat rather than risk a false death");
    await Assert.That(coordinator.Beats).IsEqualTo(2);
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("watchdog beat", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("a watchdog beat is a symptom an operator should see: the regular beat ran late");
  }

  [Test]
  public async Task Tick_BeatThatTakesAFastIntervalOrMore_IsLoggedAsSlowAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var options = new HeartbeatWorkerOptions { IntervalSeconds = 30, SlowIntervalSeconds = 60 };
    var lead = HeartbeatLivenessThreshold.WatchdogLead(options);
    var (worker, logger, _) = _worker(clock, () => {
      // The database answers the beat, but only after a full fast interval has passed.
      clock.Advance(lead);
      return Task.FromResult(true);
    }, options);

    await worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(worker.LastBeatDuration).IsGreaterThanOrEqualTo(lead);
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("beat took", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("a beat that takes a fast interval is the heartbeat starving behind slow commits; it is the leading symptom of a false death");
  }

  [Test]
  public async Task Tick_RegularBeatWithinTheLead_IsNeitherWatchdogNorSlowAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var (worker, logger, _) = _worker(clock, () => Task.FromResult(true));

    await worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(0)
      .Because("the quiet path logs nothing at warning level");
  }
}
