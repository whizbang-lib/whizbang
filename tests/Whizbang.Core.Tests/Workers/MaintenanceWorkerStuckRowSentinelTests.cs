using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Locks the v0.657 slice 5b invariants around the MaintenanceWorker → stuck-row
/// sentinel wiring. The SQL surface from slice 5a returns the stuck rows; this
/// slice ensures the C# maintenance worker calls it once per tick and emits
/// exactly one Warning per stuck row so operators see the symptom.
/// </summary>
/// <docs>operations/observability/stuck-row-sentinel</docs>
public class MaintenanceWorkerStuckRowSentinelTests {

  private sealed record _LogEntry(LogLevel Level, string Message);

  private sealed class _CapturingLogger : ILogger<MaintenanceWorker> {
    public List<_LogEntry> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add(new _LogEntry(logLevel, formatter(state, exception)));
    }
    private sealed class _NullScope : IDisposable {
      public static readonly _NullScope Instance = new();
      public void Dispose() { }
    }
  }

  private sealed class _FakeCoordinator : IWorkCoordinator {
    public int SentinelCallCount { get; private set; }
    public List<StuckRow> StuckOutbox { get; init; } = [];
    public List<StuckRow> StuckInbox { get; init; } = [];

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);

    public Task<IReadOnlyList<StuckRow>> FindStuckOutboxRowsAsync(int maxAttempts, int limit, CancellationToken ct = default) {
      SentinelCallCount++;
      return Task.FromResult<IReadOnlyList<StuckRow>>(StuckOutbox);
    }

    public Task<IReadOnlyList<StuckRow>> FindStuckInboxRowsAsync(int maxAttempts, int limit, CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<StuckRow>>(StuckInbox);

    // Stubs for the rest of IWorkCoordinator
    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private static StuckRow _stuck(Guid msgId, string msgType, int attempts) => new() {
    MessageId = msgId,
    MessageType = msgType,
    StreamId = (Guid)TrackedGuid.NewMedo(),
    Attempts = attempts,
    ClaimedSince = DateTime.UtcNow.AddMinutes(-30),
  };

  /// <summary>
  /// The slot-3 case (forward-projected): an outbox row with attempts past the
  /// MaxOutboxAttempts threshold MUST surface as a Warning naming
  /// message_id + message_type + attempts. Without this, future bugs of the
  /// same shape stay silently stuck until an operator chases them down.
  /// </summary>
  [Test]
  public async Task MaintenanceTick_StuckOutboxRow_EmitsWarningPerRowAsync() {
    Guid stuckId = TrackedGuid.NewMedo();
    var coord = new _FakeCoordinator {
      StuckOutbox = [_stuck(stuckId, "JDX.RemoveShellUserCommand", attempts: 992)]
    };
    var (worker, logger) = _buildWorker(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
    await Assert.That(warnings.Count).IsGreaterThanOrEqualTo(1)
      .Because("MaintenanceWorker MUST emit at least one Warning when the sentinel surfaces a stuck row — otherwise the structural canary is silent.");
    await Assert.That(warnings.Any(w => w.Message.Contains(stuckId.ToString()))).IsTrue()
      .Because("The Warning MUST name the specific message_id so operators can investigate that row directly.");
    await Assert.That(warnings.Any(w => w.Message.Contains("JDX.RemoveShellUserCommand"))).IsTrue()
      .Because("The Warning MUST name the message_type — that's the producer-side hint for operators chasing down the source.");
    await Assert.That(warnings.Any(w => w.Message.Contains("992"))).IsTrue()
      .Because("Reporting the attempts count is what tells operators how long this has been stuck — 992 vs 11 frames the urgency differently.");
  }

  /// <summary>
  /// Healthy tick (no stuck rows) MUST NOT emit Warnings. Operator dashboards
  /// stay quiet under normal load.
  /// </summary>
  [Test]
  public async Task MaintenanceTick_NoStuckRows_EmitsNoSentinelWarningsAsync() {
    var coord = new _FakeCoordinator();
    var (worker, logger) = _buildWorker(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    var sentinelWarnings = logger.Entries
      .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("stuck", StringComparison.OrdinalIgnoreCase))
      .ToList();
    await Assert.That(sentinelWarnings).IsEmpty()
      .Because("Healthy traffic must produce zero stuck-row Warnings — the sentinel must be quiet by default.");
    await Assert.That(coord.SentinelCallCount).IsEqualTo(1)
      .Because("The sentinel call still happens (default Enabled=true) — just returns an empty list so no Warnings fire.");
  }

  /// <summary>
  /// Multiple stuck rows emit one Warning per row. Operators count saturation
  /// rate via Warning counts, so per-row emission is the right granularity.
  /// </summary>
  [Test]
  public async Task MaintenanceTick_MultipleStuckRows_OneWarningEachAsync() {
    var coord = new _FakeCoordinator {
      StuckOutbox = [
        _stuck(TrackedGuid.NewMedo(), "TypeA", 15),
        _stuck(TrackedGuid.NewMedo(), "TypeB", 25),
        _stuck(TrackedGuid.NewMedo(), "TypeC", 50),
      ],
      StuckInbox = [
        _stuck(TrackedGuid.NewMedo(), "TypeD", 12),
      ]
    };
    var (worker, logger) = _buildWorker(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    var stuckWarnings = logger.Entries
      .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("stuck", StringComparison.OrdinalIgnoreCase))
      .ToList();
    await Assert.That(stuckWarnings.Count).IsEqualTo(4)
      .Because("3 outbox + 1 inbox = 4 stuck rows = exactly 4 Warnings. Per-row granularity lets operators GROUP BY message_type to find spammy producers.");
  }

  /// <summary>
  /// Killswitch: when StuckRowSentinelEnabled = false, the sentinel methods
  /// must NOT be called. Lets operators disable the canary if it ever becomes
  /// noisy without disabling the rest of maintenance.
  /// </summary>
  [Test]
  public async Task MaintenanceTick_SentinelDisabled_DoesNotInvokeSentinelMethodsAsync() {
    var coord = new _FakeCoordinator {
      StuckOutbox = [_stuck(TrackedGuid.NewMedo(), "TypeA", 50)]
    };
    var (worker, _) = _buildWorker(coord, sentinelEnabled: false);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.SentinelCallCount).IsEqualTo(0)
      .Because("With the sentinel disabled, FindStuckOutboxRowsAsync MUST NOT be called — the SQL query has a cost (small but non-zero) operators may opt out of.");
  }

  private static (MaintenanceWorker Worker, _CapturingLogger Logger) _buildWorker(
      _FakeCoordinator coord, bool sentinelEnabled = true) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new _CapturingLogger();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions {
        IntervalMinutes = 1,
        StuckRowSentinelEnabled = sentinelEnabled,
        StuckRowSentinelMaxAttempts = 10,
        StuckRowSentinelLimit = 50,
      }),
      logger);
    return (worker, logger);
  }
}
