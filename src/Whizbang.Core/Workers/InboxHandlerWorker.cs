using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Drains handler results from a bounded channel, batches via <see cref="BatchFlusher{T}"/>,
/// calls <see cref="IWorkCoordinator.CommitHandlerBatchAsync"/> per batch. The throughput
/// multiplier — N handler results commit in one round-trip with single fsync, per-handler
/// success/failure isolation via SAVEPOINTs.
/// Phase C of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/handler-commit</docs>
public sealed partial class InboxHandlerWorker : BackgroundService, IInboxHandlerCommitChannel {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IFailureChannel _failureChannel;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly InboxHandlerWorkerOptions _options;
  private readonly ILogger<InboxHandlerWorker> _logger;
  private readonly IPinnedConnectionPool _pinnedPool;
  private readonly BatchFlusher<HandlerCommitRequest> _flusher;

  /// <summary>Creates the worker and its inner <see cref="BatchFlusher{T}"/> so the channel is writable before <see cref="ExecuteAsync"/> is invoked.</summary>
  public InboxHandlerWorker(
    IServiceScopeFactory scopeFactory,
    IFailureChannel failureChannel,
    ISchemaReadyGate schemaReadyGate,
    IOptions<InboxHandlerWorkerOptions> options,
    ILogger<InboxHandlerWorker> logger,
    IPinnedConnectionPool? pinnedPool = null) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _failureChannel = failureChannel ?? throw new ArgumentNullException(nameof(failureChannel));
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _pinnedPool = pinnedPool ?? NoOpPinnedConnectionPool.Instance;
    _flusher = new BatchFlusher<HandlerCommitRequest>(_flushBatchAsync, _options.Flusher, _logger);
  }

  /// <inheritdoc />
  public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);

    // Refuse rather than accept-and-drop. Previously this wrote unconditionally into a channel that
    // nothing drains while disabled, so callers believed their row was durable when it was not — it
    // would sit unprocessed, be re-claimed on every lease expiry, and burn a retry attempt each time
    // until it dead-lettered having never actually failed. Deliberately NOT a throw: the killswitch
    // is a legitimate operator action, and turning it into an exception on the dispatch path would
    // convert a config choice into a crash loop.
    if (!_options.Enabled) {
      LogCommitsDisabledOnEnqueue(_logger, request.InboxCompletion.MessageId);
      return ValueTask.CompletedTask;
    }

    return _flusher.Writer.WriteAsync(request, cancellationToken);
  }

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.Enabled) {
      LogDisabled(_logger);
      return Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }
    LogStarted(_logger, _options.Flusher.MaxBatchSize, _options.Flusher.CoalesceWindowMs);
    return _flusher.StoppedSignal;
  }

  private async Task _flushBatchAsync(IReadOnlyList<HandlerCommitRequest> batch, CancellationToken ct) {
    if (!_options.Enabled) {
      // Anything already in flight when the killswitch flipped. A bare return here dropped the batch
      // with no log at all — the rows stay unprocessed and are re-claimed until their retry budget
      // is gone, which is indistinguishable from a stuck handler when you are reading a dashboard.
      LogCommitsDisabledOnFlush(_logger, batch.Count);
      return;
    }
    LogDiagFlushEntered(_logger, batch.Count);

    // A stalled flush looks identical from outside whichever phase it is stuck in — rows simply stop
    // completing — but the three phases have completely different causes: the schema gate never
    // signalling, the pinned pool being exhausted, or the coordinator call queueing behind the SAME
    // concurrency gate the dispatch path is consuming. Timing each phase separately is what turns
    // "commits stopped" into a specific answer instead of a deploy cycle per guess.
    var phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
    await _schemaReadyGate.WaitForReadyAsync(ct);
    _warnIfSlowPhase("schema-ready-gate", phaseStart, batch.Count);
    LogDiagFlushSchemaReady(_logger, batch.Count);

    phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
    await using var pin = await _pinnedPool.TryPinForAsync(typeof(InboxHandlerWorker), ct);
    _warnIfSlowPhase("pinned-connection", phaseStart, batch.Count);
    using var __ctx = PinnedConnectionContext.Push(pin.Connection);
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
    var results = await coordinator.CommitHandlerBatchAsync(batch, ct);
    // This one also covers the coordinator's own WorkCoordinatorGate acquisition, which the dispatch
    // path competes for — a commit starving behind acquisition is the shape worth catching here.
    _warnIfSlowPhase("coordinator-commit", phaseStart, batch.Count);
    var successes = 0;
    var failures = 0;
    foreach (var r in results) {
      if (r.Success) {
        successes++;
      } else {
        failures++;
      }
    }
    LogDiagFlushCommitted(_logger, batch.Count, successes, failures);

    // Route per-handler failures to the failure flush worker for retry tracking.
    foreach (var result in results) {
      if (!result.Success) {
        var matching = batch.FirstOrDefault(r => r.HandlerId == result.HandlerId);
        if (matching is not null) {
          await _failureChannel.EnqueueAsync(WorkCategory.Inbox, new MessageFailure {
            MessageId = matching.InboxCompletion.MessageId,
            CompletedStatus = MessageProcessingStatus.None,
            Error = result.ErrorMessage ?? "unknown",
            Reason = MessageFailureReason.Unknown
          }, ct);
        }
      }
    }
  }

  /// <inheritdoc />
  public override async Task StopAsync(CancellationToken cancellationToken) {
    await _flusher.DisposeAsync();
    await base.StopAsync(cancellationToken);
    LogStopped(_logger);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "InboxHandlerWorker started: maxBatchSize={MaxBatchSize}, coalesceMs={CoalesceMs}")]
  static partial void LogStarted(ILogger logger, int maxBatchSize, int coalesceMs);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "InboxHandlerWorker stopped")]
  static partial void LogStopped(ILogger logger);

  /// <summary>Warns when one flush phase dominates, naming WHICH phase so the cause is not a guess.</summary>
  private void _warnIfSlowPhase(string phase, long startTimestamp, int batchCount) {
    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp);
    if (elapsed >= _slowPhaseThreshold) {
      LogFlushPhaseSlow(_logger, phase, (long)elapsed.TotalMilliseconds, batchCount);
    }
  }

  /// <summary>
  /// How long one flush phase may take before it is called out. Deliberately well above normal
  /// commit latency: this exists to name a STALL, not to narrate ordinary work.
  /// </summary>
  private static readonly TimeSpan _slowPhaseThreshold = TimeSpan.FromSeconds(5);

  [LoggerMessage(EventId = 23, Level = LogLevel.Warning,
    Message = "InboxHandlerWorker commits are DISABLED — commit for message {MessageId} was refused, "
            + "not queued. The row will NOT be committed and will be re-claimed on every lease expiry, "
            + "spending a retry attempt each time until it dead-letters having never failed.")]
  static partial void LogCommitsDisabledOnEnqueue(ILogger logger, Guid messageId);

  [LoggerMessage(EventId = 24, Level = LogLevel.Warning,
    Message = "InboxHandlerWorker commits are DISABLED — dropping {BatchCount} in-flight commit(s). "
            + "Those rows will NOT complete and will be re-claimed until their retry budget is gone.")]
  static partial void LogCommitsDisabledOnFlush(ILogger logger, int batchCount);

  [LoggerMessage(EventId = 25, Level = LogLevel.Warning,
    Message = "InboxHandlerWorker flush phase '{Phase}' took {ElapsedMs}ms for {BatchCount} commit(s) "
            + "— commits are stalling in this phase. 'coordinator-commit' also covers the shared "
            + "WorkCoordinatorGate, where a commit can starve behind claim/dispatch traffic.")]
  static partial void LogFlushPhaseSlow(ILogger logger, string phase, long elapsedMs, int batchCount);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "InboxHandlerWorker disabled via options — handler-batch commits skipped")]
  static partial void LogDisabled(ILogger logger);

  // Flush-checkpoint diagnostic, paired with InboxDispatchWorker's DIAG[1..5].
  // At Debug so operators can opt in via Serilog override when chasing
  // "channel quietly drained zero items" failure modes; quiet by default.
  [LoggerMessage(EventId = 20, Level = LogLevel.Debug,
    Message = "DIAG[F1] flush callback entered: batch={Count}")]
  static partial void LogDiagFlushEntered(ILogger logger, int count);

  [LoggerMessage(EventId = 21, Level = LogLevel.Debug,
    Message = "DIAG[F2] schema gate ready, opening scope: batch={Count}")]
  static partial void LogDiagFlushSchemaReady(ILogger logger, int count);

  [LoggerMessage(EventId = 22, Level = LogLevel.Debug,
    Message = "DIAG[F3] CommitHandlerBatchAsync returned: batch={Count} successes={Successes} failures={Failures}")]
  static partial void LogDiagFlushCommitted(ILogger logger, int count, int successes, int failures);
}

/// <summary>Channel surface for handler-result producers (the inbox dispatch path).</summary>
public interface IInboxHandlerCommitChannel {
  /// <summary>Enqueue a completed handler bundle for batched commit.</summary>
  ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Configuration for <see cref="InboxHandlerWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class InboxHandlerWorkerOptions {
  /// <summary>Killswitch — set to <c>false</c> to halt handler-batch commits. Default <c>true</c>.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>Tuning for the inner <see cref="BatchFlusher{T}"/>.</summary>
  public BatchFlusherOptions Flusher { get; set; } = new() {
    MaxBatchSize = 100,
    CoalesceWindowMs = 25,
    ImmediateFlushThreshold = 50,
    ChannelCapacity = 5_000
  };
}
