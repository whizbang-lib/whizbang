using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Direct coverage for <see cref="InMemorySignalTransport"/> boundary cases not exercised by
/// <see cref="SignalBusTests"/>: null-arg on start, unstarted publish, and post-start loopback
/// direct on the transport (bypassing the bus).
/// </summary>
public class InMemorySignalTransportTests {
  private readonly record struct MemProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private sealed class CountingSink : ISignalSink {
    public int Received { get; private set; }
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Received++;
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  public async Task StartAsync_NullSink_ThrowsAsync() {
    var transport = new InMemorySignalTransport();
    await Assert.That(() => transport.StartAsync(null!)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task PublishAsync_BeforeStart_IsNoOpAsync() {
    var transport = new InMemorySignalTransport();
    // No sink yet — publish must NOT throw (a not-started transport is a no-op, not a failure).
    await transport.PublishAsync(new MemProbe(1), SignalTarget.Broadcast);
  }

  [Test]
  public async Task PublishAsync_AfterStart_LoopsBackToSinkAsync() {
    var transport = new InMemorySignalTransport();
    var sink = new CountingSink();
    await transport.StartAsync(sink);

    await transport.PublishAsync(new MemProbe(1), SignalTarget.Broadcast);
    await transport.PublishAsync(new MemProbe(2), SignalTarget.Broadcast);

    await Assert.That(sink.Received).IsEqualTo(2);
  }
}
