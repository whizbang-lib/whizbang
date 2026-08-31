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
    public List<EphemeralDestructionTarget> AboutToReap { get; init; } = [];
    public List<Guid> Held { get; } = [];
    public DateTimeOffset? HeldUntil { get; private set; }
    public int FailuresRecorded;
    public int AttemptToReport { get; init; } = 1;

    public Task<IReadOnlyList<EphemeralDestructionTarget>> GetEphemeralBodiesAboutToReapAsync(
        CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<EphemeralDestructionTarget>>(AboutToReap);

    public Task HoldEphemeralDestructionAsync(
        IReadOnlyList<Guid> eventIds, DateTimeOffset holdUntil, CancellationToken ct = default) {
      lock (Held) { Held.AddRange(eventIds); HeldUntil = holdUntil; }
      return Task.CompletedTask;
    }

    public Task<int> RecordDestructionFailureAsync(
        IReadOnlyList<Guid> eventIds, DateTimeOffset retryHoldUntil, int maxRetries,
        Whizbang.Core.Lifecycle.OnDestroyFailure onFailure = Whizbang.Core.Lifecycle.OnDestroyFailure.RetryThenForcedDelete,
        CancellationToken ct = default) {
      Interlocked.Increment(ref FailuresRecorded);
      return Task.FromResult(AttemptToReport);
    }

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

  private sealed class StubHook(
      DestructionResult? before = null, Exception? beforeThrows = null, Exception? afterThrows = null)
      : IDestructionHook {
    public int BeforeCalls;
    public int AfterCalls;

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(
        DestructionContext context, CancellationToken ct = default) {
      Interlocked.Increment(ref BeforeCalls);
      return beforeThrows is not null
        ? ValueTask.FromException<DestructionResult>(beforeThrows)
        : ValueTask.FromResult(before ?? new DestructionResult());
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken ct = default) {
      Interlocked.Increment(ref AfterCalls);
      return afterThrows is not null ? ValueTask.FromException(afterThrows) : ValueTask.CompletedTask;
    }
  }

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(
      SnapshotCoordinator coord, IPerspectiveRunnerRegistry? registry, IDestructionHook? hook = null,
      Whizbang.Core.Lifecycle.OnDestroyFailure policy =
        Whizbang.Core.Lifecycle.OnDestroyFailure.RetryThenForcedDelete) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (registry is not null) {
      services.AddSingleton(registry);
    }
    if (hook is not null) {
      services.AddSingleton(hook);
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new CapturingLogger();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions {
        IntervalMinutes = 1,
        OnDestroyFailure = policy,
      }),
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

  // --- Destruction hooks -----------------------------------------------------
  // A registered hook gets to preserve, compact or archive an ephemeral body before the
  // reaper deletes it. Its answer decides whether the delete proceeds at all, so the
  // cancel, defer and failure paths each change what happens to real data.

  private static EphemeralDestructionTarget _target() =>
    new(Guid.CreateVersion7(), Guid.CreateVersion7(), "Test.Event, Test");

  [Test]
  public async Task WithoutARegisteredHook_DestructionProceedsUnhookedAsync() {
    var coord = new SnapshotCoordinator { AboutToReap = { _target() } };
    var (worker, _) = _build(coord, registry: null, hook: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Held).IsEmpty();
  }

  [Test]
  public async Task HookCancelling_HoldsTheBodiesIndefinitelyAsync() {
    // Cancel means "do not delete this". The hold is DateTimeOffset.MaxValue rather than a
    // backoff, because there is no later time at which the answer changes on its own.
    var coord = new SnapshotCoordinator { AboutToReap = { _target(), _target() } };
    var hook = new StubHook(new DestructionResult { Cancel = true });
    var (worker, _) = _build(coord, registry: null, hook);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(hook.BeforeCalls).IsEqualTo(1);
    await Assert.That(coord.Held).Count().IsEqualTo(2);
    await Assert.That(coord.HeldUntil).IsEqualTo(DateTimeOffset.MaxValue);
  }

  [Test]
  public async Task HookDeferring_HoldsTheBodiesUntilTheRequestedTimeAsync() {
    var until = DateTimeOffset.UtcNow.AddHours(6);
    var coord = new SnapshotCoordinator { AboutToReap = { _target() } };
    var hook = new StubHook(new DestructionResult { DeferUntil = until });
    var (worker, _) = _build(coord, registry: null, hook);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.HeldUntil).IsEqualTo(until);
  }

  [Test]
  public async Task HookAllowing_LetsTheReapProceedAsync() {
    var coord = new SnapshotCoordinator { AboutToReap = { _target() } };
    var hook = new StubHook(new DestructionResult());
    var (worker, _) = _build(coord, registry: null, hook);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Held).IsEmpty();
    await Assert.That(hook.AfterCalls).IsEqualTo(1)
      .Because("the bodies were deleted, so the after-hook fires for them");
  }

  [Test]
  public async Task HookThrowingBeforeDestruction_RecordsAFailureAndKeepsTheBodiesAsync() {
    // A hook that cannot run is not permission to delete: the bodies are held under the
    // configured retry policy instead of being reaped unhooked.
    var coord = new SnapshotCoordinator { AboutToReap = { _target() } };
    var hook = new StubHook(beforeThrows: new InvalidOperationException("hook failed"));
    var (worker, logger) = _build(coord, registry: null, hook);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.FailuresRecorded).IsEqualTo(1);
    await Assert.That(logger.Snapshot().Any(e => e.Exception is InvalidOperationException)).IsTrue();
  }

  [Test]
  public async Task HookThrowingAfterDestruction_IsLoggedNotPropagatedAsync() {
    // The bodies are already gone by then; failing the cycle would lose the rest of the
    // maintenance pass over work that cannot be undone anyway.
    var coord = new SnapshotCoordinator { AboutToReap = { _target() } };
    var hook = new StubHook(afterThrows: new InvalidOperationException("after failed"));
    var (worker, logger) = _build(coord, registry: null, hook);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e => e.Exception is InvalidOperationException)).IsTrue();
  }

  [Test]
  public async Task WithNothingAboutToReap_TheHookIsNotConsultedAsync() {
    var coord = new SnapshotCoordinator();
    var hook = new StubHook();
    var (worker, _) = _build(coord, registry: null, hook);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(hook.BeforeCalls).IsEqualTo(0);
  }

  [Test]
  public async Task HookFailure_UnderForceDeletePolicy_ReportsAForcedDeleteAsync() {
    // The policy decides what a hook failure costs. ForceDeleteImmediately says the body
    // goes regardless, so the operator sees that rather than "held for retry".
    var coord = new SnapshotCoordinator { AboutToReap = { _target() } };
    var hook = new StubHook(beforeThrows: new InvalidOperationException("hook failed"));
    var (worker, logger) = _build(
      coord, registry: null, hook,
      Whizbang.Core.Lifecycle.OnDestroyFailure.ForceDeleteImmediately);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e =>
      e.Message.Contains("FORCED DELETE", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task HookFailure_UnderRetryThenKeep_WithRetriesExhausted_ReportsKeptAsync() {
    // The opposite policy: once the retries are spent the body is kept rather than forced,
    // so a hook that never succeeds cannot silently delete data it was meant to preserve.
    var coord = new SnapshotCoordinator {
      AboutToReap = { _target() },
      AttemptToReport = 999,
    };
    var hook = new StubHook(beforeThrows: new InvalidOperationException("hook failed"));
    var (worker, logger) = _build(
      coord, registry: null, hook,
      Whizbang.Core.Lifecycle.OnDestroyFailure.RetryThenKeep);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e =>
      e.Message.Contains("KEPT", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task HookFailure_WithRetriesExhausted_DefaultsToForcedDeleteAsync() {
    var coord = new SnapshotCoordinator {
      AboutToReap = { _target() },
      AttemptToReport = 999,
    };
    var hook = new StubHook(beforeThrows: new InvalidOperationException("hook failed"));
    var (worker, logger) = _build(coord, registry: null, hook);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e =>
      e.Message.Contains("FORCED DELETE", StringComparison.Ordinal))).IsTrue();
  }
}
