using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Notifications;

namespace Whizbang.Core.Workers;

/// <summary>
/// Hosted service that runs all registered "backup" ticks on a configurable
/// cadence, but only when the system has been idle long enough that
/// NOTIFY-driven activity isn't expected. Slice 4 of zero-idle-polling.
/// </summary>
/// <remarks>
/// <para>
/// State machine:
/// </para>
/// <code>
/// [ASLEEP]  ──(TimeSinceLastActivity ≥ IdleThreshold)──►  [POLLING]
/// [POLLING] ──(any registered hook fires Touch)────────►  [ASLEEP]
/// </code>
/// <para>
/// In ASLEEP the coordinator sleeps for
/// <c>IdleThreshold - TimeSinceLastActivity</c> — zero DB calls. In POLLING
/// it iterates <see cref="IBackupTickRegistry.Registrations"/> on every
/// cycle and sleeps for <see cref="BackupTickCoordinatorOptions.PollingInterval"/>
/// (or the faster <see cref="BackupTickCoordinatorOptions.FastPollingInterval"/>
/// when <see cref="INotifySignalingGate.IsAvailable"/> reports NOTIFY broken).
/// </para>
/// <para>
/// Per-tick try/catch isolates failures: one registered tick throwing must
/// never prevent the others from firing on the same cycle.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/backup-tick-coordinator</docs>
/// <tests>Whizbang.Core.Tests/Workers/BackupTickCoordinatorTests.cs</tests>
public sealed partial class BackupTickCoordinator(
  IIdleActivityTracker tracker,
  IBackupTickRegistry registry,
  IOptions<BackupTickCoordinatorOptions> options,
  ILogger<BackupTickCoordinator> logger,
  INotifySignalingGate? gate = null,
  TimeProvider? timeProvider = null
) : BackgroundService {
  private readonly IIdleActivityTracker _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
  private readonly IBackupTickRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
  private readonly BackupTickCoordinatorOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<BackupTickCoordinator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotifySignalingGate? _gate = gate;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  private long _totalTickCycles;
  private long _totalDelegateInvocations;
  private long _totalDelegateFailures;

  /// <summary>Cumulative count of POLLING cycles since process start. Exposed for tests + observability.</summary>
  public long TotalTickCycles => Interlocked.Read(ref _totalTickCycles);

  /// <summary>Cumulative count of individual delegate invocations (across all registrations, all cycles).</summary>
  public long TotalDelegateInvocations => Interlocked.Read(ref _totalDelegateInvocations);

  /// <summary>Cumulative count of delegate invocations that threw an exception.</summary>
  public long TotalDelegateFailures => Interlocked.Read(ref _totalDelegateFailures);

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.Enabled) {
      LogDisabled(_logger);
      return;
    }

    LogStarted(_logger, _options.IdleThreshold, _options.PollingInterval, _options.FastPollingInterval);

    try {
      while (!stoppingToken.IsCancellationRequested) {
        var idle = _tracker.TimeSinceLastActivity;
        if (idle < _options.IdleThreshold) {
          // ASLEEP — sleep just long enough for the idle threshold to elapse,
          // then re-check on the next iteration. Zero DB calls in this branch.
          var sleep = _options.IdleThreshold - idle;
          await Task.Delay(sleep, _timeProvider, stoppingToken).ConfigureAwait(false);
          continue;
        }
        // POLLING — fire every registered enabled tick once, then sleep.
        // A Touch from any hook during the cycle resets the tracker; the next
        // ASLEEP-branch check will see the tracker fresh and transition back.
        _ = await FireOneCycleAsync(stoppingToken).ConfigureAwait(false);
        var interval = ComputeEffectivePollingInterval();
        await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
      // Normal shutdown.
    }

    LogStopped(_logger);
  }

  // Fires every registered tick once. Each tick runs inside its own try/catch
  // so a failure in one delegate can't prevent the others from firing on the
  // same cycle. Returns the count of delegates actually invoked (excluding
  // those whose isEnabled() predicate returned false).
  internal async Task<int> FireOneCycleAsync(CancellationToken stoppingToken) {
    var invoked = 0;
    foreach (var reg in _registry.Registrations) {
      if (stoppingToken.IsCancellationRequested) {
        break;
      }
      if (!reg.IsEnabled()) {
        continue;
      }
      invoked++;
      _ = Interlocked.Increment(ref _totalDelegateInvocations);
      try {
        await reg.Tick(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        // Shutdown propagation — break the loop, don't count as failure.
        break;
      } catch (Exception ex) {
        _ = Interlocked.Increment(ref _totalDelegateFailures);
        LogTickFailed(_logger, reg.Name, ex);
      }
    }
    _ = Interlocked.Increment(ref _totalTickCycles);
    LogTickCycleComplete(_logger, invoked);
    return invoked;
  }

  // Effective polling cadence given the gate state. Public-internal so tests
  // can call it directly without spinning up the BackgroundService loop.
  internal TimeSpan ComputeEffectivePollingInterval() {
    return _gate?.IsAvailable == false ? _options.FastPollingInterval : _options.PollingInterval;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "BackupTickCoordinator started: idleThreshold={IdleThreshold}, pollingInterval={PollingInterval}, fastPollingInterval={FastPollingInterval}")]
  static partial void LogStarted(ILogger logger, TimeSpan idleThreshold, TimeSpan pollingInterval, TimeSpan fastPollingInterval);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "BackupTickCoordinator disabled via options — coordinator loop skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "BackupTickCoordinator stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "BackupTickCoordinator tick cycle complete: invoked={Invoked}")]
  static partial void LogTickCycleComplete(ILogger logger, int invoked);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "BackupTickCoordinator registered tick '{Name}' threw — other ticks continue")]
  static partial void LogTickFailed(ILogger logger, string name, Exception ex);
}
