using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Drains perspective-completion items from a bounded channel, batches via
/// <see cref="BatchFlusher{T}"/>, calls <see cref="IWorkCoordinator.CompletePerspectiveAsync"/>.
/// Combines event-work-id deletions and cursor advancements per batch.
/// Phase C of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public sealed partial class PerspectiveCompletionFlushWorker(
  IServiceScopeFactory scopeFactory,
  IOptions<PerspectiveCompletionFlushWorkerOptions> options,
  ILogger<PerspectiveCompletionFlushWorker> logger
) : BackgroundService, IPerspectiveCompletionChannel {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly PerspectiveCompletionFlushWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<PerspectiveCompletionFlushWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private BatchFlusher<PerspectiveCompletionItem>? _flusher;

  /// <inheritdoc />
  public ValueTask EnqueueEventWorkIdAsync(Guid eventWorkId, CancellationToken cancellationToken = default) {
    if (_flusher is null) {
      throw new InvalidOperationException("PerspectiveCompletionFlushWorker not started");
    }
    return _flusher.Writer.WriteAsync(new PerspectiveCompletionItem(EventWorkId: eventWorkId, Cursor: null), cancellationToken);
  }

  /// <inheritdoc />
  public ValueTask EnqueueCursorAsync(PerspectiveCursorCompletion cursor, CancellationToken cancellationToken = default) {
    if (_flusher is null) {
      throw new InvalidOperationException("PerspectiveCompletionFlushWorker not started");
    }
    return _flusher.Writer.WriteAsync(new PerspectiveCompletionItem(EventWorkId: null, Cursor: cursor), cancellationToken);
  }

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger);
    _flusher = new BatchFlusher<PerspectiveCompletionItem>(_flushBatchAsync, _options.Flusher, _logger);
    return _flusher.StoppedSignal;
  }

  private async Task _flushBatchAsync(IReadOnlyList<PerspectiveCompletionItem> batch, CancellationToken ct) {
    var workIds = batch.Where(i => i.EventWorkId.HasValue).Select(i => i.EventWorkId!.Value).ToArray();
    var cursors = batch.Where(i => i.Cursor is not null).Select(i => i.Cursor!).ToArray();
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await coordinator.CompletePerspectiveAsync(cursors, workIds, ct);
  }

  /// <inheritdoc />
  public override async Task StopAsync(CancellationToken cancellationToken) {
    if (_flusher is not null) {
      await _flusher.DisposeAsync();
    }
    await base.StopAsync(cancellationToken);
    LogStopped(_logger);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "PerspectiveCompletionFlushWorker started")]
  static partial void LogStarted(ILogger logger);
  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "PerspectiveCompletionFlushWorker stopped")]
  static partial void LogStopped(ILogger logger);
}

/// <summary>Discriminated payload carried by the perspective-completion channel.</summary>
public sealed record PerspectiveCompletionItem(Guid? EventWorkId, PerspectiveCursorCompletion? Cursor);

/// <summary>Channel surface for the PerspectiveProcessWorker to enqueue completions.</summary>
public interface IPerspectiveCompletionChannel {
  /// <summary>Enqueue a perspective_event work id to be marked complete (deleted in production mode).</summary>
  ValueTask EnqueueEventWorkIdAsync(Guid eventWorkId, CancellationToken cancellationToken = default);
  /// <summary>Enqueue a cursor advancement.</summary>
  ValueTask EnqueueCursorAsync(PerspectiveCursorCompletion cursor, CancellationToken cancellationToken = default);
}

/// <summary>Configuration for <see cref="PerspectiveCompletionFlushWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class PerspectiveCompletionFlushWorkerOptions {
  /// <summary>Tuning for the inner <see cref="BatchFlusher{T}"/>.</summary>
  public BatchFlusherOptions Flusher { get; set; } = new() {
    MaxBatchSize = 1000,
    CoalesceWindowMs = 25,
    ImmediateFlushThreshold = 500,
    ChannelCapacity = 20_000
  };
}
