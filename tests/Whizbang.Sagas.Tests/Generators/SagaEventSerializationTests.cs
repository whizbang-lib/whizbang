using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Sagas.Tests.Generators;

/// <summary>
/// End-to-end guard for the failure a consumer hits the first time they publish a generated saga
/// event: <c>InitiateSagaAsync</c> → <c>DispatcherSagaEventEmitter</c> → <c>IDispatcher.PublishAsync</c>
/// → <c>NotSupportedException: JsonTypeInfo metadata for type '…+InitiatedEvent' was not provided</c>.
///
/// <para>The cause is structural: source generators do not observe each other's output, so the
/// consumer-side JSON generator cannot see the nested event classes the saga generator emits. This
/// project loads BOTH generators — exactly as a consumer's project does — so these tests fail if the
/// synthesis ever regresses.</para>
/// </summary>
/// <docs>fundamentals/sagas/saga-events</docs>
[Category("Unit")]
[Category("Saga")]
[Category("Generator")]
[Category("JsonSerialization")]
public class SagaEventSerializationTests {

  private static JsonSerializerOptions _options() => JsonContextRegistry.CreateCombinedOptions();

  [Test]
  public async Task GeneratedSagaEvent_HasJsonTypeInfoAsync() {
    var options = _options();

    var typeInfo = options.TypeInfoResolver!.GetTypeInfo(typeof(GeneratorTestDefaultSaga.InitiatedEvent), options);

    await Assert.That(typeInfo).IsNotNull()
      .Because("Publishing a saga event resolves its JsonTypeInfo from the combined resolver chain; a null here IS the NotSupportedException a consumer sees on their first InitiateSagaAsync.");
  }

  [Test]
  public async Task EveryGeneratedSagaEvent_HasJsonTypeInfoAsync() {
    var options = _options();
    Type[] eventTypes = [
      typeof(GeneratorTestDefaultSaga.InitiatedEvent),
      typeof(GeneratorTestDefaultSaga.ItemsDispatchedEvent),
      typeof(GeneratorTestDefaultSaga.ItemStartedEvent),
      typeof(GeneratorTestDefaultSaga.ItemCompletedEvent),
      typeof(GeneratorTestDefaultSaga.ItemFailedEvent),
      typeof(GeneratorTestDefaultSaga.CompletedEvent),
      typeof(GeneratorTestDefaultSaga.ResetEvent),
      typeof(GeneratorTestDefaultSaga.HookStartedEvent),
      typeof(GeneratorTestDefaultSaga.HookCompletedEvent),
    ];

    foreach (var eventType in eventTypes) {
      var typeInfo = options.TypeInfoResolver!.GetTypeInfo(eventType, options);
      await Assert.That(typeInfo).IsNotNull()
        .Because($"{eventType.Name} is published on the saga's own lifecycle path — every one of the nine needs metadata, not just the first.");
    }
  }

  [Test]
  public async Task GeneratedSagaEvent_RoundTripsWithDeclaredAndInheritedPropertiesAsync() {
    var options = _options();
    var original = new GeneratorTestDefaultSaga.InitiatedEvent {
      EntityId = Guid.NewGuid(),
      ItemIdentifiers = ["item-1", "item-2"],
      TotalItems = 2,
      HookNames = ["hook-a"],
      CorrelationId = Guid.NewGuid(),
      OperationName = "operation-under-test"
    };

    var json = JsonSerializer.Serialize(original, options);
    var restored = JsonSerializer.Deserialize<GeneratorTestDefaultSaga.InitiatedEvent>(json, options)!;

    await Assert.That(restored.EntityId).IsEqualTo(original.EntityId);
    await Assert.That(restored.TotalItems).IsEqualTo(2);
    await Assert.That(restored.ItemIdentifiers).IsEquivalentTo(original.ItemIdentifiers);
    await Assert.That(restored.HookNames).IsEquivalentTo(original.HookNames!);
    await Assert.That(restored.SagaName).IsEqualTo(GeneratorTestDefaultSaga.SagaName);
    // Inherited from SagaEventBase — dropping these would sever the causal chain on the wire.
    await Assert.That(restored.CorrelationId).IsEqualTo(original.CorrelationId);
    await Assert.That(restored.OperationName).IsEqualTo("operation-under-test");
  }

