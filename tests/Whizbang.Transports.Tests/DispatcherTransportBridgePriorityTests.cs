using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Priority step 1 on the wire, producer boundary: the bridge builds the envelope it hands the transport, so it
/// is the last place the number can be declared. A publish made while handling other work carries that work's
/// number (the ambient parent), the same inheritance the dispatcher applies; a publish outside any handling
/// stays undeclared, and the consumer's own rules decide. The in-process transport hands subscribers the live
/// envelope, so the assertion reads exactly the object the bridge built.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#on-the-wire</docs>
/// <code-under-test>src/Whizbang.Core/Transports/DispatcherTransportBridge.cs</code-under-test>
public class DispatcherTransportBridgePriorityTests {
  public record BridgePriorityProbe(int Value) : ICommand;

  private static DispatcherTransportBridge _bridge(InProcessTransport transport) {
    var instanceProvider = new ServiceInstanceProvider(configuration: null);
    var dispatcher = new NoReceptorDispatcher(new ServiceCollection().BuildServiceProvider(), instanceProvider);
    return new DispatcherTransportBridge(dispatcher, transport, instanceProvider);
  }

  private static async Task<TaskCompletionSource<IMessageEnvelope>> _captureAsync(InProcessTransport transport, TransportDestination destination) {
    var received = new TaskCompletionSource<IMessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
    await transport.SubscribeBatchAsync(
      (batch, _) => {
        received.TrySetResult(batch[0].Envelope);
        return Task.CompletedTask;
      },
      destination,
      new TransportBatchOptions { BatchSize = 1, SlideMs = 10, MaxWaitMs = 100 });
    return received;
  }

  [Test]
  public async Task PublishToTransportAsync_WhilePublishingBackgroundWork_TheEnvelopeCarriesTheAmbientParentAsync() {
    var transport = new InProcessTransport();
    var destination = new TransportDestination("priority-bridge-remote");
    var received = await _captureAsync(transport, destination);
    var bridge = _bridge(transport);

    using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
      await bridge.PublishToTransportAsync(new BridgePriorityProbe(1), destination);
    }

    var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a message published while handling background work is background, the same inheritance the dispatcher applies; the bridge is the only place this envelope is built");
  }

  [Test]
  public async Task PublishToTransportAsync_OutsideAnyHandling_TheEnvelopeStaysUndeclaredAsync() {
    var transport = new InProcessTransport();
    var destination = new TransportDestination("priority-bridge-remote-plain");
    var received = await _captureAsync(transport, destination);
    var bridge = _bridge(transport);

    await bridge.PublishToTransportAsync(new BridgePriorityProbe(2), destination);

    var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("nobody said; the consumer's receive rules classify an undeclared number, and the bridge must not invent a band");
  }

  [Test]
  public async Task PublishToTransportAsync_WhilePublishingInteractiveWork_TheEnvelopeCarriesInteractiveAsync() {
    var transport = new InProcessTransport();
    var destination = new TransportDestination("priority-bridge-remote-urgent");
    var received = await _captureAsync(transport, destination);
    var bridge = _bridge(transport);

    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      await bridge.PublishToTransportAsync(new BridgePriorityProbe(3), destination);
    }

    var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("inheritance carries the exact number, not a band: an interactive handling's publish is interactive on the far side");
  }

  private sealed class NoReceptorDispatcher(IServiceProvider serviceProvider, IServiceInstanceProvider instanceProvider)
      : Dispatcher(serviceProvider, instanceProvider) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent @event, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;
  }
}
