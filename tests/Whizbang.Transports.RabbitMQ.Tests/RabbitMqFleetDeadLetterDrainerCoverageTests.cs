using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Transports.RabbitMQ;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Coverage gaps left by <c>RabbitMqFleetDeadLetterDrainerTests</c>: the transport-name
/// identity, the non-positive budget short-circuit, and the mid-pass break once the total
/// cap is exhausted before every declared queue has been visited.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMqFleetDeadLetterDrainer.cs</code-under-test>
public class RabbitMqFleetDeadLetterDrainerCoverageTests {

  private sealed class _recordingDrainer(string name) : ITransportDeadLetterDrainer {
    public int Invocations;
    public int ReturnPerDrain { get; init; } = 1;
    public string TransportName => $"rmq:{name}";
    public Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
      Invocations++;
      return Task.FromResult(Math.Min(ReturnPerDrain, maxCount));
    }
  }

  /// <summary>
  /// Production risk if this regresses: the drain worker resolves every registered
  /// <see cref="ITransportDeadLetterDrainer"/> from DI and keys behavior off
  /// <see cref="ITransportDeadLetterDrainer.TransportName"/> — a wrong or empty name would
  /// misattribute this drainer's pass in any host running more than one broker.
  /// </summary>
  [Test]
  public async Task TransportName_IsRmqAsync() {
    var fleet = new RabbitMqFleetDeadLetterDrainer(
      () => new List<string>(),
      _ => throw new InvalidOperationException("no drain pass runs in this test"));

    await Assert.That(fleet.TransportName).IsEqualTo("rmq");
  }

  /// <summary>
  /// Production risk if this regresses: without the short-circuit, a pass the worker asked to
  /// skip (budget already spent elsewhere this tick) would still read the declared-queue
  /// snapshot and touch every queue's cached drainer — needless broker chatter on a tick meant
  /// to be a complete no-op.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_WithNonPositiveBudget_ReturnsZeroWithoutTouchingAnyQueueAsync() {
    var queueSnapshotReads = 0;
    var fleet = new RabbitMqFleetDeadLetterDrainer(
      () => { queueSnapshotReads++; return new List<string> { "a.dlq" }; },
      _ => throw new InvalidOperationException("must not be reached with a non-positive budget"));

    var drained = await fleet.DrainDeadLetterQueueAsync(0);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(queueSnapshotReads).IsEqualTo(0)
      .Because("a non-positive budget must short-circuit before the declared-queue snapshot is even read");
  }

  /// <summary>
  /// Production risk if this regresses: once the pass's total cap is spent, the fleet must stop
  /// rather than keep creating and invoking drainers for the remaining queues — maxCount is a
  /// broker-pacing contract per <c>TransportDeadLetterDrainWorker.MaxPerTick</c>, and overrunning
  /// it on the last queues of a pass reintroduces the burst the pacing exists to prevent.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_BudgetExhaustedMidPass_StopsWithoutVisitingRemainingQueuesAsync() {
    var queues = new List<string> { "a.dlq", "b.dlq", "c.dlq", "d.dlq" };
    var made = new Dictionary<string, _recordingDrainer>();
    var fleet = new RabbitMqFleetDeadLetterDrainer(
      () => queues,
      name => { var d = new _recordingDrainer(name) { ReturnPerDrain = 5 }; made[name] = d; return d; });

    var drained = await fleet.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsEqualTo(10);
    await Assert.That(made.Count).IsEqualTo(2)
      .Because("the budget is exhausted after the second queue (5 + 5 = 10); the third and "
             + "fourth queues must never get a drainer created for them");
  }
}
