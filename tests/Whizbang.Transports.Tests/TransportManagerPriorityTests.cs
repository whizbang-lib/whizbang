using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Priority step 1 on the wire, producer boundary: the transport manager builds one envelope per publish and
/// fans it out to every target, so the number it declares is the number every subscriber sees. A publish made
/// while handling other work carries that work's number (the ambient parent); a publish outside any handling
/// stays undeclared for the consumer to classify.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#on-the-wire</docs>
/// <code-under-test>src/Whizbang.Core/Transports/TransportManager.cs</code-under-test>
public class TransportManagerPriorityTests {
  private sealed record ManagerPriorityProbe(int Value);

  private static async Task<(TransportManager Manager, List<PublishTarget> Targets, TaskCompletionSource<IMessageEnvelope> Received)> _setupAsync(string destination) {
    var manager = new TransportManager(new ServiceInstanceProvider(configuration: null));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);
    var received = new TaskCompletionSource<IMessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
    await transport.SubscribeBatchAsync(
      (batch, _) => {
        received.TrySetResult(batch[0].Envelope);
        return Task.CompletedTask;
      },
      new TransportDestination(destination),
      new TransportBatchOptions { BatchSize = 1, SlideMs = 10, MaxWaitMs = 100 },
      CancellationToken.None);
    var targets = new List<PublishTarget> { new() { TransportType = TransportType.InProcess, Destination = destination } };
    return (manager, targets, received);
  }

  [Test]
  public async Task PublishToTargetsAsync_WhilePublishingBackgroundWork_TheEnvelopeCarriesTheAmbientParentAsync() {
    var (manager, targets, received) = await _setupAsync("priority-manager-target");

    using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
      await manager.PublishToTargetsAsync(new ManagerPriorityProbe(1), targets);
    }

    var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the manager builds the envelope every target receives; a publish made while handling background work must not arrive undeclared and be read as standard");
  }

  [Test]
  public async Task PublishToTargetsAsync_OutsideAnyHandling_TheEnvelopeStaysUndeclaredAsync() {
    var (manager, targets, received) = await _setupAsync("priority-manager-target-plain");

    await manager.PublishToTargetsAsync(new ManagerPriorityProbe(2), targets);

    var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("with no handling in progress there is nothing to inherit; the consumer's rules decide, the manager does not invent a band");
  }

  [Test]
  public async Task PublishToTargetsAsync_WithTwoTargets_BothEnvelopesCarryTheSameNumberAsync() {
    var manager = new TransportManager(new ServiceInstanceProvider(configuration: null));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);
    var first = new TaskCompletionSource<IMessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
    var second = new TaskCompletionSource<IMessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
    var batchOptions = new TransportBatchOptions { BatchSize = 1, SlideMs = 10, MaxWaitMs = 100 };
    await transport.SubscribeBatchAsync((batch, _) => { first.TrySetResult(batch[0].Envelope); return Task.CompletedTask; }, new TransportDestination("priority-manager-a"), batchOptions, CancellationToken.None);
    await transport.SubscribeBatchAsync((batch, _) => { second.TrySetResult(batch[0].Envelope); return Task.CompletedTask; }, new TransportDestination("priority-manager-b"), batchOptions, CancellationToken.None);
    var targets = new List<PublishTarget> {
      new() { TransportType = TransportType.InProcess, Destination = "priority-manager-a" },
      new() { TransportType = TransportType.InProcess, Destination = "priority-manager-b" },
    };

    using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
      await manager.PublishToTargetsAsync(new ManagerPriorityProbe(3), targets);
    }

    await Assert.That((await first.Task.WaitAsync(TimeSpan.FromSeconds(10))).Priority).IsEqualTo(WorkPriority.BACKGROUND);
    await Assert.That((await second.Task.WaitAsync(TimeSpan.FromSeconds(10))).Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("one envelope, every target: the declaration cannot differ between subscribers of the same publish");
  }
}
