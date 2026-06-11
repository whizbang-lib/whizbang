using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Periodic scanner that drives DLQ recovery — fetches DLQ rows that are ready, applies
/// the configured <see cref="IDeadLetterRecoveryPolicy"/>, and either re-emits to the
/// source work table or marks terminal (HoldForReview / PermanentlyFailed).
/// </summary>
/// <remarks>
/// <para>
/// Cadence: <see cref="DeadLetterRecoveryOptions.ScanIntervalMinutes"/> backstop, default
/// 10 min. On startup, runs the generation-replay sweep once so rows from prior
/// generations get exactly one auto-retry on the new build.
/// </para>
/// <para>
/// The worker is process-singleton — multiple replicas all run their scan loops, and they
/// race on each row via the atomic <c>recover_dead_letter</c> SQL (which transitions to
/// Recovering inside its UPDATE). Whichever instance wins drains; the others see false
/// returns and move on. No coordination needed.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/recovery</docs>
public partial class DeadLetterRecoveryWorker(
  IServiceScopeFactory scopeFactory,
  ISchemaReadyGate schemaReadyGate,
  IOptions<DeadLetterRecoveryOptions> options,
  IGenerationProvider generationProvider,
  ILogger<DeadLetterRecoveryWorker> logger,
  DeadLetterMetrics? metrics = null,
  Whizbang.Core.Notifications.IWorkNotificationListener? notificationListener = null
) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly DeadLetterRecoveryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IGenerationProvider _generationProvider = generationProvider ?? throw new ArgumentNullException(nameof(generationProvider));
  private readonly ILogger<DeadLetterRecoveryWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly DeadLetterMetrics? _metrics = metrics;
  private readonly Whizbang.Core.Notifications.IWorkNotificationListener? _notificationListener = notificationListener;
  private readonly SemaphoreSlim _wake = new(0, 1);
  private bool _signalSubscribed;

  /// <summary>NOTIFY signal handler — wakes on a DeadLetterReady signal so the next scan runs within ms.</summary>
  private void _onSignal(Whizbang.Core.Notifications.WorkSignalCategory category) {
    if (category != Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady) {
      return;
    }
    try { _wake.Release(); } catch (SemaphoreFullException) { /* coalesce */ }
  }

  /// <inheritdoc />
  public override Task StopAsync(CancellationToken cancellationToken) {
    if (_signalSubscribed && _notificationListener is not null) {
      _notificationListener.OnSignal -= _onSignal;
      _signalSubscribed = false;
    }
    return base.StopAsync(cancellationToken);
  }

  private long _totalScans;
  private long _totalRecovered;
  private long _totalHeld;
  private long _totalPermanentlyFailed;
  private long _totalGenerationReplays;

  /// <summary>Number of scan cycles since process start.</summary>
  public long TotalScans => Interlocked.Read(ref _totalScans);
  /// <summary>Cumulative count of DLQ rows successfully recovered.</summary>
  public long TotalRecovered => Interlocked.Read(ref _totalRecovered);
  /// <summary>Cumulative count of rows policy-exhausted to HoldForReview.</summary>
  public long TotalHeld => Interlocked.Read(ref _totalHeld);
  /// <summary>Cumulative count of rows policy-exhausted to PermanentlyFailed.</summary>
  public long TotalPermanentlyFailed => Interlocked.Read(ref _totalPermanentlyFailed);
  /// <summary>Total rows scheduled by the generation-replay sweep on startup.</summary>
  public long TotalGenerationReplays => Interlocked.Read(ref _totalGenerationReplays);

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.ScanIntervalMinutes);

    // Slice 7c — subscribe to the DeadLetterReady NOTIFY signal. The wh_dead_letters
    // AFTER INSERT trigger (migration 056) fires this on every new DLQ row so the
    // worker wakes within ms instead of waiting up to ScanIntervalMinutes.
    if (_notificationListener is not null && !_signalSubscribed) {
      _notificationListener.OnSignal += _onSignal;
      _signalSubscribed = true;
    }

    if (!_options.Enabled) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
      return;
    }

    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
    } catch (OperationCanceledException) {
      return;
    }

    // Generation-replay sweep — exactly-once per (row, generation) so this is safe to
    // run on every pod startup.
    if (_options.EnableGenerationReplay) {
      try {
        var current = _generationProvider.GetGeneration();
        using var scope = _scopeFactory.CreateScope();
        // GetService (not GetRequiredService) — when no persistence layer is wired
        // (InMemory samples, unit-test hosts) the worker degrades to a no-op rather
        // than throwing at startup. Same pattern as IDeadLetterStore wiring elsewhere
        // in the DLQ surface.
        var svc = scope.ServiceProvider.GetService<IDeadLetterRecoveryService>();
        if (svc is null) {
          return;
        }
        var scheduled = await svc.ResetForGenerationAsync(current, stoppingToken).ConfigureAwait(false);
        if (scheduled > 0) {
          Interlocked.Add(ref _totalGenerationReplays, scheduled);
          LogGenerationReplay(_logger, scheduled, current);
          _metrics?.GenerationReplayScheduled.Add(scheduled,
            new KeyValuePair<string, object?>("generation", current));
        }
      } catch (Exception ex) {
        LogError(_logger, ex);
      }
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await _scanOnceAsync(stoppingToken);
        Interlocked.Increment(ref _totalScans);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogError(_logger, ex);
      }

      try {
        // Slice 7c — race the polling interval against the NOTIFY-driven wake. When the
        // listener fires, the next scan runs within ms; otherwise the ScanIntervalMinutes
        // backstop poll still kicks in. When no listener is wired the wake task never
        // completes so behaviour collapses to the legacy polling-only loop.
        var pollDelay = Task.Delay(TimeSpan.FromMinutes(_options.ScanIntervalMinutes), stoppingToken);
        var wakeTask = _notificationListener is not null
          ? _wake.WaitAsync(stoppingToken)
          : new TaskCompletionSource<bool>().Task;
        await Task.WhenAny(pollDelay, wakeTask).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      }
    }

    LogStopped(_logger);
  }

  private async Task _scanOnceAsync(CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    // Optional — no persistence layer wired = no scanning. Same as the startup-replay
    // branch above; keeps the worker safe to register everywhere.
    var svc = scope.ServiceProvider.GetService<IDeadLetterRecoveryService>();
    if (svc is null) {
      return;
    }
    var policy = scope.ServiceProvider.GetRequiredService<IDeadLetterRecoveryPolicy>();

    // Fetch in batches — bounded by ScanBatchSize so a single scan doesn't try to drain
    // an enormous backlog in one breath. Subsequent scans pick up where this one stopped.
    var entries = await svc.FetchDueAsync(_options.ScanBatchSize, ct).ConfigureAwait(false);
    if (entries.Count == 0) {
      return;
    }

    foreach (var entry in entries) {
      if (ct.IsCancellationRequested) { return; }
      if (!policy.ShouldRecover(entry)) { continue; }

      var rule = policy.GetPolicy(entry);

      // Exhaustion check first: if RecoveryAttempts already reached MaxRecoveryAttempts,
      // transition to terminal state per HoldForReviewAfterExhaustion.
      if (entry.RecoveryAttempts >= rule.MaxRecoveryAttempts) {
        try {
          if (rule.HoldForReviewAfterExhaustion) {
            await svc.MarkHoldingAsync(entry.DeadLetterId, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _totalHeld);
            LogHeld(_logger, entry.DeadLetterId, rule.Name);
            _metrics?.Held.Add(1,
              new KeyValuePair<string, object?>("policy_name", rule.Name),
              new KeyValuePair<string, object?>("reason", entry.FailureReason.ToString()));
          } else {
            await svc.MarkPermanentlyFailedAsync(entry.DeadLetterId, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _totalPermanentlyFailed);
            LogPermanentlyFailed(_logger, entry.DeadLetterId, rule.Name);
            _metrics?.PermanentlyFailed.Add(1,
              new KeyValuePair<string, object?>("policy_name", rule.Name),
              new KeyValuePair<string, object?>("reason", entry.FailureReason.ToString()));
          }
        } catch (Exception ex) {
          LogTerminalSetFailed(_logger, entry.DeadLetterId, ex);
        }
        continue;
      }

      // Try the recovery.
      _metrics?.RecoveryAttempts.Add(1,
        new KeyValuePair<string, object?>("reason", entry.FailureReason.ToString()));
      try {
        var recovered = await svc.RecoverAsync(entry.DeadLetterId, ct).ConfigureAwait(false);
        if (recovered) {
          Interlocked.Increment(ref _totalRecovered);
          LogRecovered(_logger, entry.DeadLetterId, entry.SourceTable);
          _metrics?.Recovered.Add(1,
            new KeyValuePair<string, object?>("source_table", entry.SourceTable));
        } else {
          // recover_dead_letter returned false — row was already terminal or claimed by
          // another worker. No action needed; the other worker's path handles state.
        }
      } catch (Exception ex) {
        // Recovery failed (DB hiccup, transport blip, etc.). Schedule the next attempt
        // per policy cooldown. The recovery_attempts counter was already bumped by the
        // SQL function's UPDATE; the worker will re-evaluate exhaustion on the next scan.
        LogRecoveryAttemptFailed(_logger, entry.DeadLetterId, ex);
        try {
          await svc.ScheduleNextAttemptAsync(
            entry.DeadLetterId,
            DateTimeOffset.UtcNow.Add(rule.Cooldown),
            ct).ConfigureAwait(false);
        } catch (Exception scheduleEx) {
          LogScheduleFailed(_logger, entry.DeadLetterId, scheduleEx);
        }
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "DeadLetterRecoveryWorker started: scanIntervalMinutes={ScanIntervalMinutes}")]
  static partial void LogStarted(ILogger logger, int scanIntervalMinutes);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "DeadLetterRecoveryWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "DeadLetterRecoveryWorker disabled via options")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "DeadLetterRecoveryWorker scan cycle failed; will retry on next interval")]
  static partial void LogError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information,
    Message = "DeadLetterRecoveryWorker generation-replay scheduled {Count} row(s) for current generation '{Generation}'")]
  static partial void LogGenerationReplay(ILogger logger, int count, string generation);

  [LoggerMessage(EventId = 6, Level = LogLevel.Information,
    Message = "DeadLetterRecoveryWorker recovered DLQ row {DeadLetterId} ({SourceTable})")]
  static partial void LogRecovered(ILogger logger, Guid deadLetterId, string sourceTable);

  [LoggerMessage(EventId = 7, Level = LogLevel.Information,
    Message = "DeadLetterRecoveryWorker held DLQ row {DeadLetterId} for review (policy={PolicyName})")]
  static partial void LogHeld(ILogger logger, Guid deadLetterId, string policyName);

  [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
    Message = "DeadLetterRecoveryWorker permanently-failed DLQ row {DeadLetterId} (policy={PolicyName})")]
  static partial void LogPermanentlyFailed(ILogger logger, Guid deadLetterId, string policyName);

  [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
    Message = "DeadLetterRecoveryWorker recovery attempt failed for {DeadLetterId}; will reschedule")]
  static partial void LogRecoveryAttemptFailed(ILogger logger, Guid deadLetterId, Exception ex);

  [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
    Message = "DeadLetterRecoveryWorker failed to set terminal state for {DeadLetterId}")]
  static partial void LogTerminalSetFailed(ILogger logger, Guid deadLetterId, Exception ex);

  [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
    Message = "DeadLetterRecoveryWorker failed to schedule next attempt for {DeadLetterId}")]
  static partial void LogScheduleFailed(ILogger logger, Guid deadLetterId, Exception ex);
}
