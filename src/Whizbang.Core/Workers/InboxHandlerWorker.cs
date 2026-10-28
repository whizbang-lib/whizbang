// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  // #1788: rows wait here for their commit while held by the inbox channel writer; each flush releases them.
  private readonly IInboxChannelWriter? _inboxChannelWriter;

  /// <summary>
  /// How many failed rows one flush logs individually before the rest are summarized in one line (#1789).
  /// </summary>
  internal const int MAX_COMMIT_FAILURES_LOGGED_PER_FLUSH = 5;

  /// <summary>Creates the worker and its inner <see cref="BatchFlusher{T}"/> so the channel is writable before <see cref="ExecuteAsync"/> is invoked.</summary>
  public InboxHandlerWorker(
    IServiceScopeFactory scopeFactory,
    IFailureChannel failureChannel,
    ISchemaReadyGate schemaReadyGate,
    IOptions<InboxHandlerWorkerOptions> options,
    ILogger<InboxHandlerWorker> logger,
    IPinnedConnectionPool pinnedPool,
    Whizbang.Core.Observability.WorkCoordinatorMetrics? metrics = null,
    IInboxChannelWriter? inboxChannelWriter = null) {
    _inboxChannelWriter = inboxChannelWriter;
    ArgumentNullException.ThrowIfNull(scopeFactory);
    _scopeFactory = scopeFactory;
    ArgumentNullException.ThrowIfNull(failureChannel);
    _failureChannel = failureChannel;
    ArgumentNullException.ThrowIfNull(schemaReadyGate);
    _schemaReadyGate = schemaReadyGate;
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
    _pinnedPool = pinnedPool;
    _flusher = new BatchFlusher<HandlerCommitRequest>(_flushBatchAsync, _options.Flusher, _logger);
    // The queue depth is observable (#740): the one place dispatched-but-uncommitted work waits in memory.
    metrics?.ObserveHandlerCommitQueue(() => _flusher.Pending);
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
      _inboxChannelWriter?.RemoveInFlight(request.HandlerId);
      return ValueTask.CompletedTask;
    }

    return _flusher.EnqueueAsync(request, cancellationToken);
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

  /// <summary>
  /// Flushes one batch, then releases its rows from the held set however the flush ended (#1788): a committed row is
  /// gone from the store, and a row whose commit failed or never ran must be offered again by the next claim.
  /// </summary>
  private async Task _flushBatchAsync(IReadOnlyList<HandlerCommitRequest> batch, CancellationToken ct) {
    try {
      await _flushCoreAsync(batch, ct);
    } finally {
      if (_inboxChannelWriter is { } held) {
        foreach (var request in batch) {
          held.RemoveInFlight(request.HandlerId);
        }
      }
    }
  }

  private async Task _flushCoreAsync(IReadOnlyList<HandlerCommitRequest> batch, CancellationToken ct) {
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

    if (failures > 0) {
      await _routeFailuresAsync(batch, results, ct);
    }
  }

  /// <summary>
  /// Logs each failed row and routes it to the failure flush worker for retry tracking (#1789). The first
  /// <see cref="MAX_COMMIT_FAILURES_LOGGED_PER_FLUSH"/> failures are logged one per row, with the row's id, type
  /// and error; the rest of the flush is counted in one summary line, so a flush where every row fails stays
  /// readable. Routing is never capped. A result naming no request cannot be routed and is logged as such.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/InboxHandlerWorkerTests.cs:CommitFailure_IsLoggedAtWarning_WithTheMessageIdTypeAndErrorAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/InboxHandlerWorkerTests.cs:ManyCommitFailuresInOneFlush_LogTheFirstIndividually_ThenOneSummaryOfTheRestAsync</tests>
  private async Task _routeFailuresAsync(
      IReadOnlyList<HandlerCommitRequest> batch, IReadOnlyList<HandlerBatchResult> results, CancellationToken ct) {
    var logged = 0;
    var unlogged = 0;
    foreach (var result in results) {
      if (result.Success) {
        continue;
      }
      var error = result.ErrorMessage ?? "unknown";
      var matching = batch.FirstOrDefault(r => r.HandlerId == result.HandlerId);
      if (logged < MAX_COMMIT_FAILURES_LOGGED_PER_FLUSH) {
        _logCommitFailure(result.HandlerId, matching, error);
        logged++;
      } else {
        unlogged++;
      }
      if (matching is not null) {
        await _failureChannel.EnqueueAsync(WorkCategory.Inbox, new MessageFailure {
          MessageId = matching.InboxCompletion.MessageId,
          CompletedStatus = MessageProcessingStatus.None,
          Error = error,
          Reason = MessageFailureReason.Unknown
        }, ct);
      }
    }
    if (unlogged > 0) {
      LogCommitFailuresSummarized(_logger, unlogged, batch.Count);
    }
  }

  private void _logCommitFailure(Guid handlerId, HandlerCommitRequest? matching, string error) {
    if (matching is null) {
      LogCommitFailureWithoutRequest(_logger, handlerId, error);
      return;
    }
    LogCommitFailed(_logger, matching.InboxCompletion.MessageId, matching.MessageType ?? "unknown", error);
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

  // The first wording of this message named the shared WorkCoordinatorGate as the likely culprit for a
  // slow 'coordinator-commit'. That sent two separate investigations at the wrong subsystem: when the
  // stall was finally reproduced under load, the gate was completely uncontended in every sample
  // (permits sat at 47 of 48, never approaching zero, with no waiters) while the commit itself blocked
  // on DATABASE row locks — concurrent commit batches touching overlapping stream-bookkeeping rows in
  // inconsistent order, to the point of blocking each other mutually. A diagnostic that names a
  // plausible-but-wrong cause is worse than one that names none, so this now points at the check that
  // actually discriminates.
  [LoggerMessage(EventId = 25, Level = LogLevel.Warning,
    Message = "InboxHandlerWorker flush phase '{Phase}' took {ElapsedMs}ms for {BatchCount} commit(s) "
            + "— commits are stalling in this phase. Check database lock contention FIRST "
            + "(pg_stat_activity wait_event_type='Lock', and pg_blocking_pids for batches blocking each "
            + "other); the in-process coordinator gate is the less likely cause and is worth ruling out "
            + "by confirming it actually has waiters before assuming it.")]
  static partial void LogFlushPhaseSlow(ILogger logger, string phase, long elapsedMs, int batchCount);

  [LoggerMessage(EventId = 26, Level = LogLevel.Warning,
    Message = "InboxHandlerWorker could not commit inbox message {MessageId} ({MessageType}): {Error}. "
            + "The row stays unprocessed and is routed to the failure channel, which records the error and schedules its retry.")]
  static partial void LogCommitFailed(ILogger logger, Guid messageId, string messageType, string error);

  [LoggerMessage(EventId = 27, Level = LogLevel.Warning,
    Message = "InboxHandlerWorker: {Count} more commit failure(s) in a flush of {BatchCount} are not logged individually; "
            + "each is still routed to the failure channel, which records its error on the row.")]
  static partial void LogCommitFailuresSummarized(ILogger logger, int count, int batchCount);

  [LoggerMessage(EventId = 28, Level = LogLevel.Warning,
    Message = "InboxHandlerWorker received a commit failure for handler {HandlerId} that no request in the flush names: {Error}. "
            + "It cannot be routed to the failure channel, so its row keeps its lease until it lapses.")]
  static partial void LogCommitFailureWithoutRequest(ILogger logger, Guid handlerId, string error);

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
/// <docs>fundamentals/work-coordinator/handler-commit</docs>
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
