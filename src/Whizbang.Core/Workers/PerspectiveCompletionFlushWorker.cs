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
public sealed partial class PerspectiveCompletionFlushWorker : BackgroundService, IPerspectiveCompletionChannel {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly PerspectiveCompletionFlushWorkerOptions _options;
  private readonly WorkCoordinatorOptions _coordinatorOptions;
  private readonly ILogger<PerspectiveCompletionFlushWorker> _logger;
  private readonly BatchFlusher<PerspectiveCompletionItem> _flusher;

  /// <summary>Creates the worker and its inner <see cref="BatchFlusher{T}"/> so the channel is writable before <see cref="ExecuteAsync"/> is invoked.</summary>
  public PerspectiveCompletionFlushWorker(
    IServiceScopeFactory scopeFactory,
    ISchemaReadyGate schemaReadyGate,
    IOptions<PerspectiveCompletionFlushWorkerOptions> options,
    IOptions<WorkCoordinatorOptions> coordinatorOptions,
    ILogger<PerspectiveCompletionFlushWorker> logger) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _coordinatorOptions = coordinatorOptions?.Value ?? throw new ArgumentNullException(nameof(coordinatorOptions));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _flusher = new BatchFlusher<PerspectiveCompletionItem>(_flushBatchAsync, _options.Flusher, _logger);
  }

  /// <inheritdoc />
  public ValueTask EnqueueEventWorkIdAsync(Guid eventWorkId, CancellationToken cancellationToken = default)
    => _flusher.Writer.WriteAsync(new PerspectiveCompletionItem(EventWorkId: eventWorkId, Cursor: null), cancellationToken);

  /// <inheritdoc />
  public ValueTask EnqueueCursorAsync(PerspectiveCursorCompletion cursor, CancellationToken cancellationToken = default)
    => _flusher.Writer.WriteAsync(new PerspectiveCompletionItem(EventWorkId: null, Cursor: cursor), cancellationToken);

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.Enabled) {
      LogDisabled(_logger);
      return Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }
    LogStarted(_logger);
    return _flusher.StoppedSignal;
  }

  private async Task _flushBatchAsync(IReadOnlyList<PerspectiveCompletionItem> batch, CancellationToken ct) {
    if (!_options.Enabled) {
      return;
    }
    await _schemaReadyGate.WaitForReadyAsync(ct);
    var workIds = batch.Where(i => i.EventWorkId.HasValue).Select(i => i.EventWorkId!.Value).ToArray();
    var cursors = batch.Where(i => i.Cursor is not null).Select(i => i.Cursor!).ToArray();
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await coordinator.CompletePerspectiveAsync(cursors, workIds, _coordinatorOptions.DebugMode, ct);

    // Slice 1: opportunistic stream eviction. After completions land in DB, distinct
    // stream_ids from the cursors might now have empty pending-work tables and can
    // exit wh_active_streams so the next event for that stream rebinds via the
    // store_*_messages UPSERT. The SQL function self-checks pending work — passing a
    // stream that still has work is safe (no-op DELETE).
    if (cursors.Length > 0) {
      var streamIds = cursors.Select(c => c.StreamId).Distinct().ToArray();
      try {
        await coordinator.CleanupCompletedStreamsAsync(streamIds, ct);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        // Eviction is opportunistic — failures are non-fatal. MaintenanceWorker provides
        // the backstop on its IntervalMinutes cadence.
        LogCleanupFailed(_logger, ex);
      }
    }
  }

  /// <inheritdoc />
  public override async Task StopAsync(CancellationToken cancellationToken) {
    await _flusher.DisposeAsync();
    await base.StopAsync(cancellationToken);
    LogStopped(_logger);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "PerspectiveCompletionFlushWorker started")]
  static partial void LogStarted(ILogger logger);
  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "PerspectiveCompletionFlushWorker stopped")]
  static partial void LogStopped(ILogger logger);
  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "PerspectiveCompletionFlushWorker disabled via options — flusher idle")]
  static partial void LogDisabled(ILogger logger);
  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "PerspectiveCompletionFlushWorker: cleanup_completed_streams call failed; eviction will retry on MaintenanceWorker backstop")]
  static partial void LogCleanupFailed(ILogger logger, Exception ex);
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
  /// <summary>Killswitch — set to <c>false</c> to halt flushing. Default <c>true</c>.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>Tuning for the inner <see cref="BatchFlusher{T}"/>.</summary>
  public BatchFlusherOptions Flusher { get; set; } = new() {
    MaxBatchSize = 1000,
    CoalesceWindowMs = 25,
    ImmediateFlushThreshold = 500,
    ChannelCapacity = 20_000
  };
}
