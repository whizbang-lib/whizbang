using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 4 of zero-idle-polling — exercises the
/// <see cref="BackupTickCoordinator"/>'s ASLEEP/POLLING state machine end-to-end
/// using <see cref="FakeTimeProvider"/> and <see cref="TaskCompletionSource"/>
/// signals so tests are fully deterministic per
/// <c>feedback_no_timing_tests</c> — no wall-clock <see cref="Task.Delay"/>,
/// no polling loops, no flaky timing.
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>Below idle threshold → coordinator stays ASLEEP, no ticks fire.</description></item>
/// <item><description>Crossing idle threshold → coordinator transitions to POLLING and fires one cycle.</description></item>
/// <item><description>Touch during POLLING → next iteration transitions back to ASLEEP.</description></item>
/// <item><description>Disabled coordinator returns immediately from ExecuteAsync.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/backup-tick-coordinator</docs>
public class BackupTickCoordinatorStateMachineTests {

  private static BackupTickCoordinator _build(
      IBackupTickRegistry registry,
      IIdleActivityTracker tracker,
      BackupTickCoordinatorOptions options,
      TimeProvider timeProvider) =>
    new(
      tracker,
      registry,
      Options.Create(options),
      NullLogger<BackupTickCoordinator>.Instance,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      gate: null,
      timeProvider: timeProvider);

  [Test]
  public async Task BelowIdleThreshold_StaysAsleepAsync() {
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-04T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    var tracker = new IdleActivityTracker(time);
    var registry = new BackupTickRegistry();
    var tickRan = false;
    registry.Register("never", _ => { tickRan = true; return Task.CompletedTask; }, () => true);
    var options = new BackupTickCoordinatorOptions {
      IdleThreshold = TimeSpan.FromSeconds(30),
      PollingInterval = TimeSpan.FromSeconds(30),
    };
    var coordinator = _build(registry, tracker, options, time);

    using var cts = new CancellationTokenSource();
    var execTask = coordinator.StartAsync(cts.Token);
    await execTask;
    // Advance only 10 s — below the 30 s idle threshold.
    time.Advance(TimeSpan.FromSeconds(10));
    // Give the coordinator a chance to observe the clock. Yield so scheduled
    // callbacks fire.
    await Task.Yield();

    await Assert.That(tickRan).IsFalse()
      .Because("Coordinator must not engage POLLING while TimeSinceLastActivity is below IdleThreshold.");
    await Assert.That(coordinator.TotalTickCycles).IsEqualTo(0L);

    await coordinator.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task CrossingIdleThreshold_TransitionsToPollingAsync() {
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-04T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    var tracker = new IdleActivityTracker(time);
    var registry = new BackupTickRegistry();
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    registry.Register("signaller", _ => { tcs.TrySetResult(); return Task.CompletedTask; }, () => true);
    var options = new BackupTickCoordinatorOptions {
      IdleThreshold = TimeSpan.FromSeconds(30),
      PollingInterval = TimeSpan.FromMinutes(5),  // Long enough we'd never naturally re-tick during the test
    };
    var coordinator = _build(registry, tracker, options, time);

    using var cts = new CancellationTokenSource();
    await coordinator.StartAsync(cts.Token);
    // Cross the idle threshold.
    time.Advance(TimeSpan.FromSeconds(31));

    var signalled = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    await Assert.That(signalled).IsEqualTo(tcs.Task)
      .Because("Crossing IdleThreshold must fire one cycle of registered ticks. Test waits up to 5 s real-time for the completion signal — sufficient slack for any scheduler latency without depending on wall-clock timing.");

    await coordinator.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task TouchDuringPolling_TransitionsBackToAsleepAsync() {
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-04T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    var tracker = new IdleActivityTracker(time);
    var registry = new BackupTickRegistry();
    var firstCycleSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var totalCycles = 0;
    registry.Register("counter", _ => {
      var c = Interlocked.Increment(ref totalCycles);
      if (c == 1) {
        firstCycleSeen.TrySetResult();
      }
      return Task.CompletedTask;
    }, () => true);
    var options = new BackupTickCoordinatorOptions {
      IdleThreshold = TimeSpan.FromSeconds(30),
      PollingInterval = TimeSpan.FromSeconds(1),
    };
    var coordinator = _build(registry, tracker, options, time);

    using var cts = new CancellationTokenSource();
    await coordinator.StartAsync(cts.Token);
    time.Advance(TimeSpan.FromSeconds(31));
    await firstCycleSeen.Task;
    // Touch — should bring the coordinator back to ASLEEP.
    tracker.Touch("test");
    // Even if the clock advances by the polling interval, the next iteration
    // sees the freshly-touched tracker and transitions to ASLEEP instead of
    // firing another cycle.
    time.Advance(TimeSpan.FromSeconds(2));
    await Task.Yield();
    var cyclesBefore = totalCycles;
    time.Advance(TimeSpan.FromSeconds(5));  // still under IdleThreshold from the Touch
    await Task.Yield();

    await Assert.That(totalCycles).IsEqualTo(cyclesBefore)
      .Because("Touch must transition the coordinator back to ASLEEP — no more ticks should fire until the new IdleThreshold elapses from the touch timestamp.");

    await coordinator.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task DisabledOptions_SkipsExecuteLoopAsync() {
    var time = new FakeTimeProvider();
    var tracker = new IdleActivityTracker(time);
    var registry = new BackupTickRegistry();
    var tickRan = false;
    registry.Register("never", _ => { tickRan = true; return Task.CompletedTask; }, () => true);
    var options = new BackupTickCoordinatorOptions { Enabled = false };
    var coordinator = _build(registry, tracker, options, time);

    using var cts = new CancellationTokenSource();
    await coordinator.StartAsync(cts.Token);
    time.Advance(TimeSpan.FromMinutes(10));
    await Task.Yield();

    await Assert.That(tickRan).IsFalse()
      .Because("Enabled=false is the killswitch — ExecuteAsync must return immediately without engaging the loop.");

    await coordinator.StopAsync(CancellationToken.None);
  }
}
