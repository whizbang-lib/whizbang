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
    var name = "Whizbang.Sagas.SagaCompletionWatchdogTickEvent, Whizbang.Sagas";
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
}
