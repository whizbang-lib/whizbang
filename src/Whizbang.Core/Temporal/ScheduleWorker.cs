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
/// any data provider that registers an <see cref="IScheduleClaimer"/> gets firing for free.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public sealed partial class ScheduleWorker : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly TemporalOptions _options;
  private readonly ILogger<ScheduleWorker> _logger;
  private readonly ISchemaReadyGate? _schemaReadyGate;
  private readonly ISignalBus? _signalBus;
  private ISignalSubscription? _dueSubscription;
  private readonly SemaphoreSlim _wake = new(0, 1);
  private readonly ScheduleTimer _timer;

  /// <summary>Constructor. The schema gate, signal bus, and time provider are optional (defaults suffice).</summary>
  public ScheduleWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<TemporalOptions> options,
    ILogger<ScheduleWorker> logger,
    ISchemaReadyGate? schemaReadyGate = null,
    ISignalBus? signalBus = null,
    TimeProvider? timeProvider = null) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _schemaReadyGate = schemaReadyGate;
    _signalBus = signalBus;

    // The in-memory precise wake: when the earliest owned schedule is due, ring our own doorbell.
    _timer = new ScheduleTimer(timeProvider ?? TimeProvider.System, () => {
      RequestImmediateRun();
      return ValueTask.CompletedTask;
    });

    // Doorbell: a schedule-due signal (arm-on-mutation NOTIFY or the poll-source backstop) wakes the
    // drain immediately instead of waiting for the next backstop tick.
    if (_signalBus is not null) {
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

    if (_schemaReadyGate is not null) {
      try {
        await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
      } catch (OperationCanceledException) {
        return;
      }
    }

    LogStarted(_logger, _options.BackstopIntervalMilliseconds);
    while (!stoppingToken.IsCancellationRequested) {
      try {
        _ = await TickOnceAsync(stoppingToken);
        // Arm the precise wake to the next owned fire time, so the following fire lands on time
        // rather than at backstop latency. Re-armed every cycle (and on each ScheduleDueSignal).
        await ArmTimerOnceAsync(stoppingToken);
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
}
