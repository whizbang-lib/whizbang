using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// The RabbitMQ half of the backlog-age duty (topology arc phase 10) and its CAPABILITY HONESTY.
/// A passive declare returns the broker's own message count without creating or modifying
/// anything, so depth is cheap and truthful. Age is NOT available: AMQP cannot read the head
/// message's timestamp without consuming it, and a get-and-requeue would mark the message
/// redelivered on every duty tick — corrupting the counters the poison detector reads, once a
/// minute, forever. The peek therefore reports no age rather than inventing one, and the duty
/// surfaces that gap instead of going silently inert.
/// </summary>
[Category("Transports")]
public class RabbitMQBacklogPeekTests {
  [Test]
  public async Task PeekAsync_ReportsDepthPerConsumedQueueAsync() {
    var channel = _channelWith(("orders-svc-inbox.orders", 42));
    var peek = _peek(channel, "orders-svc-inbox.orders");

    var sample = (await peek.PeekAsync(CancellationToken.None)).Single();

    await Assert.That(sample.Entity).IsEqualTo("orders-svc-inbox.orders");
    await Assert.That(sample.Depth).IsEqualTo(42L);
    await Assert.That(sample.Transport).IsEqualTo("rabbitmq");
  }

  [Test]
  public async Task PeekAsync_ReportsNoAge_TheHonestAnswerForThisTransportAsync() {
    var channel = _channelWith(("orders-svc-inbox.orders", 42));

    var sample = (await _peek(channel, "orders-svc-inbox.orders").PeekAsync(CancellationToken.None)).Single();

    await Assert.That(sample.OldestAge).IsNull()
      .Because("no age is the truth here — a fabricated one would be worse than none, and the "
             + "duty reports the gap on the health surface");
  }

  [Test]
  public async Task PeekAsync_UndeclaredQueue_IsSkippedWithoutFaultingAsync() {
    // A passive declare against a missing queue closes the channel; that must cost one sample,
    // never the whole pass.
    var peek = _peek(_channelWith(), "never-declared");

    var samples = await peek.PeekAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  [Test]
  public async Task PeekAsync_UsesADedicatedChannelPerProbeAsync() {
    // A failed passive declare closes the channel it ran on, so sharing one would take out an
    // unrelated probe in the same pass.
    var channel = _channelWith(("a", 1), ("b", 2));
    var peek = _peek(channel, "a", "b");

    var samples = await peek.PeekAsync(CancellationToken.None);

    await Assert.That(samples).Count().IsEqualTo(2);
    await Assert.That(channel.PassiveQueueDeclareCount).IsEqualTo(2);
  }

  [Test]
  public async Task PeekAsync_NoQueues_ReportsNoSamplesAsync() {
    var samples = await _peek(_channelWith()).PeekAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  private static RabbitMQBacklogPeek _peek(FakeChannel channel, params string[] queues) {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    return new RabbitMQBacklogPeek(pool, () => queues);
  }

  private static FakeChannel _channelWith(params (string Queue, uint Depth)[] queues) {
    var channel = new FakeChannel();
    foreach (var (queue, depth) in queues) {
      channel.ExistingQueues.Add(queue);
      channel.PassiveQueueDepths[queue] = depth;
    }
    return channel;
  }
}
