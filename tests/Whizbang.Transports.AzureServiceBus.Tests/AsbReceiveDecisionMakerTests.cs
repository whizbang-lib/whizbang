using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// RED-first regression locks for slice 1 hotfix's behavior change: when the local service
/// can't bind a received envelope to a CLR type (unknown JsonTypeInfo or shape rejection),
/// the broker is acked and the message dropped instead of dead-lettered. The legacy receive
/// path dead-lettered every such case, which produced unbounded ASB DLQ accumulation on a consumer
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

  // ============================================================
  // Slice 2 — receptor-registry filter at receive
  //
  // Even when the envelope deserializes successfully (typed inner CLR type IS registered),
  // some services receive events they have NO handler for (no IReceptor + no perspective Apply).
  // BFF observation 2026-05-03: 2,165 stuck inbox rows for ConnectionUpdatedEventHandler — a
  // handler that lives in a consumer.UserService, not BFF. The dispatcher silently no-op'd; rows
  // accumulated forever. With this filter, those messages drop at receive instead of storing.
  // ============================================================

  [Test]
  public async Task Decide_PayloadHasNoLocalConsumer_ReturnsAckAndDropWithReasonNoLocalConsumerAsync() {
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[JsonElement]]");
    var combinedOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = (JsonTypeInfo<MessageEnvelope<JsonElement>>)combinedOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var envelope = _makeEnvelope();
    var body = JsonSerializer.Serialize(envelope, typeInfo);

    // Local registry says: no local consumer for THIS payload type.
    bool _isHandledLocally(Type t) => false;

    var decision = decider.Decide(props, body, (_, _) => typeInfo, combinedOptions, _isHandledLocally);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop);
    await Assert.That(decision.Reason).IsEqualTo("NoLocalConsumer");
  }

  [Test]
  public async Task Decide_PayloadHasLocalConsumer_ReturnsProcessAsync() {
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[JsonElement]]");
    var combinedOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = (JsonTypeInfo<MessageEnvelope<JsonElement>>)combinedOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var envelope = _makeEnvelope();
    var body = JsonSerializer.Serialize(envelope, typeInfo);

    bool _isHandledLocally(Type t) => true;

    var decision = decider.Decide(props, body, (_, _) => typeInfo, combinedOptions, _isHandledLocally);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process);
    await Assert.That(decision.Reason).IsEqualTo("Ok");
  }

  [Test]
  public async Task Decide_NoLocalRegistryProvided_KeepsLegacyBehavior_ProcessAsync() {
    // Backward compat: when isHandledLocally is null, the filter is bypassed so existing
    // call sites that don't yet wire a registry behave as before slice 2.
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[JsonElement]]");
    var combinedOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = (JsonTypeInfo<MessageEnvelope<JsonElement>>)combinedOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var envelope = _makeEnvelope();
    var body = JsonSerializer.Serialize(envelope, typeInfo);

    var decision = decider.Decide(props, body, (_, _) => typeInfo, combinedOptions, isHandledLocally: null);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process);
  }

  [Test]
  public async Task Decide_UnconsumedPayload_OnAbsorbedNamespace_ReturnsProcessAsync() {
    // A payload with NO local consumer whose NAMESPACE is absorbed must NOT be dropped — it's kept so the
    // inbox -> event-store write can persist it for a later rebuild. (JsonElement's namespace = System.Text.Json.)
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[JsonElement]]");
    var combinedOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = (JsonTypeInfo<MessageEnvelope<JsonElement>>)combinedOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var envelope = _makeEnvelope();
    var body = JsonSerializer.Serialize(envelope, typeInfo);

    static bool _isHandledLocally(Type t) => false; // no local consumer
    var absorbed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System.Text.Json" };

    var decision = decider.Decide(
      props, body, (_, _) => typeInfo, combinedOptions, _isHandledLocally, absorbedNamespaces: absorbed);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process);
  }

  // ============================================================
  // Body-offload (claim-check) — a message whose serialized body exceeds the transport limit is
  // offloaded: the wire payload becomes BodyClaimEnvelopePayload, the ORIGINAL type is carried in
  // whizbang.original-type, and TransportConsumerWorker rehydrates the real message downstream.
  //
  // REGRESSION (production 2026-07): "bulk → Approved" (21,398 job IDs) silently did nothing. The
  // BulkUpdateJobStatusesCommand exceeded ASB's 256 KB limit → offloaded. On receive, Decide's
  // Slice-2 NO_LOCAL_CONSUMER filter ran against the CLAIM payload type (BodyClaimEnvelopePayload),
  // which no service has a receptor for → AckAndDrop before rehydration. Every offloaded message
  // died here. The filter must not fire for claim payloads — the real type is rehydrated downstream.
  // ============================================================

  [Test]
  public async Task Decide_OffloadedClaimMessage_OriginalHandledLocally_ReturnsProcessNotDropAsync() {
    var decider = new AsbReceiveDecisionMaker();
    var combinedOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var claimTypeInfo = (JsonTypeInfo<MessageEnvelope<BodyClaimEnvelopePayload>>)
      combinedOptions.GetTypeInfo(typeof(MessageEnvelope<BodyClaimEnvelopePayload>));

    var claim = new MessageBodyClaim(
      ProviderName: "test-store",
      StorageKey: "key-123",
      Size: 900_000,
      ContentHash: "sha256-deadbeef",
      ContentType: "application/json",
      UploadedAt: DateTimeOffset.UtcNow);
    var originalEnvelopeType =
      "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Commands.BigCommand, MyApp.Contracts]], Whizbang.Core";

    var envelope = new MessageEnvelope<BodyClaimEnvelopePayload> {
      MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
      Payload = new BodyClaimEnvelopePayload(claim, "application/json", originalEnvelopeType),
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
    var body = JsonSerializer.Serialize(envelope, claimTypeInfo);

    // Faithful to the wire: is-claim arrives as an AMQP boolean (ToString() == "True"); ENVELOPE_TYPE
    // names the claim envelope; original-type carries the real type.
    var props = new Dictionary<string, object> {
      [AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY] =
        "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Offloads.BodyClaimEnvelopePayload, Whizbang.Core]], Whizbang.Core",
      [BodyOffloadPostSerializeHook.IS_CLAIM_METADATA_KEY] = true,
      [BodyOffloadPostSerializeHook.ORIGINAL_TYPE_METADATA_KEY] = originalEnvelopeType,
    };

    // Service consumes its real messages but has NO receptor for the internal claim type.
    bool _isHandledLocally(Type t) => t != typeof(BodyClaimEnvelopePayload);

    var decision = decider.Decide(props, body, (_, _) => claimTypeInfo, combinedOptions, _isHandledLocally);

    // Must Process so TransportConsumerWorker.MaybeRehydrateAsync can download + rehydrate the
    // original message. Today this is AckAndDrop / NoLocalConsumer — the silent bulk drop.
    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process);
    await Assert.That(decision.Reason).IsEqualTo("Ok");
  }

  // ============================================================
  // Slice 4 — multi-pass type binder fallback when JsonContextRegistry misses
  //
  // JsonContextRegistry only knows types whose source-generated JsonTypeInfo was registered
  // via [ModuleInitializer]. When that misses but the type IS resolvable in the loaded
  // assemblies (e.g., assembly-rename case the registry can't follow), the binder cascade
  // resolves it and the decision maker re-attempts JsonTypeInfo via options.GetTypeInfo.
  // ============================================================

  [Test]
  public async Task Decide_RegistryMisses_BinderResolves_ReturnsProcessAsync() {
    // Force the registry-side lookup to miss; binder resolves via a permissive impl that
    // returns the MessageEnvelope<JsonElement> type directly. options.GetTypeInfo(type)
    // produces a typeInfo that successfully deserializes the body.
    var combinedOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = _makeEnvelope();
    var envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[JsonElement]], Whizbang.Core";
    var typeInfo = (JsonTypeInfo<MessageEnvelope<JsonElement>>)combinedOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var body = JsonSerializer.Serialize(envelope, typeInfo);

    var decider = new AsbReceiveDecisionMaker();
    var binder = new FakeBinder(typeof(MessageEnvelope<JsonElement>));
    var props = _withEnvelopeType(envelopeType);

    var decision = decider.Decide(
      props, body,
      _resolveAlwaysNull,  // registry says no
      combinedOptions,
      isHandledLocally: null,
      rawReceptorRegistry: null,
      typeBinder: binder);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process);
    await Assert.That(decision.Reason).IsEqualTo("Ok");
    await Assert.That(decision.Envelope).IsNotNull();
  }

  [Test]
  public async Task Decide_RegistryMisses_BinderAlsoMisses_FallsThroughToAckAndDropAsync() {
    var decider = new AsbReceiveDecisionMaker();
    var binder = new FakeBinder(null);  // binder also misses
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[Unknown]]");

    var decision = decider.Decide(
      props, """{"p":{}}""",
      _resolveAlwaysNull, _jsonOptions,
      isHandledLocally: null, rawReceptorRegistry: null,
      typeBinder: binder);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop);
    await Assert.That(decision.Reason).IsEqualTo("MissingJsonTypeInfo");
  }

  private sealed class FakeBinder(Type? returnType) : IMessageTypeBinder {
    public Type? Bind(string assemblyQualifiedName) => returnType;
    public (Type? Type, MessageTypeBinderPass Pass) BindWithDiagnostics(string assemblyQualifiedName) =>
      (returnType, returnType is null ? MessageTypeBinderPass.Miss : MessageTypeBinderPass.TypeFullNameAcrossAssemblies);
  }

  // ============================================================
  // Slice 5 — opt-in raw JSON receptor wire-up
  //
  // When the typed JsonTypeInfo lookup misses but a raw receptor is registered for the
  // envelope's inner message type, the decision routes to InvokeRawReceptor instead of
  // AckAndDrop. Transport invokes the raw receptor with the envelope's "p" payload as a
  // JsonElement, then acks the broker.
  // ============================================================

  private sealed class FakeRawReceptor(string targetName) : Whizbang.Core.Messaging.IRawReceptor {
    public string TargetMessageTypeName { get; } = targetName;
    public Task HandleAsync(JsonElement payload, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
  }

  [Test]
  public async Task Decide_TypeInfoMisses_RawReceptorRegistered_ReturnsInvokeRawReceptorAsync() {
    var envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Events.Foo, MyApp.Contracts]], Whizbang.Core";
    var receptor = new FakeRawReceptor("MyApp.Events.Foo, MyApp.Contracts");
    var rawRegistry = new Whizbang.Core.Messaging.RawReceptorRegistry([receptor]);
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType(envelopeType);

    var decision = decider.Decide(
      props,
      """{"p":{"someField":42},"id":"01234567-89ab-7def-0123-456789abcdef"}""",
      _resolveAlwaysNull,  // forces JsonTypeInfo miss
      _jsonOptions,
      isHandledLocally: null,
      rawReceptorRegistry: rawRegistry);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.InvokeRawReceptor);
    await Assert.That(decision.Reason).IsEqualTo("RawReceptorMatch");
    await Assert.That(decision.RawReceptor).IsSameReferenceAs(receptor);
    await Assert.That(decision.RawPayload).IsNotNull();
  }

  [Test]
  public async Task Decide_TypeInfoMisses_NoRawReceptor_FallsThroughToAckAndDropAsync() {
    var rawRegistry = new Whizbang.Core.Messaging.RawReceptorRegistry([]);
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[Unknown.Type, UnknownAsm]]");

    var decision = decider.Decide(
      props, """{"p":{}}""", _resolveAlwaysNull, _jsonOptions,
      isHandledLocally: null,
      rawReceptorRegistry: rawRegistry);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop);
    await Assert.That(decision.Reason).IsEqualTo("MissingJsonTypeInfo");
  }

  [Test]
  public async Task Decide_TypeInfoMisses_RawReceptorButMalformedBody_FallsThroughToAckAndDropAsync() {
    var receptor = new FakeRawReceptor("MyApp.Foo, MyApp");
    var rawRegistry = new Whizbang.Core.Messaging.RawReceptorRegistry([receptor]);
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Foo, MyApp]]");

    var decision = decider.Decide(
      props, "not valid json{{{", _resolveAlwaysNull, _jsonOptions,
      isHandledLocally: null,
      rawReceptorRegistry: rawRegistry);

    // Can't extract payload → fall through, don't fabricate an InvokeRawReceptor.
    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop);
    await Assert.That(decision.Reason).IsEqualTo("MissingJsonTypeInfo");
  }

  private static MessageEnvelope<JsonElement> _makeEnvelope() => new() {
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
}