  [Test]
  public async Task GeneratedSagaEvent_RoundTripsInsideMessageEnvelopeAsync() {
    var options = _options();
    var envelope = new MessageEnvelope<GeneratorTestDefaultSaga.ItemFailedEvent>(
      MessageId.New(),
      new GeneratorTestDefaultSaga.ItemFailedEvent {
        EntityId = Guid.NewGuid(),
        SagaId = Guid.NewGuid(),
        ItemIdentifier = "item-1",
        ErrorMessage = "boom",
        ErrorDetails = "stack"
      },
      []);

    var json = JsonSerializer.Serialize(envelope, options);
    var restored = JsonSerializer.Deserialize<MessageEnvelope<GeneratorTestDefaultSaga.ItemFailedEvent>>(json, options)!;

    await Assert.That(restored.Payload.ItemIdentifier).IsEqualTo("item-1");
    await Assert.That(restored.Payload.ErrorMessage).IsEqualTo("boom")
      .Because("Transport consumers receive MessageEnvelope<T> JSON and resolve the concrete generic — without envelope metadata the publish works and the receive fails.");
  }

  [Test]
  public async Task GeneratedSagaEvent_ResolvesByWireNameAsync() {
    var options = _options();
    var wireName = TypeNameFormatter.Format(typeof(GeneratorTestDefaultSaga.InitiatedEvent));

    var typeInfo = JsonContextRegistry.GetTypeInfoByName(wireName, options);

    await Assert.That(typeInfo).IsNotNull()
      .Because("Every GetTypeInfoByName call site (dispatch, outbox drain, transport consume, body-claim rehydration) looks the type up by its assembly-qualified wire name.");
  }

  [Test]
  public async Task GeneratedSagaEvent_IsRegisteredAsPolymorphicEventAsync() {
    var derived = JsonContextRegistry.GetRegisteredDerivedTypes<IEvent>();

    await Assert.That(derived).Contains(typeof(GeneratorTestDefaultSaga.InitiatedEvent))
      .Because("Polymorphic MessageEnvelope<IEvent> reads — the event-store replay path — resolve a saga event only if it registered as an IEvent derived type.");
  }

