using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Coverage-round-23 targets for <see cref="AsbReceiveDecisionMaker.Decide"/>: the
/// <c>InvalidOperationException</c> catch inside the multi-pass type-binder fallback, and two
/// early-return branches inside the slice-5 raw-receptor probe (<c>_tryRawReceptor</c>) that the
/// sibling <see cref="AsbReceiveDecisionMakerTests"/> suite never reaches.
/// </summary>
/// <tests>src/Whizbang.Transports.AzureServiceBus/AsbReceiveDecisionMaker.cs</tests>
public class AsbReceiveDecisionMakerCoverageTests {

  private static readonly JsonSerializerOptions _jsonOptions = new() {
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
  };

  private static Dictionary<string, object> _withEnvelopeType(string typeName) =>
    new() {
      [AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY] = typeName,
    };

  private static JsonTypeInfo? _resolveAlwaysNull(string typeName, JsonSerializerOptions options) => null;

  private sealed class _fakeBinder(Type returnType) : IMessageTypeBinder {
    public Type? Bind(string assemblyQualifiedName) => returnType;
    public (Type? Type, MessageTypeBinderPass Pass) BindWithDiagnostics(string assemblyQualifiedName) =>
      (returnType, MessageTypeBinderPass.TypeFullNameAcrossAssemblies);
  }

  /// <summary>
  /// A resolver that always answers with a <see cref="JsonTypeInfo"/> built for a DIFFERENT
  /// <see cref="JsonSerializerOptions"/> instance than the one that queried it -- the shape STJ
  /// rejects with <see cref="InvalidOperationException"/> rather than the
  /// <see cref="NotSupportedException"/> a plain metadata miss produces. This mirrors a real
  /// misconfiguration: two combined source-generated contexts whose <c>JsonTypeInfo</c> objects
  /// carry their own originating <c>Options</c>, queried through a third combined instance.
  /// </summary>
  private sealed class _foreignOptionsResolver(JsonTypeInfo foreignTypeInfo) : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => foreignTypeInfo;
  }

  private sealed class _fakeRawReceptor(string targetName) : IRawReceptor {
    public string TargetMessageTypeName { get; } = targetName;
    public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken) => Task.CompletedTask;
  }

  // A binder-bound type that is LOADABLE but whose JsonTypeInfo comes back tied to the wrong
  // JsonSerializerOptions is the same "not servable" shape as a metadata miss. If this
  // InvalidOperationException ever escaped Decide instead of degrading to ack+drop, the transport
  // would abandon the delivery and the broker would redeliver the same undeserializable message
  // forever -- the exact abandon-loop the sibling NotSupportedException test already pins for the
  // metadata-miss case.
  [Test]
  public async Task Decide_BinderBindsButResolverReturnsForeignOptionsTypeInfo_ReturnsAckAndDropAsync() {
    var foreignOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    var foreignTypeInfo = foreignOptions.GetTypeInfo(typeof(string));
    var badOptions = new JsonSerializerOptions {
      TypeInfoResolver = new _foreignOptionsResolver(foreignTypeInfo)
    };
    var decider = new AsbReceiveDecisionMaker();
    // The binder's return type is arbitrary -- only its "loadable but not servable through this
    // options instance" shape matters, so a BCL type keeps the test double honest and simple.
    var binder = new _fakeBinder(typeof(string));
    var props = _withEnvelopeType("MyApp.Events.Foo, MyApp.Contracts");

    var decision = decider.Decide(
      props, """{"p":1}""", _resolveAlwaysNull, badOptions,
      isHandledLocally: null,
      rawReceptorRegistry: null,
      typeBinder: binder);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop)
      .Because("an unservable bound type must degrade to ack+drop, never escape Decide as an unhandled exception");
    await Assert.That(decision.Reason).IsEqualTo("MissingJsonTypeInfo");
  }

  // A raw receptor can only ever be keyed by the envelope's inner type name. When the envelope
  // type name isn't generic-shaped at all, there is no inner name to extract and therefore nothing
  // that could ever match a registered receptor. If this early return regressed into calling the
  // registry with a nonsensical key (or worse, throwing), a message with a malformed EnvelopeType
  // property would crash the receive path instead of degrading to the same ack+drop every other
  // unbindable message gets.
  [Test]
  public async Task Decide_TypeInfoMisses_EnvelopeTypeNameNotGeneric_FallsThroughToAckAndDropAsync() {
    var rawRegistry = new RawReceptorRegistry([]);
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType("PlainNonGenericEnvelopeTypeName");

    var decision = decider.Decide(
      props, """{"p":{}}""", _resolveAlwaysNull, _jsonOptions,
      isHandledLocally: null,
      rawReceptorRegistry: rawRegistry);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop)
      .Because("with no inner type name to extract, there is nothing a raw receptor could ever match -- the probe must still degrade to ack+drop rather than stall or throw");
    await Assert.That(decision.Reason).IsEqualTo("MissingJsonTypeInfo");
  }

  // The registry has a receptor matching the envelope's inner type, but the envelope body never
  // carried a "p" (payload) property -- valid JSON, wrong shape. If this null-payload guard
  // regressed, InvokeRawReceptor would be handed a raw receptor with no payload to give it,
  // crashing (or silently no-op'ing) the receptor's handler instead of the message safely
  // degrading to ack+drop.
  [Test]
  public async Task Decide_TypeInfoMisses_RawReceptorRegistered_BodyMissingPayloadProperty_FallsThroughToAckAndDropAsync() {
    var envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Events.Foo, MyApp.Contracts]], Whizbang.Core";
    var receptor = new _fakeRawReceptor("MyApp.Events.Foo, MyApp.Contracts");
    var rawRegistry = new RawReceptorRegistry([receptor]);
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType(envelopeType);

    var decision = decider.Decide(
      props, """{"id":"01234567-89ab-7def-0123-456789abcdef"}""", _resolveAlwaysNull, _jsonOptions,
      isHandledLocally: null,
      rawReceptorRegistry: rawRegistry);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop)
      .Because("a matched receptor with no payload to hand it must still degrade to ack+drop, not fabricate an InvokeRawReceptor decision with a null payload");
    await Assert.That(decision.Reason).IsEqualTo("MissingJsonTypeInfo");
  }
}
