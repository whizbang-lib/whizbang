// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Testing.Async;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The consumer loop's idle passes, held to a wait budget with the shipped verification tool (loop-waits audit, #2613).
/// </summary>
/// <remarks>
/// The same leak the channel-waiter tests prove, measured with <see cref="CountingChannelReader{T}"/> and
/// <see cref="LoopWaitBudget"/> from <c>Whizbang.Testing</c> instead of test-local code, so the audit also proves the
/// tool works against a real loop: it must fail on the loop that leaks and pass on the one that does not.
/// </remarks>
public partial class PerspectiveWorkerInjectedClockTests {
  [Test]
  [Timeout(30000)]
  public async Task IdlePasses_HeldToAWaitBudget_KeepAtMostOneWaitOnEachChannelAsync(CancellationToken cancellationToken) {
    var pollInterval = TimeSpan.FromMilliseconds(150);
    var clock = new ArmingClock();
    var workChannel = new CountingWorkChannel();
    var drainChannel = new CountingDrainChannel();
    var fx = _build(clock, new PerspectiveWorkerOptions {
      PollingIntervalMilliseconds = (int)pollInterval.TotalMilliseconds,
      MaxConcurrentDrainConsumers = 1,
    }, workChannel: workChannel, drainChannel: drainChannel);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    await fx.Worker.StartAsync(cts.Token);
    try {
      var highest = await new LoopWaitBudget()
        .AtMost("work channel waits", () => workChannel.Counting.OutstandingWaits, 1)
        .AtMost("drain channel waits", () => drainChannel.Counting.OutstandingWaits, 1)
        .RunAsync(20, async pass => {
          // Each pass arms one idle wait after its channel waits; once armed, the pass is parked. Then the timeout ends it.
          await clock.Armed(pollInterval, pass).WaitAsync(cancellationToken);
          clock.Advance(pollInterval);
          await clock.Armed(pollInterval, pass + 1).WaitAsync(cancellationToken);
        });

      await Assert.That(highest["work channel waits"]).IsEqualTo(1)
        .Because("the counter saw the one pending wait the loop legitimately carries");
    } finally {
      await cts.CancelAsync();
      await fx.Worker.StopAsync(CancellationToken.None);
    }
  }
}
