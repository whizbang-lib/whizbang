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
  private readonly LeaseRegistry? _leaseRegistry;
  private readonly TimeProvider _timeProvider;
  private readonly IPinnedConnectionPool _pinnedPool;
  private readonly BatchFlusher<CategorizedLeaseRenewal> _flusher;

  /// <summary>Creates the worker and its inner <see cref="BatchFlusher{T}"/> so the channel is writable before <see cref="ExecuteAsync"/> is invoked.</summary>
  public LeaseRenewalWorker(
    IServiceScopeFactory scopeFactory,
    ISchemaReadyGate schemaReadyGate,
    IOptions<LeaseRenewalWorkerOptions> options,
    ILogger<LeaseRenewalWorker> logger,
    LeaseRegistry? leaseRegistry = null,
    TimeProvider? timeProvider = null,
    IPinnedConnectionPool? pinnedPool = null) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _leaseRegistry = leaseRegistry;
    _timeProvider = timeProvider ?? TimeProvider.System;
    _pinnedPool = pinnedPool ?? NoOpPinnedConnectionPool.Instance;
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
    await using var pin = await _pinnedPool.TryPinForAsync(typeof(LeaseRenewalWorker), ct);
    using var __ctx = PinnedConnectionContext.Push(pin.Connection);
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    foreach (var group in batch.GroupBy(b => b.Category)) {
      // Phase H step 9 slice 6: filter out work_ids whose LeaseHandle has hit MaxRenewalsPerWork
      // (or has been disposed). Skipping the DB renewal lets the SQL lease expire naturally —
      // claim_orphaned_* then re-issues with bumped attempts (step 8). This is what surfaces a
      // hung handler: without the cap, every renewal cycle silently extends the lease forever.
      var idsToRenew = new List<Guid>(group.Count());
      var newDeadline = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(_options.LeaseSeconds);
      foreach (var item in group) {
        if (_leaseRegistry is null) {
          // No registry wired — fall back to legacy behavior: always renew.
          idsToRenew.Add(item.Id);
          continue;
        }
        if (!_leaseRegistry.TryGet(item.Category, item.Id, out var handle) || handle is null) {
          LogHandleNotFound(_logger, item.Category, item.Id);
          continue;
        }
        if (!handle.TryExtendDeadline(newDeadline)) {
          // Cap hit (or disposed). Stop submitting this work_id to RenewLeasesAsync — DB lease
          // expires naturally, claim_orphaned re-issues, attempts bumps.
          LogMaxRenewalsHit(_logger, item.Category, item.Id, handle.RenewalCount);
          continue;
        }
        idsToRenew.Add(item.Id);
      }

      if (idsToRenew.Count > 0) {
        await coordinator.RenewLeasesAsync(group.Key, idsToRenew, _options.LeaseSeconds, ct);
      }
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

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "LeaseRenewalWorker: max renewals hit for {Category}/{WorkId} after {RenewalCount} extensions — letting DB lease expire so claim_orphaned can re-issue")]
  static partial void LogMaxRenewalsHit(ILogger logger, WorkCategory category, Guid workId, int renewalCount);

  [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "LeaseRenewalWorker: handle not found in registry for {Category}/{WorkId} — race with disposal, skipping")]
  static partial void LogHandleNotFound(ILogger logger, WorkCategory category, Guid workId);
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
