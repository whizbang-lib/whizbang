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
public sealed partial class LeaseRenewalWorker(
  IServiceScopeFactory scopeFactory,
  IOptions<LeaseRenewalWorkerOptions> options,
  ILogger<LeaseRenewalWorker> logger
) : BackgroundService, ILeaseRenewalChannel {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly LeaseRenewalWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<LeaseRenewalWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private BatchFlusher<CategorizedLeaseRenewal>? _flusher;

  /// <inheritdoc />
  public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken cancellationToken = default) {
    if (_flusher is null) {
      throw new InvalidOperationException("LeaseRenewalWorker not started");
    }
    return _flusher.Writer.WriteAsync(new CategorizedLeaseRenewal(category, id), cancellationToken);
  }

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.LeaseSeconds);
    _flusher = new BatchFlusher<CategorizedLeaseRenewal>(_flushBatchAsync, _options.Flusher, _logger);
    return _flusher.StoppedSignal;
  }

  private async Task _flushBatchAsync(IReadOnlyList<CategorizedLeaseRenewal> batch, CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    foreach (var group in batch.GroupBy(b => b.Category)) {
      var ids = group.Select(g => g.Id).ToArray();
      await coordinator.RenewLeasesAsync(group.Key, ids, _options.LeaseSeconds, ct);
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

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "LeaseRenewalWorker started: leaseSeconds={LeaseSeconds}")]
  static partial void LogStarted(ILogger logger, int leaseSeconds);
  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "LeaseRenewalWorker stopped")]
  static partial void LogStopped(ILogger logger);
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
