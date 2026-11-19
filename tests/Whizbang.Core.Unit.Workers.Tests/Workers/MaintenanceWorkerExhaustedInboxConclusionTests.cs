// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The maintenance cycle concludes inbox rows past their attempt ceiling that no dispatcher concluded (#2117).
/// </summary>
/// <remarks>
/// The ceiling is enforced where a row is dispatched, so a row the claim keeps charging that never reaches a dispatcher
/// was retried for ever: its lease lapsed, the next claim took it again, and its attempts climbed for days without it
/// completing or being dead-lettered, while the stuck-row sentinel only reported it. The cycle now moves a domain
/// message to the dead-letter store, where its last recorded error is kept, and drops control-plane traffic, which is
/// never stored.
/// </remarks>
/// <docs>operations/observability/stuck-row-sentinel#exhausted-rows-are-concluded</docs>
public class MaintenanceWorkerExhaustedInboxConclusionTests {

  private const string DOMAIN_TYPE = "Shop.Orders.OrderPlaced, Shop.Contracts";
  private static readonly string _controlPlaneType = TypeNameFormatter.Format(typeof(IntegrityCheckpoint));

  private sealed record LogEntry(LogLevel Level, string Message);

  private sealed class CapturingLogger : ILogger<MaintenanceWorker> {
    public List<LogEntry> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  private sealed class FakeCoordinator : IWorkCoordinator {
    public List<StuckRow> Exhausted { get; init; } = [];
    public List<(int MaxAttempts, int Limit)> Finds { get; } = [];
    public List<(Guid[] Ids, int MaxAttempts)> Drops { get; } = [];
    public bool DropThrows { get; init; }
    public bool DropCancels { get; init; }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);

    public Task<IReadOnlyList<StuckRow>> FindStuckInboxRowsAsync(int maxAttempts, int limit, CancellationToken cancellationToken = default) {
      Finds.Add((maxAttempts, limit));
      return Task.FromResult<IReadOnlyList<StuckRow>>([.. Exhausted.Where(r => r.Attempts > maxAttempts).Take(limit)]);
    }

    public Task<int> DropExhaustedInboxRowsAsync(IReadOnlyList<Guid> messageIds, int maxAttempts, CancellationToken cancellationToken = default) {
      if (DropCancels) {
        throw new OperationCanceledException();
      }
      if (DropThrows) {
        throw new InvalidOperationException("drop failed");
      }
      Drops.Add(([.. messageIds], maxAttempts));
      return Task.FromResult(messageIds.Count);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed record Move(string SourceTable, Guid SourceId, MessageFailureReason Reason, string? ErrorText, Guid InstanceId, string Generation);

  private sealed class FakeDeadLetterStore : IDeadLetterStore {
    public List<Move> Moves { get; } = [];
    public bool Configured { get; init; } = true;
    public HashSet<Guid> AlreadyGone { get; init; } = [];
    public HashSet<Guid> Throws { get; init; } = [];
    public bool Cancels { get; init; }
    public bool IsConfigured => Configured;

    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId, MessageFailureReason failureReason,
        string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      if (Cancels) {
        throw new OperationCanceledException();
      }
      if (Throws.Contains(sourceId)) {
        throw new InvalidOperationException("move failed");
      }
      Moves.Add(new Move(sourceTable, sourceId, failureReason, errorText, instanceId, generation));
      return Task.FromResult<Guid?>(AlreadyGone.Contains(sourceId) ? null : deadLetterId);
    }
  }

  private sealed class FixedInstance(Guid id) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = id;
    public string ServiceName => "ConclusionSvc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class FixedGeneration : IGenerationProvider {
    public string GetGeneration() => "gen-test";
  }

