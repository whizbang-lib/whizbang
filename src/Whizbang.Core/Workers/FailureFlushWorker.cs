using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Drains failure reports from a bounded channel, batches via <see cref="BatchFlusher{T}"/>,
/// calls <see cref="IWorkCoordinator.ReportFailuresAsync"/> per category. Coalesced flush.
/// Phase C of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public sealed partial class FailureFlushWorker : BackgroundService, IFailureChannel {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly FailureFlushWorkerOptions _options;
  private readonly ILogger<FailureFlushWorker> _logger;
  private readonly BatchFlusher<CategorizedFailure> _flusher;

  /// <summary>Creates the worker and its inner <see cref="BatchFlusher{T}"/> so the channel is writable before <see cref="ExecuteAsync"/> is invoked.</summary>
  public FailureFlushWorker(
    IServiceScopeFactory scopeFactory,
    ISchemaReadyGate schemaReadyGate,
    IOptions<FailureFlushWorkerOptions> options,
    ILogger<FailureFlushWorker> logger) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _flusher = new BatchFlusher<CategorizedFailure>(_flushBatchAsync, _options.Flusher, _logger);
  }

  /// <inheritdoc />
  public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default)
    => _flusher.Writer.WriteAsync(new CategorizedFailure(category, failure), cancellationToken);

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.Enabled) {
      LogDisabled(_logger);
      return Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }
    LogStarted(_logger);
    return _flusher.StoppedSignal;
  }

  private async Task _flushBatchAsync(IReadOnlyList<CategorizedFailure> batch, CancellationToken ct) {
    if (!_options.Enabled) {
      return;
    }
    await _schemaReadyGate.WaitForReadyAsync(ct);
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    foreach (var group in batch.GroupBy(b => b.Category)) {
      var failures = group.Select(g => g.Failure).ToArray();
      await coordinator.ReportFailuresAsync(group.Key, failures, ct);
    }
  }

  /// <inheritdoc />
  public override async Task StopAsync(CancellationToken cancellationToken) {
    await _flusher.DisposeAsync();
    await base.StopAsync(cancellationToken);
    LogStopped(_logger);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "FailureFlushWorker started")]
  static partial void LogStarted(ILogger logger);
  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "FailureFlushWorker stopped")]
  static partial void LogStopped(ILogger logger);
  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "FailureFlushWorker disabled via options — flusher idle")]
  static partial void LogDisabled(ILogger logger);
}

/// <summary>Categorized failure carried by the failure channel.</summary>
public sealed record CategorizedFailure(WorkCategory Category, MessageFailure Failure);

/// <summary>Channel surface for workers to enqueue failures.</summary>
public interface IFailureChannel {
  /// <summary>Enqueue a categorized failure for asynchronous flushing.</summary>
  ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default);
}

/// <summary>Configuration for <see cref="FailureFlushWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class FailureFlushWorkerOptions {
  /// <summary>Killswitch — set to <c>false</c> to halt flushing. Default <c>true</c>.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>Tuning for the inner <see cref="BatchFlusher{T}"/>.</summary>
  public BatchFlusherOptions Flusher { get; set; } = new() {
    MaxBatchSize = 100,
    CoalesceWindowMs = 100,
    ImmediateFlushThreshold = 50,
    ChannelCapacity = 5_000
  };
}
