using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Coverage-round-23 target for <see cref="ScheduleTimer"/>: the doorbell's fire-and-forget failure
/// path. <c>_fireAsync</c> discards its <see cref="Task"/> (<c>_ = _fireAsync();</c>) so the timer
/// callback itself never throws — if <c>onDue</c> faults and that fault isn't caught and logged here,
/// the exception becomes unobserved on a thread-pool timer callback, which can crash the process
/// instead of leaving a diagnosable warning. A schedule that silently stops re-arming after one bad
/// wake is indistinguishable from a healthy engine that simply has nothing due next — this log line
/// is the only signal an operator gets that the doorbell itself is broken.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public class ScheduleTimerCoverageTests {
  private static readonly DateTimeOffset _t0 = new(2026, 07, 13, 12, 00, 00, TimeSpan.Zero);

  [Test]
  public async Task ArmFor_OnDueThrows_LogsWakeFailedWithTheExceptionAsync() {
    var clock = new FakeTimeProvider(_t0);
    var logger = new _capturingLogger();
    var boom = new InvalidOperationException("onDue exploded");
    var timer = new ScheduleTimer(clock, () => throw boom, logger);

    timer.ArmFor(_t0.AddSeconds(1));
    // FakeTimeProvider fires due timer callbacks synchronously inside Advance(), and onDue throws
    // synchronously (before any await), so _fireAsync's try/catch completes before Advance returns.
    clock.Advance(TimeSpan.FromSeconds(1));

    await Assert.That(timer.WakeCount).IsEqualTo(1L)
      .Because("the doorbell still rang once even though the handler faulted");
    await Assert.That(logger.Warnings.Count).IsEqualTo(1)
      .Because("a faulted onDue must produce exactly one diagnosable warning, not silence and not a crash");
    await Assert.That(logger.Warnings[0].Exception).IsSameReferenceAs(boom)
      .Because("the operator needs the ORIGINAL exception, not a wrapped/lossy summary, to diagnose the fault");
    timer.Dispose();
  }

  private sealed class _capturingLogger : ILogger<ScheduleTimer> {
    public List<(string Message, Exception? Exception)> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        Warnings.Add((formatter(state, exception), exception));
      }
    }
  }
}
