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
/// Covers the reap-driven snapshot pass and the post-destruction hook inside the
/// maintenance cycle.
/// </summary>
/// <remarks>
/// The snapshot pass runs BEFORE the reaper deletes consumed ephemeral bodies, so every
/// pair about to lose its history gets a rewind floor first. A failure there must not stop
/// the remaining targets — one perspective that cannot snapshot would otherwise cost every
/// later pair its floor too.
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerSnapshotAndHookTests {

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

  private sealed class SnapshotCoordinator : IWorkCoordinator {
    public List<EphemeralSnapshotTarget> Targets { get; init; } = [];

    public Task<IReadOnlyList<EphemeralSnapshotTarget>> GetEphemeralPairsNeedingSnapshotAsync(
        CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<EphemeralSnapshotTarget>>(Targets);

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

  private sealed class StubRunner(Exception? snapshotThrows = null) : IPerspectiveRunner {
    public List<(Guid StreamId, string Perspective)> Snapshots { get; } = [];
    public Type PerspectiveType => typeof(object);

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken ct = default)
      => throw new NotImplementedException();

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
        Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken ct = default)
      => throw new NotImplementedException();

    public Task BootstrapSnapshotAsync(
        Guid streamId, string perspectiveName, Guid lastEventId, CancellationToken ct = default) {
      lock (Snapshots) { Snapshots.Add((streamId, perspectiveName)); }
      return snapshotThrows is not null ? Task.FromException(snapshotThrows) : Task.CompletedTask;
    }
  }

  private sealed class StubRegistry(Dictionary<string, IPerspectiveRunner?> runners) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider sp)
      => runners.TryGetValue(perspectiveName, out var r) ? r : null;
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } =
      new HashSet<LifecycleStage>();
  }

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(
      SnapshotCoordinator coord, IPerspectiveRunnerRegistry? registry) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (registry is not null) {
      services.AddSingleton(registry);
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

  [Test]
  public async Task WithoutARunnerRegistry_TheSnapshotPassIsSkippedAsync() {
    // A host with no perspectives registered has nothing to snapshot; the pass must not
    // even ask the coordinator for targets.
    var coord = new SnapshotCoordinator {
      Targets = { new EphemeralSnapshotTarget(Guid.CreateVersion7(), "P", Guid.CreateVersion7()) },
    };
    var (worker, _) = _build(coord, registry: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
  }

  [Test]
  public async Task EachPairNeedingASnapshot_GetsOneAsync() {
    var streamId = Guid.CreateVersion7();
    var runner = new StubRunner();
    var coord = new SnapshotCoordinator {
      Targets = { new EphemeralSnapshotTarget(streamId, "Orders", Guid.CreateVersion7()) },
    };
    var (worker, _) = _build(coord, new StubRegistry(new() { ["Orders"] = runner }));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(runner.Snapshots.Select(s => s.Perspective)).Contains("Orders");
  }

  [Test]
  public async Task APairWithNoRunnerRegistered_IsSkippedWithoutFailingTheOthersAsync() {
    var known = new StubRunner();
    var coord = new SnapshotCoordinator {
      Targets = {
        new EphemeralSnapshotTarget(Guid.CreateVersion7(), "Unknown", Guid.CreateVersion7()),
        new EphemeralSnapshotTarget(Guid.CreateVersion7(), "Known", Guid.CreateVersion7()),
      },
    };
    var (worker, _) = _build(coord, new StubRegistry(new() { ["Known"] = known }));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(known.Snapshots.Select(s => s.Perspective)).Contains("Known");
  }

  [Test]
  public async Task OneSnapshotFailing_DoesNotCostTheLaterPairsTheirFloorAsync() {
    // The snapshot is a rewind floor taken before the reaper deletes the bodies. Abandoning
    // the loop on the first failure would leave every later pair without one.
    var good = new StubRunner();
    var bad = new StubRunner(new InvalidOperationException("snapshot failed"));
    var coord = new SnapshotCoordinator {
      Targets = {
        new EphemeralSnapshotTarget(Guid.CreateVersion7(), "Bad", Guid.CreateVersion7()),
        new EphemeralSnapshotTarget(Guid.CreateVersion7(), "Good", Guid.CreateVersion7()),
      },
    };
    var (worker, logger) = _build(coord, new StubRegistry(new() { ["Bad"] = bad, ["Good"] = good }));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(good.Snapshots.Select(s => s.Perspective)).Contains("Good");
    await Assert.That(logger.Snapshot().Any(e => e.Exception is InvalidOperationException)).IsTrue();
  }

  [Test]
  public async Task WithNoTargets_NothingIsSnapshottedAsync() {
    var runner = new StubRunner();
    var coord = new SnapshotCoordinator();
    var (worker, _) = _build(coord, new StubRegistry(new() { ["Orders"] = runner }));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(runner.Snapshots).IsEmpty();
  }
}
