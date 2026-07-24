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
      return;
    }
    LogDiagFlushEntered(_logger, batch.Count);
    await _schemaReadyGate.WaitForReadyAsync(ct);
    LogDiagFlushSchemaReady(_logger, batch.Count);
    await using var pin = await _pinnedPool.TryPinForAsync(typeof(InboxHandlerWorker), ct);
    using var __ctx = PinnedConnectionContext.Push(pin.Connection);
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    var results = await coordinator.CommitHandlerBatchAsync(batch, ct);
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
