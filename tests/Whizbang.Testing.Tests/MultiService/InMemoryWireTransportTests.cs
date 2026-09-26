using System.Text.Json;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.MultiService;

namespace Whizbang.Testing.Tests.MultiService;

/// <summary>
/// Wire-boundary behavior of <see cref="InMemoryWireTransport"/>: what it advertises about itself,
/// what actually crosses the boundary when the caller has already serialized, and the subscription
/// state a consumer reads back.
/// </summary>
public class InMemoryWireTransportTests {
  private static readonly TransportDestination _topic = new("inbox");
  private const string WIRE_ENVELOPE_TYPE =
    "Whizbang.Core.Messaging.MessageEnvelope`1[[Probe.Payload, Probe]], Whizbang.Core";

  private static JsonSerializerOptions _wireOptions() => JsonContextRegistry.CreateCombinedOptions();

  private static MessageEnvelope<JsonElement> _envelope(string payloadJson) {
    using var document = JsonDocument.Parse(payloadJson);
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = document.RootElement.Clone(),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "inbox",
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static byte[] _bytes(JsonSerializerOptions options, MessageEnvelope<JsonElement> envelope) =>
    JsonSerializer.SerializeToUtf8Bytes(
      envelope,
      (System.Text.Json.Serialization.Metadata.JsonTypeInfo<MessageEnvelope<JsonElement>>)
        options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));

  [Test]
  public async Task Wire_IsUsableImmediatelyAndAdvertisesOnlyWhatItImplementsAsync(
      CancellationToken cancellationToken) {
    var wire = new InMemoryWireTransport(_wireOptions());

    // A host built on the shared wire never runs a connect step, so the wire has to report itself
    // ready from construction — a false here would strand any caller that gates on IsInitialized.
    await Assert.That(wire.IsInitialized).IsTrue();

    // The advertised capabilities are a promise the SendAsync test below holds it to: publish and
    // subscribe are real, request/response is deliberately absent.
    await Assert.That(wire.Capabilities.HasFlag(TransportCapabilities.PublishSubscribe)).IsTrue();
    await Assert.That(wire.Capabilities.HasFlag(TransportCapabilities.Reliable)).IsTrue();
    await Assert.That(wire.Capabilities.HasFlag(TransportCapabilities.RequestResponse)).IsFalse();

    // Initialization is a no-op the harness still calls; it must complete and change nothing.
    await wire.InitializeAsync(cancellationToken);
    await Assert.That(wire.IsInitialized).IsTrue();
  }

  [Test]
  public async Task PublishAsync_WithPreSerializedBytes_PutsThoseBytesOnTheWireAsync(
      CancellationToken cancellationToken) {
    var options = _wireOptions();
    var wire = new InMemoryWireTransport(options);
    var alreadySerialized = _envelope("""{"probe":"already-serialized"}""");
    var payloadBytes = _bytes(options, alreadySerialized);

    // A DIFFERENT envelope object is handed to PublishAsync alongside the bytes. Whatever the
    // subscriber sees identifies which one crossed the boundary: taking the bytes is the contract
    // (a broker forwards the body the outbox already wrote), re-serializing the argument would
    // silently replace an offloaded or already-stamped body with a fresh one.
    var ignoredEnvelope = _envelope("""{"probe":"re-serialized"}""");

    var received = new List<TransportMessage>();
    var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await wire.SubscribeBatchAsync(
      (batch, _) => {
        received.AddRange(batch);
        delivered.TrySetResult();
        return Task.CompletedTask;
      },
      _topic,
      new TransportBatchOptions(),
      cancellationToken);

    await wire.PublishAsync(
      ignoredEnvelope, _topic, WIRE_ENVELOPE_TYPE, payloadBytes,
      cancellationToken);

    await delivered.Task.WaitAsync(cancellationToken);
    await Assert.That(received).Count().IsEqualTo(1);
    await Assert.That(received[0].EnvelopeType).IsEqualTo(WIRE_ENVELOPE_TYPE);

    var envelope = (MessageEnvelope<JsonElement>)received[0].Envelope;
    await Assert.That(envelope.MessageId).IsEqualTo(alreadySerialized.MessageId);
    await Assert.That(envelope.Payload.GetProperty("probe").GetString()).IsEqualTo("already-serialized");
    await Assert.That(received[0].Envelope).IsNotSameReferenceAs(ignoredEnvelope)
      .Because("each subscriber deserializes the bytes itself — no object graph is shared across services");
  }

  [Test]
  public async Task SendAsync_IsRefused_BecauseTheWireHasNoRequestResponseAsync(
      CancellationToken cancellationToken) {
    var wire = new InMemoryWireTransport(_wireOptions());
    var envelope = _envelope("""{"probe":"request"}""");

    // Capabilities say request/response is unsupported; the call must say so too rather than hang
    // waiting for a reply the harness will never produce.
    var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
      await wire.SendAsync<JsonElement, JsonElement>(envelope, _topic, cancellationToken));

    await Assert.That(ex!.Message).Contains("Request/response");
  }

  [Test]
  public async Task Subscription_ReportsPausedAndResumedStateToItsConsumerAsync(
      CancellationToken cancellationToken) {
    var wire = new InMemoryWireTransport(_wireOptions());

    var subscription = await wire.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask,
      _topic,
      new TransportBatchOptions(),
      cancellationToken);

    // IsActive is the only thing a consumer can read back about a subscription's state, so each
    // control operation has to move it: a pause that leaves IsActive true reports a live
    // subscription that is not, and a resume that does not clear it reports the reverse.
    await Assert.That(subscription.IsActive).IsTrue();

    await subscription.PauseAsync();
    await Assert.That(subscription.IsActive).IsFalse();

    await subscription.ResumeAsync();
    await Assert.That(subscription.IsActive).IsTrue();

    subscription.Dispose();
    await Assert.That(subscription.IsActive).IsFalse();
  }
}
