// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1735 (WS-3 S6): the ephemeral reaper routes on the disposition its PreDestruction hook returns. <c>Delete</c> reaps as
/// before; <c>Compact</c> runs the registered <see cref="ICompactionHandler"/> over the batch and then reaps, and with no
/// handler holds the batch for the retry backoff and logs why; a disposition the reaper cannot carry out holds the batch
/// the same way. A held batch is not reaped, and no PostDestruction fires for it.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#dispositions</docs>
public class MaintenanceWorkerDispositionTests {
  private static readonly DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
  private const int BACKOFF_SECONDS = 300;

  private sealed class DecidingHook(List<string> log, DestructionResult decision) : IDestructionHook {
    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      log.Add("before");
      return ValueTask.FromResult(decision);
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      log.Add("after");
      return ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingCompactor(List<string> log, Exception? fails = null) : ICompactionHandler {
    public DestructionContext? Seen { get; private set; }

    public ValueTask CompactAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      Seen = context;
      log.Add("compact");
      return fails is null ? ValueTask.CompletedTask : ValueTask.FromException(fails);
    }
  }

  private sealed class Coordinator(List<string> log, IReadOnlyList<EphemeralDestructionTarget> targets) : NoOpWorkCoordinator, IWorkCoordinator {
    public List<(IReadOnlyList<Guid> Ids, DateTimeOffset Until)> Holds { get; } = [];
    public int Failures { get; private set; }

    Task<IReadOnlyList<EphemeralDestructionTarget>> IWorkCoordinator.GetEphemeralBodiesAboutToReapAsync(CancellationToken cancellationToken) =>
      Task.FromResult(targets);

    Task IWorkCoordinator.HoldEphemeralDestructionAsync(IReadOnlyList<Guid> eventIds, DateTimeOffset holdUntil, CancellationToken cancellationToken) {
      Holds.Add((eventIds, holdUntil));
      return Task.CompletedTask;
    }

    Task<int> IWorkCoordinator.RecordDestructionFailureAsync(IReadOnlyList<Guid> eventIds, DateTimeOffset retryHoldUntil, int maxRetries,
        OnDestroyFailure onFailure, CancellationToken cancellationToken) {
      Failures++;
      return Task.FromResult(1);
    }

    Task<IReadOnlyList<MaintenanceResult>> IWorkCoordinator.PerformMaintenanceAsync(CancellationToken cancellationToken) {
      log.Add("reap");
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }
  }

  private sealed class ListLogger : ILogger<MaintenanceWorker> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private static readonly EphemeralDestructionTarget[] _batch = [
    new(new Guid("0192f1a0-0000-7000-8000-000000000001"), new Guid("0192f1a0-0000-7000-8000-0000000000a1"), "A"),
    new(new Guid("0192f1a0-0000-7000-8000-000000000002"), new Guid("0192f1a0-0000-7000-8000-0000000000a2"), "B"),
  ];

  private static MaintenanceWorker _worker(Coordinator coordinator, IDestructionHook hook, ListLogger logger, ICompactionHandler? compactor = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton(hook);
    services.AddSingleton<TimeProvider>(new FakeTimeProvider(_now));
    if (compactor is not null) {
      services.AddSingleton(compactor);
    }
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions {
        IntervalMinutes = 1,
        StuckRowSentinelEnabled = false,
        DestructionRetryBackoffSeconds = BACKOFF_SECONDS,
      }),
      logger);
  }

  [Test]
  public async Task Compact_RunsTheHandlerOverTheBatch_ThenReapsAsync() {
    var log = new List<string>();
    var coordinator = new Coordinator(log, _batch);
    var compactor = new RecordingCompactor(log);

    await _worker(coordinator, new DecidingHook(log, DestructionResult.Proceed(Disposition.Compact)), new ListLogger(), compactor)
      .RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(log).IsEquivalentTo(["before", "compact", "reap", "after"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the summary is durable before the bodies are reaped");
    await Assert.That(compactor.Seen!.Targets).IsEquivalentTo(_batch);
    await Assert.That(coordinator.Holds).IsEmpty();
  }

  [Test]
  public async Task Compact_WithNoHandler_HoldsTheBatchForTheBackoff_AndLogsWhyAsync() {
    var log = new List<string>();
    var coordinator = new Coordinator(log, _batch);
    var logger = new ListLogger();

    await _worker(coordinator, new DecidingHook(log, DestructionResult.Proceed(Disposition.Compact)), logger)
      .RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Holds).Count().IsEqualTo(1);
    await Assert.That(coordinator.Holds[0].Ids).IsEquivalentTo(_batch.Select(t => t.EventId).ToArray());
    await Assert.That(coordinator.Holds[0].Until).IsEqualTo(_now.AddSeconds(BACKOFF_SECONDS))
      .Because("held for the retry backoff, so a handler registered later picks the batch up, and on the host's clock");
    await Assert.That(log).DoesNotContain("after").Because("nothing was destroyed, so nothing is announced as destroyed");
    await Assert.That(logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(ICompactionHandler), StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task CryptoShred_HoldsTheBatchForTheBackoff_AndLogsWhyAsync() {
    var log = new List<string>();
    var coordinator = new Coordinator(log, _batch);
    var logger = new ListLogger();

    await _worker(coordinator, new DecidingHook(log, DestructionResult.Proceed(Disposition.CryptoShred)), logger)
      .RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Holds).Count().IsEqualTo(1);
    await Assert.That(coordinator.Holds[0].Until).IsEqualTo(_now.AddSeconds(BACKOFF_SECONDS));
    await Assert.That(log).DoesNotContain("after");
    await Assert.That(logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains("CryptoShred", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task Archive_HoldsTheBatchForTheBackoff_AndLogsWhy_BecauseTheReaperCannotArchiveEventBodiesAsync() {
    // The archive store moves a stream's prefix or whole stream; the reaper removes event bodies across streams, and
    // a prefix reaching an ephemeral perspective's newest snapshot is refused, so the reaper keeps the bodies.
    var log = new List<string>();
    var coordinator = new Coordinator(log, _batch);
    var logger = new ListLogger();

    await _worker(coordinator, new DecidingHook(log, DestructionResult.Proceed(Disposition.Archive)), logger)
      .RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Holds).Count().IsEqualTo(1);
    await Assert.That(log).DoesNotContain("after");
    await Assert.That(logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains("Archive", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task Delete_AndAHookThatNamesNone_ReapAsBeforeAsync() {
    // The control: the harness sees a reap with no hold when the disposition is one the reaper carries out.
    foreach (var decision in new[] { DestructionResult.Proceed(Disposition.Delete), DestructionResult.Proceed() }) {
      var log = new List<string>();
      var coordinator = new Coordinator(log, _batch);

      await _worker(coordinator, new DecidingHook(log, decision), new ListLogger()).RunMaintenanceOnceAsync(CancellationToken.None);

      await Assert.That(log).IsEquivalentTo(["before", "reap", "after"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
      await Assert.That(coordinator.Holds).IsEmpty();
    }
  }

  [Test]
  public async Task Compact_AHandlerThatThrows_TakesTheFailurePath_AndNothingIsAnnouncedAsync() {
    var log = new List<string>();
    var coordinator = new Coordinator(log, _batch);

    await _worker(coordinator, new DecidingHook(log, DestructionResult.Proceed(Disposition.Compact)), new ListLogger(),
        new RecordingCompactor(log, new InvalidOperationException("summary store down")))
      .RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Failures).IsEqualTo(1)
      .Because("a failed compaction is a failed preserve step: held and retried like a throwing hook");
    await Assert.That(log).DoesNotContain("after");
  }
}
