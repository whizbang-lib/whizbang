using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Sagas;

namespace Whizbang.Sagas.Tests.Generated;

/// <summary>
/// Full lock-in of Whizbang.Sagas's framework-owned JSON registration. The framework
/// publishes exactly one runtime type itself (<see cref="SagaCompletionWatchdogTickEvent"/>,
/// emitted by <c>BaseSagaService.InitiateSagaAsync</c> via
/// <see cref="Services.ISagaEventEmitter.PublishAsync{T}"/>). For a consumer-side
/// transport-backed round-trip to succeed, FOUR registrations are required — the same
/// pattern <c>Whizbang.Generators.MessageJsonContextGenerator</c> emits per event in
/// the consumer's MessageJsonContext:
///
/// <list type="number">
///   <item><c>[JsonSerializable(typeof(TEvent))]</c> — bare JsonTypeInfo</item>
///   <item><c>[JsonSerializable(typeof(MessageEnvelope&lt;TEvent&gt;))]</c> — wire envelope JsonTypeInfo</item>
///   <item><c>RegisterTypeName("Full.Name, Asm", typeof(TEvent), Ctx)</c> — wire-name lookup
///     (used by Dispatcher, TransportConsumer, ServiceBus, Lifecycle, EnvelopeSerializer)</item>
///   <item><c>RegisterDerivedType&lt;IEvent, TEvent&gt;</c> + <c>RegisterDerivedType&lt;IMessage, TEvent&gt;</c>
///     — polymorphic dispatch for envelope reads of base interfaces</item>
/// </list>
///
/// <para>Each test locks one layer. Production exposed gaps #2/#3/#4 in successive deploys
/// because earlier "surgical" patches only addressed one path at a time.
/// This suite is the comprehensive lock so we don't fix-and-rediscover again.</para>
/// </summary>
[Category("Unit")]
[Category("Saga")]
[Category("Serialization")]
public class SagasJsonContextTests {

  [Test]
  public async Task SagasJsonContext_ResolvesBareTickEventViaGetTypeInfoAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = options.GetTypeInfo(typeof(SagaCompletionWatchdogTickEvent));

