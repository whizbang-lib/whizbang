using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Drains outbox-completion ids (one per successfully-published outbox message)
/// from a bounded channel, batches them via <see cref="BatchFlusher{T}"/>, and calls
/// <see cref="IWorkCoordinator.CompleteOutboxPublishedAsync"/> per batch.
/// Phase C of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public sealed partial class OutboxCompletionFlushWorker(
  IServiceScopeFactory scopeFactory,
  IOptions<OutboxCompletionFlushWorkerOptions> options,
  ILogger<OutboxCompletionFlushWorker> logger
) : BackgroundService, IOutboxCompletionChannel {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly OutboxCompletionFlushWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<OutboxCompletionFlushWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private BatchFlusher<Guid>? _flusher;

  /// <inheritdoc />
  public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) {
    if (_flusher is null) {
      throw new InvalidOperationException("OutboxCompletionFlushWorker not started");
    }
    return _flusher.Writer.WriteAsync(outboxMessageId, cancellationToken);
  }

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.Flusher.MaxBatchSize, _options.Flusher.CoalesceWindowMs);

    _flusher = new BatchFlusher<Guid>(
      flush: _flushBatchAsync,
      options: _options.Flusher,
      logger: _logger);

    return _flusher.StoppedSignal;
  }

  private async Task _flushBatchAsync(IReadOnlyList<Guid> ids, CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await coordinator.CompleteOutboxPublishedAsync(ids, ct);
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
    Message = "OutboxCompletionFlushWorker started: maxBatchSize={MaxBatchSize}, coalesceMs={CoalesceMs}")]
  static partial void LogStarted(ILogger logger, int maxBatchSize, int coalesceMs);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "OutboxCompletionFlushWorker stopped")]
  static partial void LogStopped(ILogger logger);
}

/// <summary>Channel surface for the OutboxPublishWorker (or test) to enqueue completed ids.</summary>
public interface IOutboxCompletionChannel {
  /// <summary>Enqueue an outbox message id whose transport publish succeeded.</summary>
  ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default);
}

/// <summary>Configuration for <see cref="OutboxCompletionFlushWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class OutboxCompletionFlushWorkerOptions {
  /// <summary>Tuning for the inner <see cref="BatchFlusher{T}"/>.</summary>
  public BatchFlusherOptions Flusher { get; set; } = new() {
    MaxBatchSize = 500,
    CoalesceWindowMs = 10,
    ImmediateFlushThreshold = 250,
    ChannelCapacity = 10_000
  };
}
