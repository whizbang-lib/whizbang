using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Priority step 1, background work: a redelivery bundle replays stored history to a consumer that missed it.
/// The pump declares <see cref="WorkPriority.BACKGROUND"/> on the wire envelope of every bundle, so the
/// consumer's fan-out gives each replayed child that number and none of them is claimed ahead of live work.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#background-work</docs>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryPump.cs</code-under-test>
[Category("Unit")]
public class RedeliveryPumpPriorityTests {
  [Test]
  public async Task Publish_EveryBundlesEnvelopeIsBackground_BeforeSerializationAsync() {
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(transport: transport, envelopeSerializer: serializer, instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()), compositeFactory: new CompositeFactory());
    var streamA = TrackedGuid.New().Value;
    var streamB = TrackedGuid.New().Value;

    var published = await pump.PublishAsync(
      [_evt(streamA, 1), _evt(streamA, 2), _evt(streamB, 1)],
      topic: "repair-topic", target: "svc-x", originServiceId: TrackedGuid.New().Value);

    await Assert.That(published).IsEqualTo(2);
    foreach (var envelope in serializer.Captured) {
      await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
        .Because("the typed envelope is where the pump declares; the serializer copies whatever it finds there");
    }
  }

  [Test]
  public async Task Publish_EveryBundleOnTheWireIsBackgroundAsync() {
    var transport = new CaptureTransport();
    var pump = new RedeliveryPump(transport: transport, envelopeSerializer: new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()), instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()), compositeFactory: new CompositeFactory());
    var stream = TrackedGuid.New().Value;

    await pump.PublishAsync([_evt(stream, 1), _evt(stream, 2)], topic: "repair-topic", target: "svc-x");

    await Assert.That(transport.Published.Single().Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the storage-form envelope is what the transport ships; the consumer classifies the bundle from this number");
  }

  [Test]
  public async Task Publish_InsideAnInteractiveHandling_TheBundleStaysBackgroundAsync() {
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(transport: transport, envelopeSerializer: serializer, instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()), compositeFactory: new CompositeFactory());

    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      await pump.PublishAsync([_evt(TrackedGuid.New().Value, 1)], topic: "repair-topic", target: null);
    }

    await Assert.That(serializer.Captured.Single().Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a redelivery request handled interactively must not make the replay it triggers interactive; the pump declares the band itself");
  }

  private static RedeliveryEvent _evt(Guid streamId, long version) => new() {
    EventId = TrackedGuid.New().Value,
    StreamId = streamId,
    Version = version,
    CommitSequence = version,
    EventType = "Contracts.ProbeHappened",
    EventData = /*lang=json,strict*/ "{\"seeded\":true}",
    Metadata = "{}",
    Scope = null,
    Flags = 0
  };

  private sealed class CaptureSerializer : IEnvelopeSerializer {
    public List<IMessageEnvelope> Captured { get; } = [];
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      Captured.Add(envelope);
      var payloadType = envelope.Payload!.GetType();
      return new SerializedEnvelope(
        new MessageEnvelope<System.Text.Json.JsonElement> {
          MessageId = envelope.MessageId,
          Payload = default,
          Hops = [.. envelope.Hops],
          DispatchContext = envelope.DispatchContext,
          Target = envelope.Target,
          StateOnly = envelope.StateOnly,
          Priority = envelope.Priority,
        },
        $"Whizbang.Core.Observability.MessageEnvelope`1[[{payloadType.AssemblyQualifiedName}]], Whizbang.Core",
        payloadType.AssemblyQualifiedName!);
    }
    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed class CaptureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }
}