    await Assert.That(typeInfo).IsNotNull();
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(SagaCompletionWatchdogTickEvent));
  }

  [Test]
  public async Task SagasJsonContext_ResolvesMessageEnvelopeWrapperViaGetTypeInfoAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = options.GetTypeInfo(typeof(MessageEnvelope<SagaCompletionWatchdogTickEvent>));

    await Assert.That(typeInfo).IsNotNull();
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(MessageEnvelope<SagaCompletionWatchdogTickEvent>));
  }

  [Test]
  public async Task SagasJsonContext_ResolvesBareTickEventViaGetTypeInfoByNameAsync() {
    // The wire-name form that wh_outbox.message_type stores (no Version/Culture/PublicKeyToken).
    const string name = "Whizbang.Sagas.SagaCompletionWatchdogTickEvent, Whizbang.Sagas";
    var options = JsonContextRegistry.CreateCombinedOptions();

    var typeInfo = JsonContextRegistry.GetTypeInfoByName(name, options);
    await Assert.That(typeInfo).IsNotNull();
  }

  [Test]
  public async Task SagasJsonContext_ResolvesBareTickEventViaGetTypeInfoByName_FullAssemblyQualifiedFormAsync() {
    // The exact form Dispatcher._serializeToJsonEnvelope passes (eventType.AssemblyQualifiedName)
    // and the lifecycle hooks read from wh_outbox.message_type at publish time. Includes
    // Version/Culture/PublicKeyToken — must round-trip through NormalizeTypeName to the
    // short-form registration. Without this layer, a consumer's lifecycle hooks logged
    // "Failed to resolve message type 'Whizbang.Sagas.SagaCompletionWatchdogTickEvent,
    // Whizbang.Sagas, Version=0.742.2.0, Culture=neutral, PublicKeyToken=null'" on every
    // outbox batch in production.
    var fullName = typeof(SagaCompletionWatchdogTickEvent).AssemblyQualifiedName!;
    var options = JsonContextRegistry.CreateCombinedOptions();

    var typeInfo = JsonContextRegistry.GetTypeInfoByName(fullName, options);
    await Assert.That(typeInfo).IsNotNull();
  }

  [Test]
  public async Task SagasJsonContext_RegistersTickEventAsDerivedOfIEventAsync() {
    // The polymorphic dispatch path (GetPolymorphicTypeInfo<IEvent>) needs the framework's
    // own event to appear in the discovered derived-types set — otherwise reads of
    // MessageEnvelope<IEvent> from event store can't deserialize tick instances.
    var derivedTypes = JsonContextRegistry.GetRegisteredDerivedTypes<IEvent>().ToList();

    await Assert.That(derivedTypes).Contains(typeof(SagaCompletionWatchdogTickEvent));
  }

  [Test]
  public async Task SagasJsonContext_RegistersTickEventAsDerivedOfIMessageAsync() {
    // Mirror of the IEvent registration — every consumer-side event registers under
    // both bases, so the framework's own event must too.
    var derivedTypes = JsonContextRegistry.GetRegisteredDerivedTypes<IMessage>().ToList();

    await Assert.That(derivedTypes).Contains(typeof(SagaCompletionWatchdogTickEvent));
  }

  [Test]
  public async Task SagasJsonContext_RoundTripsSagaCompletionWatchdogTickEventAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var original = new SagaCompletionWatchdogTickEvent {
      SagaName = "BulkImport",
      EntityId = Guid.NewGuid(),
      StreamId = Guid.NewGuid(),
      RescheduleCount = 2,
    };

    var json = System.Text.Json.JsonSerializer.Serialize(original, options);
    var round = System.Text.Json.JsonSerializer.Deserialize<SagaCompletionWatchdogTickEvent>(json, options);

    await Assert.That(round).IsNotNull();
    await Assert.That(round!.SagaName).IsEqualTo(original.SagaName);
    await Assert.That(round.EntityId).IsEqualTo(original.EntityId);
    await Assert.That(round.StreamId).IsEqualTo(original.StreamId);
    await Assert.That(round.RescheduleCount).IsEqualTo(original.RescheduleCount);
  }

  // ── The continuation request (#1001) ─────────────────────────────────────
  //
  // Published by BaseSagaService.CompleteSagaAsync when a finished saga declared a continuation, so it
  // crosses the same outbox, transport and inbox boundaries as the tick, and needs the same four layers.

  private static SagaContinuationRequestedEvent _continuation() => new() {
    SagaName = "EnrichImport",
    EntityId = Guid.CreateVersion7(),
    StreamId = Guid.CreateVersion7(),
    ParentSagaName = "BulkImport",
    ParentSagaId = Guid.CreateVersion7(),
    ParentFinalStatus = SagaStatus.CompletedWithFailures,
  };

  /// <summary>
  /// The stored and wire forms of the continuation's type name both resolve: the short form the
  /// outbox and event store keep, and the assembly-qualified form the dispatcher and lifecycle pass.
  /// </summary>
  /// <param name="fullyQualified">Whether to ask with the assembly-qualified name.</param>
  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task ContinuationRequest_ResolvesByItsWireNameAsync(bool fullyQualified) {
    var name = fullyQualified
      ? typeof(SagaContinuationRequestedEvent).AssemblyQualifiedName!
      : "Whizbang.Sagas.SagaContinuationRequestedEvent, Whizbang.Sagas";
    var options = JsonContextRegistry.CreateCombinedOptions();

    var typeInfo = JsonContextRegistry.GetTypeInfoByName(name, options);

    await Assert.That(typeInfo).IsNotNull()
      .Because("a transport or the inbox resolves the payload by its stored name; unresolved, the continuation is never started");
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(SagaContinuationRequestedEvent));
  }

  /// <summary>The continuation is a derived type of both message bases, for polymorphic reads.</summary>
  [Test]
  public async Task ContinuationRequest_IsRegisteredAsADerivedTypeOfIEventAndIMessageAsync() {
    await Assert.That(JsonContextRegistry.GetRegisteredDerivedTypes<IEvent>()).Contains(typeof(SagaContinuationRequestedEvent));
    await Assert.That(JsonContextRegistry.GetRegisteredDerivedTypes<IMessage>()).Contains(typeof(SagaContinuationRequestedEvent));
  }

  /// <summary>
  /// A continuation serialized as the outbox stores it comes back, through its stored type name, with
  /// every field the continuation's receptor reads.
  /// </summary>
  [Test]
  public async Task ContinuationRequest_RoundTripsThroughItsStoredTypeNameAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var original = _continuation();

    var stored = System.Text.Json.JsonSerializer.SerializeToElement(original, options.GetTypeInfo(typeof(SagaContinuationRequestedEvent)));
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(TypeNameFormatter.Format(typeof(SagaContinuationRequestedEvent)), options)!;
    var round = (SagaContinuationRequestedEvent?)System.Text.Json.JsonSerializer.Deserialize(stored, typeInfo);

    await Assert.That(round).IsNotNull();
    await Assert.That(round!.SagaName).IsEqualTo(original.SagaName);
    await Assert.That(round.EntityId).IsEqualTo(original.EntityId);
    await Assert.That(round.StreamId).IsEqualTo(original.StreamId);
    await Assert.That(round.ParentSagaName).IsEqualTo(original.ParentSagaName);
    await Assert.That(round.ParentSagaId).IsEqualTo(original.ParentSagaId);
    await Assert.That(round.ParentFinalStatus).IsEqualTo(original.ParentFinalStatus);
  }

  /// <summary>A continuation read back as an <see cref="IEvent"/>, as the event store reads it, keeps its type.</summary>
  [Test]
  public async Task ContinuationRequest_RoundTripsAsAnIEventAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var eventInfo = JsonContextRegistry.GetPolymorphicTypeInfo<IEvent>(options)!;
    var original = _continuation();

    var json = System.Text.Json.JsonSerializer.Serialize<IEvent>(original, eventInfo);
    var round = System.Text.Json.JsonSerializer.Deserialize(json, eventInfo);

    await Assert.That(round).IsTypeOf<SagaContinuationRequestedEvent>();
    await Assert.That(((SagaContinuationRequestedEvent)round!).ParentSagaId).IsEqualTo(original.ParentSagaId);
  }
}
