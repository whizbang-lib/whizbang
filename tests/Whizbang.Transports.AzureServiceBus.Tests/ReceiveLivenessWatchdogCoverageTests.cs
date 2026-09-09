using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Coverage for the periodic loop's own two exception-handling branches: the cancellation-filtered
/// catch that lets a shutdown mid-recovery exit cleanly, and the catch-all that lets the loop
/// survive a non-cancellation sweep failure and try again on the next tick. The sibling test file
/// covers <c>ProbeAsync</c>'s own internal backlog-probe failure handling and the loop's
/// happy-path tick, but never drives an exception out of a sweep while the loop itself is running.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/ReceiveLivenessWatchdog.cs</code-under-test>
[Timeout(10_000)]
public class ReceiveLivenessWatchdogCoverageTests {
  [Test]
  public async Task DisposeAsync_DuringInFlightRecovery_ExitsTheLoopViaCancellationReturnAsync() {
    // If this cancellation-filtered catch regressed to the general catch, an ordinary shutdown
    // mid-recovery would log a spurious "sweep failed" error for what is actually a clean stop --
    // or, if the filter direction flipped instead, DisposeAsync would hang forever waiting for a
    // loop that no longer recognizes its own cancellation as a reason to return.
    var recoverInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var logger = new _levelRecordingLogger();
    var time = new FakeTimeProvider();
    var interval = TimeSpan.FromMinutes(1);
    var watchdog = new ReceiveLivenessWatchdog(
      new AzureServiceBusOptions {
        ReceiveLivenessSilenceThreshold = TimeSpan.FromSeconds(30),
        ReceiveLivenessProbeInterval = interval
      },
      (_, _, _) => Task.FromResult(5L),
      async ct => {
        recoverInvoked.TrySetResult();
        // Blocks on the very token DisposeAsync cancels below -- that cancellation is what makes
        // this throw with stopToken.IsCancellationRequested already true, the exact condition the
        // loop's cancellation-filtered catch tests for.
        await Task.Delay(Timeout.Infinite, ct);
      },
      time,
      logger);
    watchdog.Track("topic-a", "sub-a");

    watchdog.Start();
    time.Advance(interval);
    await recoverInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // DisposeAsync awaits the loop task directly, so returning from here at all already rules out
    // the hang half of the regression (the [Timeout] would fire otherwise).
    await watchdog.DisposeAsync();

    // The other half is quieter and therefore worse: if the shutdown cancellation fell through to
    // the general catch, every ordinary stop mid-recovery would file a sweep FAILURE against a
    // watchdog that did exactly the right thing -- an error an operator has no way to dismiss and
    // no fault to find behind it.
    await Assert.That(logger.Levels.Any(l => l == LogLevel.Error)).IsFalse()
      .Because("a clean stop is not a failed sweep, and logging it as one sends an operator "
             + "hunting a transport fault that never happened");
  }

  [Test]
  public async Task ProbeThrows_NonCancellationException_LoopSurvivesAndSweepsAgainAsync() {
    // The loop must survive any sweep failure. If this catch-all regressed and let the exception
    // propagate instead, one bad recovery attempt would silently kill the entire watchdog -- every
    // subscription it was tracking loses liveness detection for good, with nothing left running to
    // say so.
    var attempt = 0;
    var firstAttemptSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondAttemptSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var time = new FakeTimeProvider();
    var interval = TimeSpan.FromMinutes(1);
    var watchdog = new ReceiveLivenessWatchdog(
      new AzureServiceBusOptions {
        ReceiveLivenessSilenceThreshold = TimeSpan.FromSeconds(30),
        ReceiveLivenessProbeInterval = interval
      },
      (_, _, _) => Task.FromResult(5L),
      _ => {
        var n = Interlocked.Increment(ref attempt);
        if (n == 1) {
          firstAttemptSeen.TrySetResult();
          throw new InvalidOperationException("recovery transport unavailable");
        }
        secondAttemptSeen.TrySetResult();
        return Task.CompletedTask;
      },
      time,
      NullLogger.Instance);
    watchdog.Track("topic-a", "sub-a");

    watchdog.Start();
    time.Advance(interval);
    await firstAttemptSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // The failed attempt threw before the reset-all-windows step, so the tracked subscription is
    // still (correctly) silent past threshold on the next tick -- proving the loop looped back to
    // await another tick instead of dying.
    time.Advance(interval);
    await secondAttemptSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(attempt).IsEqualTo(2)
      .Because("a sweep failure must be logged and survived, not left to kill the loop -- the next tick has to run");

    await watchdog.DisposeAsync();
  }

  /// <summary>Records only what this file asserts on: the level of each entry the loop emitted.</summary>
  private sealed class _levelRecordingLogger : ILogger {
    public List<LogLevel> Levels { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (Levels) {
        Levels.Add(logLevel);
      }
    }
  }
}
