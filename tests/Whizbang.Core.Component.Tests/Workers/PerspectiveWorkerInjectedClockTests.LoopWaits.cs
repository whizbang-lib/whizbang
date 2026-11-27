// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
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
    var workChannel = new CountedWorkChannel();
    var drainChannel = new CountedDrainChannel();
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

  /// <summary>A perspective work channel read through a <see cref="CountingChannelReader{T}"/>.</summary>
  private sealed class CountedWorkChannel : IPerspectiveChannelWriter {
    private readonly Channel<PerspectiveWork> _channel = Channel.CreateUnbounded<PerspectiveWork>();

    public CountedWorkChannel() => Counting = new CountingChannelReader<PerspectiveWork>(_channel.Reader);

    public CountingChannelReader<PerspectiveWork> Counting { get; }
    public ChannelReader<PerspectiveWork> Reader => Counting;
    public ValueTask WriteAsync(PerspectiveWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(PerspectiveWork work) => _channel.Writer.TryWrite(work);
    public void Complete() => _channel.Writer.TryComplete();
  }

  /// <summary>A perspective drain channel read through a <see cref="CountingChannelReader{T}"/>.</summary>
  private sealed class CountedDrainChannel : IPerspectiveDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public CountedDrainChannel() => Counting = new CountingChannelReader<Guid>(_channel.Reader);

    public CountingChannelReader<Guid> Counting { get; }
    public ChannelReader<Guid> Reader => Counting;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }
}
