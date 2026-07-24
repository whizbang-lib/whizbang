using System.Runtime.CompilerServices;
using Whizbang.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Sagas.Generated;

/// <summary>
/// Auto-registration coordinator for Whizbang.Sagas's <see cref="SagasJsonContext"/>.
/// Mirrors the per-event registration pattern that
/// <c>Whizbang.Generators.MessageJsonContextGenerator</c> emits on the consumer
/// side — each event gets bare-type JsonTypeInfo (via <see cref="SagasJsonContext"/>),
/// envelope-wrapper JsonTypeInfo (via <see cref="SagasJsonContext"/>), wire-name
/// lookup (via <see cref="JsonContextRegistry.RegisterTypeName"/>), and polymorphic
/// dispatch registrations (via <see cref="JsonContextRegistry.RegisterDerivedType"/>).
/// Without all four layers a framework-published event can't round-trip through
/// the publish ↔ outbox ↔ transport ↔ consumer ↔ receptor pipeline.
/// </summary>
/// <remarks>
/// Any new framework-published runtime event added to
/// <see cref="SagasJsonContext"/> MUST also be added to the wire-name lookup and
/// polymorphic dispatch registrations here.
/// </remarks>
public static class SagasJsonContextInitializer {
  // CA2255: Intentional use of ModuleInitializer in library code for AOT-compatible JSON context registration.
#pragma warning disable CA2255
  [ModuleInitializer]
#pragma warning restore CA2255
  public static void Initialize() {
    // Register the JsonContext itself (carries bare + envelope JsonTypeInfo via the
    // [JsonSerializable] attributes on SagasJsonContext).
    JsonContextRegistry.RegisterContext(SagasJsonContext.Default);

    _registerEventRoutes<SagaCompletionWatchdogTickEvent>("Whizbang.Sagas.SagaCompletionWatchdogTickEvent");
    _registerEventRoutes<SagaCompletionAbandonedEvent>("Whizbang.Sagas.SagaCompletionAbandonedEvent");
  }

  /// <summary>
  /// Wire-name lookup + polymorphic dispatch registration for a single framework-published
  /// event type. Mirrors the per-event block <c>MessageJsonContextGenerator</c> emits for
  /// consumer events.
  /// </summary>
  /// <remarks>
  /// Wire-name lookup: every <c>GetTypeInfoByName</c> call site
  /// (<c>Dispatcher._serializeToJsonEnvelope</c>, <c>TransportConsumerWorker.PublishBulk</c>,
  /// <c>ServiceBusConsumerWorker</c>, <c>JsonLifecycleMessageDeserializer</c>,
  /// <c>EnvelopeSerializer</c>, <c>BodyClaimRehydrator</c>) passes an
  /// assembly-qualified type name; the registry strips Version/Culture/PublicKeyToken via
  /// <c>EventTypeMatchingHelper.NormalizeTypeName</c>, so the short "Full.Name, Asm" form
  /// matches every variant.
  ///
  /// Polymorphic dispatch: <c>GetPolymorphicTypeInfo&lt;TBase&gt;</c> builds a
  /// <c>JsonTypeInfo&lt;TBase&gt;</c> that knows about each registered derived type as a
  /// polymorphic alternative — required for reads of <c>MessageEnvelope&lt;IEvent&gt;</c>
  /// or <c>MessageEnvelope&lt;IMessage&gt;</c> (event-store polymorphic reads, certain
  /// lifecycle paths). Discriminator matches the consumer-side generator convention
  /// (full namespace-qualified type name, no assembly).
  /// </remarks>
  private static void _registerEventRoutes<TEvent>(string fullName) where TEvent : class, IEvent {
    JsonContextRegistry.RegisterTypeName(
      // Shared formatter derives "FullName, AssemblyName" from the type itself rather than
      // hardcoding the assembly — canonical '+'-nested form, consistent with every other wire key.
      TypeNameFormatter.Format(typeof(TEvent)),
      typeof(TEvent),
      SagasJsonContext.Default);
    JsonContextRegistry.RegisterDerivedType<IEvent, TEvent>(fullName);
    JsonContextRegistry.RegisterDerivedType<IMessage, TEvent>(fullName);
  }
}
