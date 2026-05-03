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
/// RED-first regression locks for slice 1 hotfix's behavior change: when the local service
/// can't bind a received envelope to a CLR type (unknown JsonTypeInfo or shape rejection),
/// the broker is acked and the message dropped instead of dead-lettered. The legacy receive
/// path dead-lettered every such case, which produced unbounded ASB DLQ accumulation on JDX
/// services that received events from contracts assemblies they didn't reference. These
/// tests pin the new behavior so a future refactor can't quietly regress to broker DLQ.
/// </summary>
public class AsbReceiveDecisionMakerTests {
  private static readonly JsonSerializerOptions _jsonOptions = new() {
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
  };

  private static Dictionary<string, object> _withEnvelopeType(string typeName) =>
    new() {
      [AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY] = typeName,
    };

  [Test]
  public async Task Decide_NoEnvelopeTypeProperty_ReturnsDeadLetterAsync() {
    // Genuine broker-metadata failure — the message has no metadata to route on.
    // Dead-lettering at the broker is correct here; redelivery wouldn't help.
    var decider = new AsbReceiveDecisionMaker();
    var props = new Dictionary<string, object>();  // no EnvelopeType

    var decision = decider.Decide(props, "{}", _resolveAlwaysNull, _jsonOptions);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.DeadLetter);
    await Assert.That(decision.Reason).IsEqualTo("MissingEnvelopeType");
  }

  [Test]
  public async Task Decide_UnregisteredEnvelopeType_ReturnsAckAndDropAsync() {
    // The behavior-change lock: today this dead-letters; new behavior is ack + drop.
    // This is the change that stops ASB DLQ accumulation on services that don't reference
    // the publisher's contracts assembly.
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[Unknown.Event, UnknownContracts]]");

    var decision = decider.Decide(props, """{"id":"00000000-0000-0000-0000-000000000000"}""", _resolveAlwaysNull, _jsonOptions);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop);
    await Assert.That(decision.Reason).IsEqualTo("MissingJsonTypeInfo");
    await Assert.That(decision.EnvelopeTypeName).IsEqualTo("Whizbang.Core.Observability.MessageEnvelope`1[[Unknown.Event, UnknownContracts]]");
    await Assert.That(decision.Envelope).IsNull();
  }

  [Test]
  public async Task Decide_ResolverHitsButDeserializeReturnsNull_ReturnsAckAndDropAsync() {
    // JsonTypeInfo resolved but JsonSerializer.Deserialize returns null (e.g., body is just
    // "null"). Pre-hotfix: DLQ. Post-hotfix: ack + drop.
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[Foo]]");
    var typeInfo = (JsonTypeInfo<MessageEnvelope<JsonElement>>)_jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));

    var decision = decider.Decide(props, "null", (_, _) => typeInfo, _jsonOptions);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop);
    await Assert.That(decision.Reason).IsEqualTo("DeserializationFailed");
  }

  [Test]
  public async Task Decide_ResolverHitsAndDeserializeSucceeds_ReturnsProcessAsync() {
    // Round-trip a real envelope so we don't depend on hand-crafted JSON matching the
    // current envelope shape — the test is about the decision policy, not the envelope's
    // wire format. JsonContextRegistry's options are used so Vogen converters are wired.
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[JsonElement]]");
    var combinedOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = (JsonTypeInfo<MessageEnvelope<JsonElement>>)combinedOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));

    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = (Guid)TrackedGuid.NewMedo(),
          ServiceName = "test",
          HostName = "test-host",
          ProcessId = 1,
        },
        Timestamp = DateTimeOffset.UtcNow,
      }],
      DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Local, Source = MessageSource.Local },
    };
    var body = JsonSerializer.Serialize(envelope, typeInfo);

    var decision = decider.Decide(props, body, (_, _) => typeInfo, combinedOptions);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process);
    await Assert.That(decision.Reason).IsEqualTo("Ok");
    await Assert.That(decision.Envelope).IsNotNull();
  }

  private static JsonTypeInfo? _resolveAlwaysNull(string typeName, JsonSerializerOptions options) => null;
}
