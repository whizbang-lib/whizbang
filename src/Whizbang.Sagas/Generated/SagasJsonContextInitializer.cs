using System.Runtime.CompilerServices;
using Whizbang.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Sagas.Generated;

/// <summary>
/// Auto-registration coordinator for Whizbang.Sagas's <see cref="SagasJsonContext"/>.
/// Mirrors the per-event registration pattern that
/// <c>Whizbang.Generators.MessageJsonContextGenerator</c> emits on the consumer
/// side (each event gets bare-type JsonTypeInfo, envelope-wrapper JsonTypeInfo,
/// wire-name lookup, and polymorphic dispatch registrations). Without this set,
/// the framework's auto-armed <see cref="SagaCompletionWatchdogTickEvent"/> can't
/// round-trip through the publish ↔ outbox ↔ Service Bus ↔ consumer ↔ receptor
/// pipeline — production dev tripped each of the four gaps in successive deploys
/// (2026-06-24).
/// </summary>
/// <remarks>
/// Whizbang.Sagas publishes exactly ONE framework-internal type:
/// <see cref="SagaCompletionWatchdogTickEvent"/> (line 132 of
/// <c>BaseSagaService.InitiateSagaAsync</c>). Every other PublishAsync call uses
/// consumer-provided generic event types. Any future framework-published event
/// MUST be added to all four registration layers here.
/// </remarks>
public static class SagasJsonContextInitializer {
  // CA2255: Intentional use of ModuleInitializer in library code for AOT-compatible JSON context registration.
#pragma warning disable CA2255
  [ModuleInitializer]
#pragma warning restore CA2255
  public static void Initialize() {
    // Layer 1: register the JsonContext itself (carries bare + envelope JsonTypeInfo
    // via the [JsonSerializable] attributes on SagasJsonContext).
    JsonContextRegistry.RegisterContext(SagasJsonContext.Default);

    // Layer 2: wire-name lookup for the BARE type. Every GetTypeInfoByName call site
    // (Dispatcher._serializeToJsonEnvelope, TransportConsumerWorker.PublishBulk,
    // ServiceBusConsumerWorker, JsonLifecycleMessageDeserializer, EnvelopeSerializer,
    // BodyClaimRehydrator) passes an assembly-qualified type name; the registry
    // strips Version/Culture/PublicKeyToken via NormalizeTypeName, so the short
    // "Full.Name, Asm" form matches every variant.
    JsonContextRegistry.RegisterTypeName(
      "Whizbang.Sagas.SagaCompletionWatchdogTickEvent, Whizbang.Sagas",
      typeof(SagaCompletionWatchdogTickEvent),
      SagasJsonContext.Default);

    // Layer 3: polymorphic dispatch under IEvent + IMessage. GetPolymorphicTypeInfo<TBase>
    // builds a JsonTypeInfo<TBase> that knows about each registered derived type as a
    // polymorphic alternative — required for reads of MessageEnvelope<IEvent> /
    // MessageEnvelope<IMessage> (event-store polymorphic reads, certain lifecycle paths).
    // The discriminator string matches the consumer-side generator's convention
    // (full namespace-qualified type name, no assembly).
    JsonContextRegistry.RegisterDerivedType<IEvent, SagaCompletionWatchdogTickEvent>(
      "Whizbang.Sagas.SagaCompletionWatchdogTickEvent");
    JsonContextRegistry.RegisterDerivedType<IMessage, SagaCompletionWatchdogTickEvent>(
      "Whizbang.Sagas.SagaCompletionWatchdogTickEvent");
  }
}