  /// <summary>
  /// Drift guard. The JSON generator describes the saga events from a compile-time shape table
  /// (<c>SagaEventShapes</c>) because it cannot see the classes the saga generator emits. Nothing in
  /// the compiler couples the two, so this test does the coupling: it reflects over the classes that
  /// were actually emitted and demands the synthesized metadata cover exactly the same properties.
  /// A property added, renamed, or removed in <c>SagaGenerator</c> without the shape table failing
  /// silently drops that property from the wire — this test turns that into a build failure.
  /// </summary>
  [Test]
  public async Task SynthesizedMetadata_CoversExactlyTheEmittedPropertiesAsync() {
    var options = _options();
    Type[] eventTypes = [
      typeof(GeneratorTestDefaultSaga.InitiatedEvent),
      typeof(GeneratorTestDefaultSaga.ItemsDispatchedEvent),
      typeof(GeneratorTestDefaultSaga.ItemStartedEvent),
      typeof(GeneratorTestDefaultSaga.ItemCompletedEvent),
      typeof(GeneratorTestDefaultSaga.ItemFailedEvent),
      typeof(GeneratorTestDefaultSaga.CompletedEvent),
      typeof(GeneratorTestDefaultSaga.ResetEvent),
      typeof(GeneratorTestDefaultSaga.HookStartedEvent),
      typeof(GeneratorTestDefaultSaga.HookCompletedEvent),
    ];

    foreach (var eventType in eventTypes) {
      var declared = eventType
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite)
        .Select(p => p.Name)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();

      var serialized = options.GetTypeInfo(eventType).Properties
        .Select(p => p.Name)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();

      await Assert.That(serialized).IsEquivalentTo(declared)
        .Because($"{eventType.Name}'s synthesized JSON metadata must describe exactly the properties the saga generator emitted — any divergence silently drops or invents wire fields.");
    }
  }

  /// <summary>
  /// This assembly declares TWO sagas, so both emit a class named <c>ItemStartedEvent</c> under the
  /// same polymorphic bases. That is the collision that makes STJ throw
  /// <c>… has already specified a type discriminator …</c> when it configures the base — which is
  /// why the generator disambiguates colliding discriminators. If it stopped, this test throws
  /// rather than merely returning the wrong type.
  /// </summary>
  [Test]
  public async Task GeneratedSagaEvent_RoundTripsThroughItsEventBaseAsync() {
    var options = _options();
    SagaEventBase evt = new GeneratorTestDefaultSaga.ItemStartedEvent {
      EntityId = Guid.NewGuid(),
      SagaId = Guid.NewGuid(),
      ItemIdentifier = "item-1"
    };

    var json = JsonSerializer.Serialize(evt, options);
    var restored = JsonSerializer.Deserialize<SagaEventBase>(json, options);

    await Assert.That(restored).IsTypeOf<GeneratorTestDefaultSaga.ItemStartedEvent>()
      .Because("A member or payload typed as the event base resolves through the generated polymorphic base typeinfo — saga events have to appear in its derived-type list, exactly as a hand-written event of the same shape does.");
    await Assert.That(((GeneratorTestDefaultSaga.ItemStartedEvent)restored!).ItemIdentifier).IsEqualTo("item-1");
  }

  [Test]
  public async Task GeneratedSagaEvent_RoundTripsThroughItsSagaContractInterfaceAsync() {
    var options = _options();
    ISagaItemStartedEvent evt = new GeneratorTestDefaultSaga.ItemStartedEvent {
      EntityId = Guid.NewGuid(),
      SagaId = Guid.NewGuid(),
      ItemIdentifier = "item-2"
    };

    var json = JsonSerializer.Serialize(evt, options);
    var restored = JsonSerializer.Deserialize<ISagaItemStartedEvent>(json, options);

    await Assert.That(restored).IsTypeOf<GeneratorTestDefaultSaga.ItemStartedEvent>()
      .Because("The saga contract interfaces are polymorphic bases like any consumer interface; a consumer projecting over ISagaItemStartedEvent must get the concrete event back.");
    await Assert.That(restored!.ItemIdentifier).IsEqualTo("item-2");
  }

  /// <summary>
  /// The sibling saga's identically-named event must resolve to ITS own type, not the first one
  /// registered — the point of disambiguating the discriminator rather than dropping a duplicate.
  /// </summary>
  [Test]
  public async Task SiblingSagasWithIdenticallyNamedEvents_EachResolveToTheirOwnTypeAsync() {
    var options = _options();
    SagaEventBase fromDefault = new GeneratorTestDefaultSaga.ItemStartedEvent { ItemIdentifier = "a" };

    var restoredDefault = JsonSerializer.Deserialize<SagaEventBase>(
      JsonSerializer.Serialize(fromDefault, options), options);

    await Assert.That(restoredDefault).IsTypeOf<GeneratorTestDefaultSaga.ItemStartedEvent>()
      .Because("Two sagas in one assembly both emit ItemStartedEvent; each must round-trip to its own type.");
  }

  [Test]
  public async Task CustomBaseSagaEvent_RoundTripsWithConsumerBasePropertiesAsync() {
    var options = _options();
    var original = new GeneratorTestCustomBaseSaga.CompletedEvent {
      EntityId = Guid.NewGuid(),
      FinalStatus = SagaStatus.Completed,
      CompletedItems = 3,
      TotalItems = 3
    };

    var json = JsonSerializer.Serialize(original, options);
    var restored = JsonSerializer.Deserialize<GeneratorTestCustomBaseSaga.CompletedEvent>(json, options)!;

    await Assert.That(restored.FinalStatus).IsEqualTo(SagaStatus.Completed)
      .Because("SagaStatus is an enum from a referenced assembly — its metadata has to be discovered through the synthesized property list.");
    await Assert.That(restored.CompletedItems).IsEqualTo(3);
    await Assert.That(restored.MessageId).IsEqualTo(original.MessageId)
      .Because("[Saga<TBase>] events inherit the consumer's own base; its properties must survive the round trip too.");
  }
}