  private static StuckRow _row(string messageType, int attempts) => new() {
    MessageId = TrackedGuid.New(),
    MessageType = messageType,
    StreamId = TrackedGuid.New(),
    Attempts = attempts,
    ClaimedSince = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
  };

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(
      FakeCoordinator coordinator, FakeDeadLetterStore? store, int? maxInboxAttempts = 10, bool registerDispatchOptions = true,
      bool conclude = true, Guid? instanceId = null, bool registerIdentity = true, DeadLetterMetrics? metrics = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    if (store is not null) {
      services.AddSingleton<IDeadLetterStore>(store);
    }
    if (registerDispatchOptions) {
      services.AddSingleton(Options.Create(new InboxDispatchWorkerOptions { MaxInboxAttempts = maxInboxAttempts }));
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    if (registerIdentity) {
      services.AddSingleton<IServiceInstanceProvider>(new FixedInstance(instanceId ?? Guid.Empty));
      services.AddSingleton<IGenerationProvider>(new FixedGeneration());
    }
    var sp = services.BuildServiceProvider();
    var logger = new CapturingLogger();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions {
        IntervalMinutes = 1,
        StuckRowSentinelEnabled = false,
        ConcludeExhaustedInboxRows = conclude,
        ExhaustedInboxConcludeLimit = 25,
      }),
      logger);
    return (worker, logger);
  }

  [Test]
  public async Task ExhaustedDomainRow_IsMovedToTheDeadLetterStore_AsMaxAttemptsExceededAsync() {
    var row = _row(DOMAIN_TYPE, attempts: 12);
    var coordinator = new FakeCoordinator { Exhausted = [row] };
    var store = new FakeDeadLetterStore();
    var instance = Guid.CreateVersion7();
    var (worker, logger) = _build(coordinator, store, instanceId: instance);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Finds).Contains((10, 25))
      .Because("the rows asked for are those past the dispatcher's own ceiling, in batches of the configured limit");
    await Assert.That(store.Moves.Count).IsEqualTo(1);
    var move = store.Moves[0];
    await Assert.That(move.SourceTable).IsEqualTo(DeadLetterSourceTable.INBOX);
    await Assert.That(move.SourceId).IsEqualTo(row.MessageId);
    await Assert.That(move.Reason).IsEqualTo(MessageFailureReason.MaxAttemptsExceeded);
    await Assert.That(move.InstanceId).IsEqualTo(instance);
    await Assert.That(move.Generation).IsEqualTo("gen-test");
    await Assert.That(move.ErrorText).Contains("attempts=12 > max=10")
      .Because("the dead letter says why it was concluded; the store appends the row's last recorded error to it");
    await Assert.That(coordinator.Drops).IsEmpty()
      .Because("a domain message is stored, never dropped");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(row.MessageId.ToString()))).IsTrue()
      .Because("every concluded row is named in a warning, as the dispatcher names the rows it dead-letters");
  }

  [Test]
  public async Task ExhaustedControlPlaneRow_IsDropped_NeverStoredAsync() {
    var row = _row(_controlPlaneType, attempts: 40);
    var coordinator = new FakeCoordinator { Exhausted = [row] };
    var store = new FakeDeadLetterStore();
    var (worker, _) = _build(coordinator, store);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(store.Moves).IsEmpty()
      .Because("control-plane traffic is never stored: the recovery worker would re-emit a stale copy on a later boot");
    await Assert.That(coordinator.Drops.Count).IsEqualTo(1);
    await Assert.That(coordinator.Drops[0].Ids).IsEquivalentTo([row.MessageId]);
    await Assert.That(coordinator.Drops[0].MaxAttempts).IsEqualTo(10)
      .Because("the drop re-checks the ceiling so a row concluded meanwhile is left alone");
  }

  [Test]
  public async Task NoAttemptCeiling_ConcludesNothingAsync() {
    var coordinator = new FakeCoordinator { Exhausted = [_row(DOMAIN_TYPE, attempts: 500)] };
    var store = new FakeDeadLetterStore();
    var (worker, _) = _build(coordinator, store, maxInboxAttempts: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Finds).IsEmpty()
      .Because("with no ceiling configured no row is ever exhausted");
    await Assert.That(store.Moves).IsEmpty();
  }

  [Test]
  public async Task NoInboxDispatchOptions_ConcludesNothingAsync() {
    var coordinator = new FakeCoordinator { Exhausted = [_row(DOMAIN_TYPE, attempts: 500)] };
    var store = new FakeDeadLetterStore();
    var (worker, _) = _build(coordinator, store, registerDispatchOptions: false);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Finds).IsEmpty()
      .Because("a host with no inbox dispatch has no ceiling to enforce");
  }

  [Test]
  public async Task Disabled_ConcludesNothingAsync() {
    var coordinator = new FakeCoordinator { Exhausted = [_row(DOMAIN_TYPE, attempts: 500)] };
    var store = new FakeDeadLetterStore();
    var (worker, _) = _build(coordinator, store, conclude: false);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Finds).IsEmpty();
    await Assert.That(store.Moves).IsEmpty();
  }

  [Test]
  public async Task NoDeadLetterStore_LeavesDomainRows_AndStillDropsControlPlaneAsync() {
    var domain = _row(DOMAIN_TYPE, attempts: 12);
    var control = _row(_controlPlaneType, attempts: 12);
    var coordinator = new FakeCoordinator { Exhausted = [domain, control] };
    var (worker, logger) = _build(coordinator, new FakeDeadLetterStore { Configured = false });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Drops.Count).IsEqualTo(1);
    await Assert.That(coordinator.Drops[0].Ids).IsEquivalentTo([control.MessageId]);
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("no dead-letter store"))).IsTrue()
      .Because("a domain row that cannot be stored is kept, and the warning says why it was not concluded");
  }

  [Test]
  public async Task UnregisteredDeadLetterStore_LeavesDomainRowsAsync() {
    var coordinator = new FakeCoordinator { Exhausted = [_row(DOMAIN_TYPE, attempts: 12)] };
    var (worker, logger) = _build(coordinator, store: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Drops).IsEmpty();
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("no dead-letter store"))).IsTrue();
  }

  [Test]
  public async Task AFailedMove_IsLogged_AndTheNextRowIsStillConcludedAsync() {
    var failing = _row(DOMAIN_TYPE, attempts: 12);
    var next = _row(DOMAIN_TYPE, attempts: 11);
    var coordinator = new FakeCoordinator { Exhausted = [failing, next] };
    var store = new FakeDeadLetterStore { Throws = [failing.MessageId] };
    var (worker, logger) = _build(coordinator, store);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(store.Moves.Select(m => m.SourceId)).IsEquivalentTo([next.MessageId]);
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(failing.MessageId.ToString())
      && e.Message.Contains("could not"))).IsTrue()
      .Because("the failed move names the row it left, which the next cycle tries again");
  }

  [Test]
  public async Task AMoveThatFindsTheRowGone_IsNotReportedAsConcludedAsync() {
    var gone = _row(DOMAIN_TYPE, attempts: 12);
    var coordinator = new FakeCoordinator { Exhausted = [gone] };
    var store = new FakeDeadLetterStore { AlreadyGone = [gone.MessageId] };
    var (worker, logger) = _build(coordinator, store);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(gone.MessageId.ToString()))).IsFalse()
      .Because("a row its dispatcher concluded in the meantime was logged there; it is not concluded twice");
  }

  [Test]
  public async Task AFailedDrop_IsLogged_AndDoesNotFailTheCycleAsync() {
    var coordinator = new FakeCoordinator { Exhausted = [_row(_controlPlaneType, attempts: 12)], DropThrows = true };
    var (worker, logger) = _build(coordinator, new FakeDeadLetterStore());

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("could not drop"))).IsTrue();
  }

  [Test]
  public async Task WithoutARegisteredIdentity_TheMoveUsesTheDefaultGenerationAsync() {
    var coordinator = new FakeCoordinator { Exhausted = [_row(DOMAIN_TYPE, attempts: 12)] };
    var store = new FakeDeadLetterStore();
    var (worker, _) = _build(coordinator, store, registerIdentity: false);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(store.Moves.Count).IsEqualTo(1);
    await Assert.That(store.Moves[0].Generation).IsEqualTo(new DefaultGenerationProvider().GetGeneration());
    await Assert.That(store.Moves[0].InstanceId).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task NoExhaustedRows_ConcludesNothingAsync() {
    var coordinator = new FakeCoordinator { Exhausted = [_row(DOMAIN_TYPE, attempts: 10)] };
    var store = new FakeDeadLetterStore();
    var (worker, _) = _build(coordinator, store);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Finds.Count).IsEqualTo(1);
    await Assert.That(store.Moves).IsEmpty()
      .Because("a row at the ceiling still has its last attempt to come");
    await Assert.That(coordinator.Drops).IsEmpty();
  }

  [Test]
  public async Task AConcludedDomainRow_IsCountedAsADeadLetterArrivalAsync() {
    var metrics = new DeadLetterMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
    var coordinator = new FakeCoordinator { Exhausted = [_row(DOMAIN_TYPE, attempts: 12)] };
    var store = new FakeDeadLetterStore();
    var (worker, _) = _build(coordinator, store, metrics: metrics);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(store.Moves.Count).IsEqualTo(1)
      .Because("the metrics are recorded alongside the move, which still happens");
  }

  [Test]
  public async Task CancellationDuringAMove_PropagatesAsync() {
    var coordinator = new FakeCoordinator { Exhausted = [_row(DOMAIN_TYPE, attempts: 12)] };
    var (worker, _) = _build(coordinator, new FakeDeadLetterStore { Cancels = true });

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("cancellation is shutdown, never a failed move to log and pass over");
  }

  [Test]
  public async Task CancellationDuringADrop_PropagatesAsync() {
    var coordinator = new FakeCoordinator { Exhausted = [_row(_controlPlaneType, attempts: 12)], DropCancels = true };
    var (worker, _) = _build(coordinator, new FakeDeadLetterStore());

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>();
  }
}
