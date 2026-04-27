using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Drains lease-renewal requests from a bounded channel, batches via <see cref="BatchFlusher{T}"/>,
/// calls <see cref="IWorkCoordinator.RenewLeasesAsync"/> per category. Coalesced flush.
/// Phase C of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public sealed partial class LeaseRenewalWorker : BackgroundService, ILeaseRenewalChannel {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly LeaseRenewalWorkerOptions _options;
  private readonly ILogger<LeaseRenewalWorker> _logger;
  private readonly BatchFlusher<CategorizedLeaseRenewal> _flusher;

  /// <summary>Creates the worker and its inner <see cref="BatchFlusher{T}"/> so the channel is writable before <see cref="ExecuteAsync"/> is invoked.</summary>
  public LeaseRenewalWorker(
    IServiceScopeFactory scopeFactory,
    ISchemaReadyGate schemaReadyGate,
    IOptions<LeaseRenewalWorkerOptions> options,
    ILogger<LeaseRenewalWorker> logger) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _flusher = new BatchFlusher<CategorizedLeaseRenewal>(_flushBatchAsync, _options.Flusher, _logger);
  }

  /// <inheritdoc />
  public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken cancellationToken = default)
    => _flusher.Writer.WriteAsync(new CategorizedLeaseRenewal(category, id), cancellationToken);

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.Enabled) {
      LogDisabled(_logger);
      return Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }
    LogStarted(_logger, _options.LeaseSeconds);
    return _flusher.StoppedSignal;
  }

  private async Task _flushBatchAsync(IReadOnlyList<CategorizedLeaseRenewal> batch, CancellationToken ct) {
    if (!_options.Enabled) {
      return;
    }
    await _schemaReadyGate.WaitForReadyAsync(ct);
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    foreach (var group in batch.GroupBy(b => b.Category)) {
      var ids = group.Select(g => g.Id).ToArray();
      await coordinator.RenewLeasesAsync(group.Key, ids, _options.LeaseSeconds, ct);
    }
  }

  /// <inheritdoc />
  public override async Task StopAsync(CancellationToken cancellationToken) {
    await _flusher.DisposeAsync();
    await base.StopAsync(cancellationToken);
    LogStopped(_logger);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "LeaseRenewalWorker started: leaseSeconds={LeaseSeconds}")]
  static partial void LogStarted(ILogger logger, int leaseSeconds);
  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "LeaseRenewalWorker stopped")]
  static partial void LogStopped(ILogger logger);
  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "LeaseRenewalWorker disabled via options — flusher idle")]
  static partial void LogDisabled(ILogger logger);
}

/// <summary>Categorized lease-renewal request.</summary>
public sealed record CategorizedLeaseRenewal(WorkCategory Category, Guid Id);

/// <summary>Channel surface for workers to enqueue lease renewals.</summary>
public interface ILeaseRenewalChannel {
  /// <summary>Enqueue a (category, id) for asynchronous lease extension.</summary>
  ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Configuration for <see cref="LeaseRenewalWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class LeaseRenewalWorkerOptions {
  /// <summary>Killswitch — set to <c>false</c> to halt flushing. Default <c>true</c>.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>New lease duration applied per renewal call. Default 300 (5 minutes).</summary>
  public int LeaseSeconds { get; set; } = 300;

  /// <summary>Tuning for the inner <see cref="BatchFlusher{T}"/>.</summary>
  public BatchFlusherOptions Flusher { get; set; } = new() {
    MaxBatchSize = 200,
    CoalesceWindowMs = 200,
    ImmediateFlushThreshold = 100,
    ChannelCapacity = 5_000
  };
}
