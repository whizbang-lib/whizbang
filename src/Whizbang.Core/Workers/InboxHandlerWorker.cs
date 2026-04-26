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
public sealed partial class InboxHandlerWorker(
  IServiceScopeFactory scopeFactory,
  IFailureChannel failureChannel,
  IOptions<InboxHandlerWorkerOptions> options,
  ILogger<InboxHandlerWorker> logger
) : BackgroundService, IInboxHandlerCommitChannel {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IFailureChannel _failureChannel = failureChannel ?? throw new ArgumentNullException(nameof(failureChannel));
  private readonly InboxHandlerWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<InboxHandlerWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private BatchFlusher<HandlerCommitRequest>? _flusher;

  /// <inheritdoc />
  public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    if (_flusher is null) {
      throw new InvalidOperationException("InboxHandlerWorker not started");
    }
    return _flusher.Writer.WriteAsync(request, cancellationToken);
  }

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.Flusher.MaxBatchSize, _options.Flusher.CoalesceWindowMs);
    _flusher = new BatchFlusher<HandlerCommitRequest>(_flushBatchAsync, _options.Flusher, _logger);
    return _flusher.StoppedSignal;
  }

  private async Task _flushBatchAsync(IReadOnlyList<HandlerCommitRequest> batch, CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    var results = await coordinator.CommitHandlerBatchAsync(batch, ct);

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
    if (_flusher is not null) {
      await _flusher.DisposeAsync();
    }
    await base.StopAsync(cancellationToken);
    LogStopped(_logger);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "InboxHandlerWorker started: maxBatchSize={MaxBatchSize}, coalesceMs={CoalesceMs}")]
  static partial void LogStarted(ILogger logger, int maxBatchSize, int coalesceMs);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "InboxHandlerWorker stopped")]
  static partial void LogStopped(ILogger logger);
}

/// <summary>Channel surface for handler-result producers (the inbox dispatch path).</summary>
public interface IInboxHandlerCommitChannel {
  /// <summary>Enqueue a completed handler bundle for batched commit.</summary>
  ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Configuration for <see cref="InboxHandlerWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class InboxHandlerWorkerOptions {
  /// <summary>Tuning for the inner <see cref="BatchFlusher{T}"/>.</summary>
  public BatchFlusherOptions Flusher { get; set; } = new() {
    MaxBatchSize = 100,
    CoalesceWindowMs = 25,
    ImmediateFlushThreshold = 50,
    ChannelCapacity = 5_000
  };
}
