// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The consumer loop holds at most one wait on each channel however many passes go by with nothing written (#2613).
/// </summary>
/// <remarks>
/// Every pass of the loop races a wait on the work channel and a wait on the drain channel against the idle timeout
/// and the wake signal. A pass that the timeout or the signal ends leaves both channel waits unfinished. When each pass
/// asked for fresh waits on the host's stopping token, every such pass queued two more waiters the channels never let
/// go, so an idle instance grew by two waiters per pass for its whole life.
/// </remarks>
public partial class PerspectiveWorkerInjectedClockTests {
  private const int IDLE_PASSES = 40;

  [Test]
  [Timeout(30000)]
  public async Task IdlePasses_EndedByTheTimeoutOrTheWakeSignal_HoldAtMostOneWaitOnEachChannelAsync(CancellationToken cancellationToken) {
    var pollInterval = TimeSpan.FromMilliseconds(150);
    var clock = new ArmingClock();
    var workChannel = new CountingWorkChannel();
    var drainChannel = new CountingDrainChannel();
    var listener = new SignalingNotificationListener();
    var fx = _build(clock, new PerspectiveWorkerOptions {
      PollingIntervalMilliseconds = (int)pollInterval.TotalMilliseconds,
      NotifyHealthyPollingIntervalMilliseconds = (int)pollInterval.TotalMilliseconds,
      MaxConcurrentDrainConsumers = 1,
    }, workChannel: workChannel, drainChannel: drainChannel, notificationListener: listener);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    await fx.Worker.StartAsync(cts.Token);

    var maxWorkHeld = 0;
    var maxDrainHeld = 0;
    for (var pass = 1; pass <= IDLE_PASSES; pass++) {
      // Each pass arms one idle wait, after its channel waits and before it parks; once it is armed the pass is waiting.
      await clock.Armed(pollInterval, pass).WaitAsync(cancellationToken);
      maxWorkHeld = Math.Max(maxWorkHeld, workChannel.Counting.OutstandingWaits);
      maxDrainHeld = Math.Max(maxDrainHeld, drainChannel.Counting.OutstandingWaits);
      // Half the passes end on the idle timeout, half on the wake signal; neither writes to a channel.
      if (pass % 2 == 0) {
        listener.Raise(WorkSignalCategory.Perspective);
      } else {
        clock.Advance(pollInterval);
      }
    }
    await clock.Armed(pollInterval, IDLE_PASSES + 1).WaitAsync(cancellationToken);

    await Assert.That(maxWorkHeld).IsEqualTo(1)
      .Because("an idle pass must leave no wait behind on the work channel; the pending one carries over to the next pass");
    await Assert.That(maxDrainHeld).IsEqualTo(1)
      .Because("an idle pass must leave no wait behind on the drain channel; the pending one carries over to the next pass");
    await Assert.That(workChannel.Counting.OutstandingWaits).IsEqualTo(1);
    await Assert.That(drainChannel.Counting.OutstandingWaits).IsEqualTo(1);
    await Assert.That(fx.Worker.PendingWakeWaiters).IsLessThanOrEqualTo(1)
      .Because("the wake signal keeps its single waiter across passes (#728)");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);

    await Assert.That(workChannel.Counting.OutstandingWaits).IsEqualTo(0)
      .Because("stopping the worker releases the wait it carried");
    await Assert.That(drainChannel.Counting.OutstandingWaits).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task IdlePasses_WithSeveralConsumers_HoldAtMostOneWaitPerConsumerOnEachChannelAsync(CancellationToken cancellationToken) {
    const int consumers = 3;
    var pollInterval = TimeSpan.FromMilliseconds(150);
    var clock = new ArmingClock();
    var workChannel = new CountingWorkChannel();
    var drainChannel = new CountingDrainChannel();
    var fx = _build(clock, new PerspectiveWorkerOptions {
      PollingIntervalMilliseconds = (int)pollInterval.TotalMilliseconds,
      MaxConcurrentDrainConsumers = consumers,
    }, workChannel: workChannel, drainChannel: drainChannel);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    await fx.Worker.StartAsync(cts.Token);

    var maxWorkHeld = 0;
    var maxDrainHeld = 0;
    for (var round = 1; round <= IDLE_PASSES / 2; round++) {
      // Every consumer has armed its idle wait for this round: all of them are waiting.
      await clock.Armed(pollInterval, consumers * round).WaitAsync(cancellationToken);
      maxWorkHeld = Math.Max(maxWorkHeld, workChannel.Counting.OutstandingWaits);
      maxDrainHeld = Math.Max(maxDrainHeld, drainChannel.Counting.OutstandingWaits);
      clock.Advance(pollInterval);
    }

    await Assert.That(maxWorkHeld).IsLessThanOrEqualTo(consumers)
      .Because("each consumer loop carries its own single wait on the work channel, and no more");
    await Assert.That(maxDrainHeld).IsLessThanOrEqualTo(consumers)
      .Because("each consumer loop carries its own single wait on the drain channel, and no more");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  /// <summary>A configured notification listener the test raises by hand.</summary>
  private sealed class SignalingNotificationListener : IWorkNotificationListener {
    private Action<WorkSignalCategory>? _onSignal;

    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt => null;

    public event Action<WorkSignalCategory>? OnSignal {
      add => _onSignal += value;
      remove => _onSignal -= value;
    }

    public event Action<bool>? OnHealthChanged {
      add { /* the fake never raises this event */ }
      remove { /* the fake never raises this event */ }
    }

    public void Raise(WorkSignalCategory category) => _onSignal?.Invoke(category);
  }
}
