using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;

#pragma warning disable IDE0290  // Allow explicit constructor for optional channel writers

namespace Whizbang.Core.Workers;

/// <summary>
/// The polling worker. The only place that calls <see cref="IWorkCoordinator.ClaimWorkAsync"/>.
/// Adaptive backoff on consecutive empty polls; wake semaphore lets external producers
/// (NOTIFY listener, local channel writes) interrupt the wait so burst latency stays low.
/// Distributes claimed work to the existing channel writers.
/// Phase C of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
public sealed partial class ClaimWorker : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IWorkNotificationListener _notificationListener;
  private readonly IWorkChannelWriter? _outboxChannel;
  private readonly IInboxChannelWriter? _inboxChannel;
  private readonly IPerspectiveChannelWriter? _perspectiveChannel;
  private readonly IPerspectiveDrainChannel? _perspectiveDrainChannel;
  private readonly ClaimWorkerOptions _options;
  private readonly ILogger<ClaimWorker> _logger;
  private readonly SemaphoreSlim _wake = new(0, 1);
  private int _consecutiveEmptyPolls;

  /// <summary>Constructor.</summary>
  public ClaimWorker(
    IServiceScopeFactory scopeFactory,
    IServiceInstanceProvider instanceProvider,
    IWorkNotificationListener notificationListener,
    IOptions<ClaimWorkerOptions> options,
    ILogger<ClaimWorker> logger,
    IWorkChannelWriter? outboxChannel = null,
    IInboxChannelWriter? inboxChannel = null,
    IPerspectiveChannelWriter? perspectiveChannel = null,
    IPerspectiveDrainChannel? perspectiveDrainChannel = null) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _notificationListener = notificationListener ?? throw new ArgumentNullException(nameof(notificationListener));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _outboxChannel = outboxChannel;
    _inboxChannel = inboxChannel;
    _perspectiveChannel = perspectiveChannel;
    _perspectiveDrainChannel = perspectiveDrainChannel;

    // Subscribe to outbox/inbox signals only — perspective signals route to PerspectiveProcessWorker.
    _notificationListener.OnSignal += _onSignal;
  }

  private void _onSignal(WorkSignalCategory category) {
    if (category is WorkSignalCategory.Outbox or WorkSignalCategory.Inbox) {
      RequestImmediatePoll();
    }
  }

  /// <summary>
  /// Observable: the most recent <see cref="WorkBatch"/> distributed by the worker.
  /// Set whenever a tick produces a non-empty batch. Useful for wiring up downstream
  /// consumers in tests.
  /// </summary>
  public event Action<WorkBatch>? OnBatchClaimed;

  /// <summary>External wake — call from notification listener or local channel writer.</summary>
  public void RequestImmediatePoll() {
    if (_wake.CurrentCount == 0) {
      try { _wake.Release(); } catch (SemaphoreFullException) { /* already pending */ }
    }
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.PollingIntervalMilliseconds, _options.PollingMaxIntervalMilliseconds, _instanceProvider.InstanceId);

    while (!stoppingToken.IsCancellationRequested) {
      try {
        var batch = await _claimOnceAsync(stoppingToken);
        var hadWork = batch.OutboxWork.Count > 0 || batch.InboxWork.Count > 0 || batch.PerspectiveStreamIds.Count > 0;

        if (hadWork) {
          _consecutiveEmptyPolls = 0;
          await _distributeAsync(batch, stoppingToken);
          OnBatchClaimed?.Invoke(batch);
        } else {
          Interlocked.Increment(ref _consecutiveEmptyPolls);
        }
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogError(_logger, ex);
        Interlocked.Increment(ref _consecutiveEmptyPolls);  // back off after errors too
      }

      var waitMs = _computeAdaptivePollWaitMs();
      try {
        _ = await _wake.WaitAsync(TimeSpan.FromMilliseconds(waitMs), stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }

    LogStopped(_logger);
  }

  private async Task _distributeAsync(WorkBatch batch, CancellationToken ct) {
    if (_outboxChannel is not null) {
      foreach (var ow in batch.OutboxWork) {
        await _outboxChannel.WriteAsync(ow, ct);
      }
    }
    if (_inboxChannel is not null) {
      foreach (var iw in batch.InboxWork) {
        await _inboxChannel.WriteAsync(iw, ct);
      }
    }
    if (_perspectiveChannel is not null) {
      foreach (var pw in batch.PerspectiveWork) {
        await _perspectiveChannel.WriteAsync(pw, ct);
      }
    }
    if (_perspectiveDrainChannel is not null) {
      foreach (var streamId in batch.PerspectiveStreamIds) {
        await _perspectiveDrainChannel.WriteAsync(streamId, ct);
      }
    }
  }

  private async Task<WorkBatch> _claimOnceAsync(CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    return await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
      InstanceId: _instanceProvider.InstanceId,
      ServiceName: _instanceProvider.ServiceName,
      HostName: _instanceProvider.HostName,
      ProcessId: _instanceProvider.ProcessId,
      MaxStreams: _options.MaxStreamsPerBatch,
      PartitionCount: _options.PartitionCount,
      LeaseSeconds: _options.LeaseSeconds), ct);
  }

  private int _computeAdaptivePollWaitMs() {
    var baseMs = _options.PollingIntervalMilliseconds;
    var maxMs = _options.PollingMaxIntervalMilliseconds;
    var empty = Volatile.Read(ref _consecutiveEmptyPolls);
    if (maxMs <= baseMs || empty <= 0) {
      return baseMs;
    }
    var shift = Math.Min(empty - 1, 10);
    var doubled = (long)baseMs << shift;
    return (int)Math.Min(doubled, maxMs);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "ClaimWorker started: pollMs={PollMs}, maxBackoffMs={MaxMs}, instance={InstanceId}")]
  static partial void LogStarted(ILogger logger, int pollMs, int maxMs, Guid instanceId);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "ClaimWorker tick failed; will back off and retry")]
  static partial void LogError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "ClaimWorker stopped")]
  static partial void LogStopped(ILogger logger);
}

/// <summary>Configuration for <see cref="ClaimWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class ClaimWorkerOptions {
  /// <summary>Base polling cadence in ms. Default 250.</summary>
  public int PollingIntervalMilliseconds { get; set; } = 250;
  /// <summary>Adaptive backoff cap in ms. Default 10 000 (10 s).
  /// Constrained at startup to <c>AbandonStaleInstanceThresholdSeconds × 1000 / 3</c>
  /// to preserve heartbeat-budget freshness.</summary>
  public int PollingMaxIntervalMilliseconds { get; set; } = 10_000;
  /// <summary>Cap on rows returned per claim_work call. Default 1000.</summary>
  public int MaxStreamsPerBatch { get; set; } = 1000;
  /// <summary>Modulo partition count. Default 10000.</summary>
  public int PartitionCount { get; set; } = 10_000;
  /// <summary>Lease duration applied to claimed work. Default 300 s.</summary>
  public int LeaseSeconds { get; set; } = 300;
}
