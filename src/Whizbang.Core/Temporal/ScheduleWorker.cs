// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Temporal;

/// <summary>
/// The temporal engine's firing loop. Wakes on a <see cref="ScheduleDueSignal"/> doorbell (the NOTIFY /
/// poll-source fast path) and reconciles on a backstop interval, then drains due schedules through
/// <see cref="IScheduleClaimer"/> — which atomically spawns each occurrence, advances <c>next_fire_at</c>,
/// and logs the run. Provider-agnostic: it depends only on the Core claimer seam and the signal bus, so
/// any data provider that registers an <see cref="IScheduleClaimer"/> gets firing for free. Firing claims a due schedule
/// for this instance, so each tick is a hold of the instance's run permit (#2045): nothing fires while it is closed.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public sealed partial class ScheduleWorker : BackgroundService, Whizbang.Core.RunControl.IObeysRunPermit {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly TemporalOptions _options;
  private readonly ILogger<ScheduleWorker> _logger;
  private readonly ISignalBus _signalBus;
  private readonly ISignalSubscription? _dueSubscription;
  private readonly SemaphoreSlim _wake = new(0, 1);
  private readonly ScheduleTimer _timer;
  private readonly TemporalMetrics? _metrics;
  // The last overdue set reported (#2575), so a warning marks a change instead of repeating every tick.
  private int _reportedOverdueCount;
  private Guid? _reportedOldestOverdue;

  /// <summary>The run permit each tick takes a hold of (#2045).</summary>
  public Whizbang.Core.RunControl.WhizbangRunPermit RunPermit { get; }

  /// <summary>
  /// Constructor. The time provider is optional, and so is the run permit: a worker built without one is under no run
  /// control. The metrics are optional too: without them a stuck schedule is still logged (#2575).
  /// </summary>
  public ScheduleWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<TemporalOptions> options,
    ILogger<ScheduleWorker> logger,
    ILoggerFactory loggerFactory,
    ISignalBus signalBus,
    TimeProvider? timeProvider = null,
    Whizbang.Core.RunControl.WhizbangRunPermit? runPermit = null,
    TemporalMetrics? metrics = null) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    RunPermit = runPermit ?? new Whizbang.Core.RunControl.WhizbangRunPermit();
    _scopeFactory = scopeFactory;
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
    _signalBus = signalBus;
    _metrics = metrics;

    // The in-memory precise wake: when the earliest owned schedule is due, ring our own doorbell.
    _timer = new ScheduleTimer(timeProvider ?? TimeProvider.System, () => {
      RequestImmediateRun();
      return ValueTask.CompletedTask;
    }, loggerFactory.CreateLogger<ScheduleTimer>());

    // Doorbell: a schedule-due signal (arm-on-mutation NOTIFY or the poll-source backstop) wakes the
    // drain immediately instead of waiting for the next backstop tick.
    if (_signalBus.IsConfigured) {
      _dueSubscription = _signalBus.Subscribe<ScheduleDueSignal>(_onScheduleDue);
    }
  }

  private ValueTask _onScheduleDue(ScheduleDueSignal signal) {
    _ = signal;
    RequestImmediateRun();
    return ValueTask.CompletedTask;
  }

  /// <summary>Wake the drain loop now. Idempotent between drains (coalesces multiple doorbells).</summary>
  public void RequestImmediateRun() {
    try {
      _ = _wake.Release();
    } catch (SemaphoreFullException) {
      // A wake is already pending; the next drain will pick up everything due.
    }
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.Enabled) {
      LogDisabled(_logger);
      try {
        await Task.Delay(Timeout.Infinite, stoppingToken);
      } catch (OperationCanceledException) {
        // expected on shutdown
      }
      return;
    }

    LogStarted(_logger, _options.BackstopIntervalMilliseconds);
    while (!stoppingToken.IsCancellationRequested) {
      try {
        _ = await TickOnceAsync(stoppingToken);
        // Arm the precise wake to the next owned fire time, so the following fire lands on time
        // rather than at backstop latency. Re-armed every cycle (and on each ScheduleDueSignal).
        await ArmTimerOnceAsync(stoppingToken);
        // #2575: a schedule no instance fires is reported, not left silent.
        _ = await CheckOverdueOnceAsync(stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogTickFailed(_logger, ex);
      }

      try {
        // Wake early on a ScheduleDueSignal or the in-memory timer; otherwise reconcile at the backstop.
        _ = await _wake.WaitAsync(TimeSpan.FromMilliseconds(_options.BackstopIntervalMilliseconds), stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }
    LogStopped(_logger);
  }

  /// <summary>
  /// One drain pass: claim due schedules through the resolved <see cref="IScheduleClaimer"/> until a
  /// claim fires fewer than the batch limit (nothing more due). Returns the total occurrences fired.
  /// A no-op returning 0 when no claimer is registered (non-temporal host). Public test seam.
  /// </summary>
  public async Task<int> TickOnceAsync(CancellationToken cancellationToken = default) {
    using var hold = await RunPermit.EnterAsync(cancellationToken).ConfigureAwait(false);
    using var scope = _scopeFactory.CreateScope();
    var claimer = scope.ServiceProvider.GetService<IScheduleClaimer>();
    if (claimer is null) {
      return 0;
    }

    var total = 0;
    int fired;
    do {
      fired = await claimer.ClaimDueSchedulesAsync(_options.ClaimBatchLimit, cancellationToken);
      total += fired;
    } while (fired >= _options.ClaimBatchLimit && !cancellationToken.IsCancellationRequested);
    return total;
  }

  /// <summary>
  /// Query the earliest owned fire time and arm the in-memory timer to it (replacing any prior arming),
  /// so the timer rings the doorbell exactly then. No-op when no claimer is registered. Public test seam.
  /// </summary>
  public async Task ArmTimerOnceAsync(CancellationToken cancellationToken = default) {
    using var scope = _scopeFactory.CreateScope();
    var claimer = scope.ServiceProvider.GetService<IScheduleClaimer>();
    if (claimer is null) {
      return;
    }
    var next = await claimer.GetNextFireTimeAsync(cancellationToken);
    _timer.ArmFor(next);
  }

  /// <summary>
  /// Reads the Active schedules overdue beyond <see cref="TemporalOptions.OverdueWarningMilliseconds"/>, whoever owns
  /// them, publishes the reading on <see cref="TemporalMetrics"/>, and logs a warning naming the oldest when the
  /// overdue set appears or changes, and once when it clears (#2575). Returns the reading, or <c>null</c> when the
  /// check is off, there is no claimer, or the reading is unknown. Public test seam, run on every drain pass.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token; its cancellation propagates, any other failure is logged.</param>
  /// <returns>The reading, or null.</returns>
  /// <docs>fundamentals/temporal/temporal-engine#a-schedule-that-does-not-fire-is-reported</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Temporal/ScheduleWorkerOverdueTests.cs</tests>
  public async Task<ScheduleOverdueReading?> CheckOverdueOnceAsync(CancellationToken cancellationToken = default) {
    var bound = TimeSpan.FromMilliseconds(_options.OverdueWarningMilliseconds);
    if (bound <= TimeSpan.Zero) {
      return null;
    }
    using var scope = _scopeFactory.CreateScope();
    var claimer = scope.ServiceProvider.GetService<IScheduleClaimer>();
    if (claimer is null) {
      return null;
    }

    ScheduleOverdueReading? reading;
    try {
      reading = await claimer.GetOverdueSchedulesAsync(bound, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
      // A failure, or a cancellation that is not ours (a command timeout), is an unknown reading; the next pass retries.
      LogOverdueReadFailed(_logger, ex);
      reading = null;
    }
    _metrics?.Record(reading);
    if (reading is { } known) {
      _report(known, bound);
    }
    return reading;
  }

  private void _report(ScheduleOverdueReading reading, TimeSpan bound) {
    if (reading.Count > 0) {
      if (reading.Count != _reportedOverdueCount || reading.OldestScheduleId != _reportedOldestOverdue) {
        LogSchedulesOverdue(_logger, reading.Count, bound.TotalSeconds, reading.OldestScheduleKey,
          reading.OldestScheduleId, reading.OldestAge.TotalSeconds);
      }
    } else if (_reportedOverdueCount > 0) {
      LogSchedulesOverdueCleared(_logger);
    }
    _reportedOverdueCount = reading.Count;
    _reportedOldestOverdue = reading.OldestScheduleId;
  }

  /// <summary>Test seam: whether the worker was given its overdue gauges.</summary>
  internal bool HasMetricsForTests => _metrics is not null;

  /// <summary>Test seam: consumes and reports whether a wake is currently pending.</summary>
  internal bool TryConsumeWakeForTests() => _wake.Wait(0);

  /// <summary>Test seam: the in-memory timer (inspect <see cref="ScheduleTimer.ArmedFor"/> / WakeCount).</summary>
  internal ScheduleTimer TimerForTests => _timer;

  /// <inheritdoc />
  public override void Dispose() {
    _dueSubscription?.Dispose();
    _timer.Dispose();
    _wake.Dispose();
    base.Dispose();
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "ScheduleWorker started (backstop {IntervalMs} ms)")]
  private static partial void LogStarted(ILogger logger, int intervalMs);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "ScheduleWorker disabled via TemporalOptions.Enabled=false")]
  private static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "ScheduleWorker drain pass failed; will retry on next tick")]
  private static partial void LogTickFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "ScheduleWorker stopped")]
  private static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "{Count} Active schedule(s) are overdue by more than {BoundSeconds} s and no instance has fired them; the oldest, '{ScheduleKey}' ({ScheduleId}), was due {AgeSeconds} s ago")]
  private static partial void LogSchedulesOverdue(
    ILogger logger, int count, double boundSeconds, string? scheduleKey, Guid? scheduleId, double ageSeconds);

  [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "No Active schedule is overdue any longer")]
  private static partial void LogSchedulesOverdueCleared(ILogger logger);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "Could not read the overdue schedules; the next drain pass retries")]
  private static partial void LogOverdueReadFailed(ILogger logger, Exception exception);
}
