// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The Service Bus receive decision hands every message that is a valid envelope on to be stored before the broker is
/// told anything, and refuses only a body that is not one, to the broker's dead-letter queue (#1720).
/// </summary>
/// <remarks>
/// A registry miss, a typed deserialization failure and a payload no local consumer takes were completed with the
/// broker (ack and drop) before anything stored them, and a raw receptor match was handled and completed with no
/// inbox row: each was gone with no record. A valid envelope now goes to the handler, which stores it (as received,
/// whatever this service knows of its type) and makes any skip decision after storage, where it is durable and
/// inspectable; a raw receptor runs once the message is stored.
/// </remarks>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AsbReceiveDecisionMaker.cs</code-under-test>
public class AsbReceiveStoresBeforeSettlingTests {
  private const string UNKNOWN_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Upstream.Contracts.OrderShipped, Upstream.Contracts]], Whizbang.Core";

  private static readonly JsonSerializerOptions _options = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  [Test]
  public async Task Decide_AValidEnvelopeOfATypeThisServiceDoesNotKnow_IsHandedOnAsReceivedAsync() {
    var (id, body) = _envelopeBody();

    var decision = new AsbReceiveDecisionMaker().Decide(_withEnvelopeType(UNKNOWN_ENVELOPE_TYPE), body, _resolveNothing, _options);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process)
      .Because("a valid envelope is stored first, whatever this service knows of its type");
    await Assert.That(decision.Envelope).IsTypeOf<MessageEnvelope<JsonElement>>();
    await Assert.That(decision.Envelope!.MessageId.Value).IsEqualTo(id);
    await Assert.That(decision.EnvelopeTypeName).IsEqualTo(UNKNOWN_ENVELOPE_TYPE)
      .Because("the row records the type the producer named");
  }

  [Test]
  public async Task Decide_AValidEnvelopeTheTypedReadRejects_IsHandedOnAsReceivedAsync() {
    var (id, body) = _envelopeBody();
    // The named type resolves, but the body does not fit it (the payload is an object, not a number).
    var mismatched = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }
      .GetTypeInfo(typeof(MessageEnvelope<int>));

    var decision = new AsbReceiveDecisionMaker().Decide(_withEnvelopeType(UNKNOWN_ENVELOPE_TYPE), body, (_, _) => mismatched, _options);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process);
    await Assert.That(decision.Envelope!.MessageId.Value).IsEqualTo(id);
  }

  [Test]
  public async Task Decide_AnEnvelopeNoLocalConsumerTakes_IsHandedOnToBeStoredAsync() {
    var (id, body) = _envelopeBody();
    var typed = _options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));

    var decision = new AsbReceiveDecisionMaker().Decide(
      _withEnvelopeType(UNKNOWN_ENVELOPE_TYPE), body, (_, _) => typed, _options);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process)
      .Because("the skip is decided after the message is stored");
    await Assert.That(decision.Envelope!.MessageId.Value).IsEqualTo(id);
  }

  [Test]
  public async Task Decide_AnEnvelopeARawReceptorTakes_IsHandedOnToBeStored_WithTheReceptorAsync() {
    var (id, body) = _envelopeBody();
    var receptor = new FakeRawReceptor("Upstream.Contracts.OrderShipped, Upstream.Contracts");

    var decision = new AsbReceiveDecisionMaker().Decide(
      _withEnvelopeType(UNKNOWN_ENVELOPE_TYPE), body, _resolveNothing, _options,
      rawReceptorRegistry: new RawReceptorRegistry([receptor]));

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process)
      .Because("the message is stored first, and the raw receptor runs once it is");
    await Assert.That(decision.Envelope!.MessageId.Value).IsEqualTo(id);
    await Assert.That(decision.RawReceptor).IsSameReferenceAs(receptor);
    await Assert.That(decision.RawPayload.HasValue).IsTrue();
  }

  [Test]
  public async Task Decide_AValidEnvelopeWithNoEnvelopeTypeProperty_IsHandedOnAsReceivedAsync() {
    var (id, body) = _envelopeBody();

    var decision = new AsbReceiveDecisionMaker().Decide(new Dictionary<string, object>(), body, _resolveNothing, _options);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process)
      .Because("the consumer gives a message it cannot store as a row dead-letter custody with its body");
    await Assert.That(decision.Envelope!.MessageId.Value).IsEqualTo(id);
    await Assert.That(decision.EnvelopeTypeName).IsNull();
  }

  [Test]
  public async Task Decide_AnEnvelopeWhoseIdIsNotAMessageId_IsDeadLetteredAsync() {
    var (id, body) = _envelopeBody();
    var v4 = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
    var typed = _options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));

    var decision = new AsbReceiveDecisionMaker().Decide(
      _withEnvelopeType(UNKNOWN_ENVELOPE_TYPE), body.Replace(id.ToString(), v4.ToString(), StringComparison.Ordinal),
      (_, _) => typed, _options);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.DeadLetter)
      .Because("the id validation's ArgumentException must not escape the decision, which the broker would redeliver forever");
  }

  [Test]
  [Arguments("""{"hello":1}""")]
  [Arguments("""{"id": not json""")]
  [Arguments("")]
  public async Task Decide_ABodyThatIsNotAnEnvelope_IsDeadLetteredAsync(string body) {
    var decision = new AsbReceiveDecisionMaker().Decide(_withEnvelopeType(UNKNOWN_ENVELOPE_TYPE), body, _resolveNothing, _options);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.DeadLetter)
      .Because("a body that cannot be stored as a message goes to the dead-letter queue, never dropped");
  }

  // ===== Helpers =====

  private static (Guid Id, string Body) _envelopeBody() {
    var id = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(id),
      Payload = JsonElement.Parse("""{"reference":"R-1"}"""),
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = new ServiceInstanceInfo { InstanceId = (Guid)TrackedGuid.New(), ServiceName = "upstream", HostName = "host", ProcessId = 1 },
        Timestamp = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
      }],
      DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return (id, JsonSerializer.Serialize(envelope, _options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))));
  }

  private static Dictionary<string, object> _withEnvelopeType(string typeName) =>
    new() { [AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY] = typeName };

  private static JsonTypeInfo? _resolveNothing(string typeName, JsonSerializerOptions options) => null;

  private sealed class FakeRawReceptor(string targetName) : IRawReceptor {
    public string TargetMessageTypeName { get; } = targetName;
    public Task HandleAsync(JsonElement payload, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
  }
}
