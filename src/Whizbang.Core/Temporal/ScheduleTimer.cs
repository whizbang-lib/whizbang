using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Core.Temporal;

/// <summary>
/// The temporal engine's precise local wake. A single <see cref="TimeProvider"/> timer armed to the
/// earliest upcoming <c>next_fire_at</c> among this instance's owned schedules; when it elapses it rings
/// the doorbell (<c>onDue</c>) so <see cref="ScheduleWorker"/> claims due schedules right at their fire
/// time instead of at backstop latency. The armed time is only a hint about <em>when to attempt</em> — the
/// authoritative fire is always the leased DB claim — so a stale/early/late wake can never double-fire or
/// lose a fire; worst case is a bounded, backstop-caught delay. Re-armed after every drain (to the new
/// minimum) and on arm-on-mutation NOTIFY, so a freshly-created near-term schedule wakes without waiting.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public sealed partial class ScheduleTimer : IDisposable {
  private readonly TimeProvider _timeProvider;
  private readonly Func<ValueTask> _onDue;
  private readonly ILogger<ScheduleTimer> _logger;
  private readonly Lock _gate = new();
  private ITimer? _timer;
  private DateTimeOffset? _armedFor;
  private long _wakeCount;
  private bool _disposed;

  /// <summary>Creates the timer. <paramref name="onDue"/> must be fast/non-blocking (enqueue-and-return).</summary>
  public ScheduleTimer(TimeProvider timeProvider, Func<ValueTask> onDue, ILogger<ScheduleTimer>? logger = null) {
    _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    _onDue = onDue ?? throw new ArgumentNullException(nameof(onDue));
    _logger = logger ?? NullLogger<ScheduleTimer>.Instance;
  }

  /// <summary>The current armed fire time, or <c>null</c> when disarmed. For diagnostics/tests.</summary>
  public DateTimeOffset? ArmedFor {
    get { lock (_gate) { return _armedFor; } }
  }

  /// <summary>Number of times the timer has elapsed and rung the doorbell. OTel/test signal.</summary>
  public long WakeCount => Interlocked.Read(ref _wakeCount);

  /// <summary>
  /// Arm the wake for <paramref name="nextFireAt"/> (the authoritative next minimum), replacing any prior
  /// arming. <c>null</c> disarms (no owned schedule is pending). A time already in the past wakes promptly.
  /// </summary>
  public void ArmFor(DateTimeOffset? nextFireAt) {
    lock (_gate) {
      if (_disposed) {
        return;
      }
      _armedFor = nextFireAt;
      if (nextFireAt is null) {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return;
      }
      var delay = nextFireAt.Value - _timeProvider.GetUtcNow();
      if (delay < TimeSpan.Zero) {
        delay = TimeSpan.Zero;
      }
      _timer ??= _timeProvider.CreateTimer(_onTick, state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
      _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }
  }

  private void _onTick(object? state) {
    lock (_gate) {
      _armedFor = null;   // consumed; the worker re-arms after it drains
    }
    _ = Interlocked.Increment(ref _wakeCount);
    _ = _fireAsync();   // discard the Task — fire-and-forget the doorbell
  }

  private async Task _fireAsync() {
    try {
      await _onDue().ConfigureAwait(false);
    } catch (Exception ex) {
      if (_logger is not null) {
        LogWakeFailed(_logger, ex);
      }
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    lock (_gate) {
      if (_disposed) {
        return;
      }
      _disposed = true;
      _timer?.Dispose();
      _timer = null;
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "ScheduleTimer wake callback failed")]
  private static partial void LogWakeFailed(ILogger logger, Exception ex);
}
