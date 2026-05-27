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
public sealed partial class OutboxCompletionFlushWorker : BackgroundService, IOutboxCompletionChannel {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly OutboxCompletionFlushWorkerOptions _options;
  private readonly WorkCoordinatorOptions _coordinatorOptions;
  private readonly ILogger<OutboxCompletionFlushWorker> _logger;
  private readonly BatchFlusher<Guid> _flusher;

  /// <summary>Creates the worker and its inner <see cref="BatchFlusher{T}"/> so the channel is writable before <see cref="ExecuteAsync"/> is invoked.</summary>
  public OutboxCompletionFlushWorker(
    IServiceScopeFactory scopeFactory,
    ISchemaReadyGate schemaReadyGate,
    IOptions<OutboxCompletionFlushWorkerOptions> options,
    IOptions<WorkCoordinatorOptions> coordinatorOptions,
    ILogger<OutboxCompletionFlushWorker> logger) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _coordinatorOptions = coordinatorOptions?.Value ?? throw new ArgumentNullException(nameof(coordinatorOptions));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _flusher = new BatchFlusher<Guid>(_flushBatchAsync, _options.Flusher, _logger);
  }

  /// <inheritdoc />
  public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default)
    => _flusher.Writer.WriteAsync(outboxMessageId, cancellationToken);

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.Enabled) {
      LogDisabled(_logger);
      return Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }
    LogStarted(_logger, _options.Flusher.MaxBatchSize, _options.Flusher.CoalesceWindowMs);
    return _flusher.StoppedSignal;
  }

  private async Task _flushBatchAsync(IReadOnlyList<Guid> ids, CancellationToken ct) {
    // Killswitch is honored here too — the inner BatchFlusher loop starts in the ctor and
    // runs independently of ExecuteAsync, so the ExecuteAsync short-circuit alone isn't
    // enough to suppress flushes. Drop the batch silently when disabled.
    if (!_options.Enabled) {
      return;
    }
    // Defense-in-depth: hold flush calls until schema is ready. Producers normally gate
    // themselves, so this rarely matters in practice — but if a custom producer enqueues
    // before the initializer completes, this prevents firing SQL against an unmigrated DB.
    await _schemaReadyGate.WaitForReadyAsync(ct);

    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await coordinator.CompleteOutboxPublishedAsync(ids, _coordinatorOptions.DebugMode, ct);
  }

  /// <inheritdoc />
  public override async Task StopAsync(CancellationToken cancellationToken) {
    await _flusher.DisposeAsync();
    await base.StopAsync(cancellationToken);
    LogStopped(_logger);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "OutboxCompletionFlushWorker started: maxBatchSize={MaxBatchSize}, coalesceMs={CoalesceMs}")]
  static partial void LogStarted(ILogger logger, int maxBatchSize, int coalesceMs);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "OutboxCompletionFlushWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "OutboxCompletionFlushWorker disabled via options — flusher idle")]
  static partial void LogDisabled(ILogger logger);
}

/// <summary>Channel surface for the OutboxPublishWorker (or test) to enqueue completed ids.</summary>
public interface IOutboxCompletionChannel {
  /// <summary>Enqueue an outbox message id whose transport publish succeeded.</summary>
  ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default);
}

/// <summary>Configuration for <see cref="OutboxCompletionFlushWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class OutboxCompletionFlushWorkerOptions {
  /// <summary>
  /// Killswitch. Set to <c>false</c> to halt the flush loop. Producers can still
  /// enqueue (writes go onto the channel), but nothing drains until re-enabled or
  /// the worker is restarted. Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>Tuning for the inner <see cref="BatchFlusher{T}"/>.</summary>
  public BatchFlusherOptions Flusher { get; set; } = new() {
    MaxBatchSize = 500,
    CoalesceWindowMs = 10,
    ImmediateFlushThreshold = 250,
    ChannelCapacity = 10_000
  };
}
