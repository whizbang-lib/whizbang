using System.Collections.Concurrent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Transports.RabbitMQ;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Fleet fan-out for the RabbitMQ dead-letter drain: every declared DLQ gets a cached per-queue
/// drainer, the pass budget is a TOTAL cap, and queues declared after startup are picked up on
/// the next pass.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMqFleetDeadLetterDrainer.cs</code-under-test>
public class RabbitMqFleetDeadLetterDrainerTests {

  private sealed class _recordingDrainer(string name) : ITransportDeadLetterDrainer {
    public int Invocations;
    public int LastBudget;
    public int ReturnPerDrain { get; init; } = 1;
    public string TransportName => $"rmq:{name}";
    public Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
      Invocations++;
      LastBudget = maxCount;
      return Task.FromResult(Math.Min(ReturnPerDrain, maxCount));
    }
  }

  [Test]
  public async Task Fleet_DrainsEveryDeclaredQueue_AndSumsCountsAsync() {
    var made = new ConcurrentDictionary<string, _recordingDrainer>();
    var queues = new List<string> { "orders.dlq", "billing.dlq" };
    var fleet = new RabbitMqFleetDeadLetterDrainer(
      () => queues,
      name => made.GetOrAdd(name, n => new _recordingDrainer(n) { ReturnPerDrain = 3 }));

    var drained = await fleet.DrainDeadLetterQueueAsync(500);

    await Assert.That(made.Count).IsEqualTo(2);
    await Assert.That(drained).IsEqualTo(6);
  }

  [Test]
  public async Task Fleet_BudgetIsATotalCapAsync() {
    var queues = new List<string> { "a.dlq", "b.dlq", "c.dlq" };
    var made = new List<_recordingDrainer>();
    var fleet = new RabbitMqFleetDeadLetterDrainer(
      () => queues,
      name => { var d = new _recordingDrainer(name) { ReturnPerDrain = 4 }; made.Add(d); return d; });

    var drained = await fleet.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsLessThanOrEqualTo(10)
      .Because("MaxPerTick is the worker's broker-pacing contract — the fleet must not multiply "
             + "it by the queue count");
  }

  [Test]
  public async Task Fleet_QueueDeclaredLater_GetsDrained_AndDrainersAreCachedAsync() {
    var queues = new List<string> { "a.dlq" };
    var made = new ConcurrentDictionary<string, _recordingDrainer>();
    var fleet = new RabbitMqFleetDeadLetterDrainer(
      () => queues,
      name => made.GetOrAdd(name, n => new _recordingDrainer(n)));

    _ = await fleet.DrainDeadLetterQueueAsync(100);
    queues.Add("b.dlq");
    _ = await fleet.DrainDeadLetterQueueAsync(100);

    await Assert.That(made.ContainsKey("b.dlq")).IsTrue()
      .Because("the declared-queue snapshot is re-evaluated per pass");
    await Assert.That(made["a.dlq"].Invocations).IsEqualTo(2)
      .Because("per-queue drainers are cached, not re-created each pass");
  }
}
