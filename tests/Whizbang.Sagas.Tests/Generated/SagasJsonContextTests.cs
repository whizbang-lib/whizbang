using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Sagas;

namespace Whizbang.Sagas.Tests.Generated;

/// <summary>
/// Locks Whizbang.Sagas's <c>SagasJsonContext</c> + <c>[ModuleInitializer]</c>
/// auto-registration with the cross-assembly <see cref="JsonContextRegistry"/>.
///
/// <para>The runtime event types Whizbang.Sagas itself publishes —
/// e.g. <see cref="SagaCompletionWatchdogTickEvent"/> emitted by
/// <c>BaseSagaService.InitiateSagaAsync</c> — are never referenced from the
/// consumer's source, so the per-consumer <c>MessageJsonContextGenerator</c>
/// can't see them. Without a framework-owned context, every consumer of
/// Whizbang.Sagas hits a <c>JsonTypeInfo metadata for type ... was not
/// provided</c> failure the first time <c>InitiateSagaAsync</c> publishes
/// the watchdog tick (production dev 2026-06-24).</para>
///
/// <para>These tests prove the framework's own JsonContext is registered on
/// load and that <see cref="JsonContextRegistry.CreateCombinedOptions"/>
/// resolves <see cref="SagaCompletionWatchdogTickEvent"/> without throwing
/// — locking the architectural invariant that Whizbang framework packages
/// own their own JSON contexts.</para>
/// </summary>
[Category("Unit")]
[Category("Saga")]
[Category("Serialization")]
public class SagasJsonContextTests {

  [Test]
  public async Task SagasJsonContext_RegistersOnModuleLoad_ResolvesSagaCompletionWatchdogTickEventAsync() {
    // [ModuleInitializer] in SagasJsonContextInitializer must have already run as
    // a side effect of Whizbang.Sagas assembly load.
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Without the framework-owned context this throws NotSupportedException with
    // "JsonTypeInfo metadata for type 'Whizbang.Sagas.SagaCompletionWatchdogTickEvent'
    // was not provided by TypeInfoResolver". With it, the resolver chain returns a
    // strongly-typed JsonTypeInfo<SagaCompletionWatchdogTickEvent>.
    var typeInfo = options.GetTypeInfo(typeof(SagaCompletionWatchdogTickEvent));

    await Assert.That(typeInfo).IsNotNull();
    await Assert.That(typeInfo!.Type).IsEqualTo(typeof(SagaCompletionWatchdogTickEvent));
  }

  [Test]
  public async Task SagasJsonContext_ResolvesStronglyTypedMessageEnvelopeAsync() {
    // The transport-consumer side (Service Bus, RabbitMQ) deserializes the wire
    // bytes as MessageEnvelope<SagaCompletionWatchdogTickEvent>. Without an
    // explicit [JsonSerializable] for the concrete generic envelope, STJ throws
    // `JsonTypeInfo metadata for type
    // 'Whizbang.Core.Observability.MessageEnvelope`1[Whizbang.Sagas.SagaCompletionWatchdogTickEvent]'
    // was not provided` (production dev 2026-06-24 second-strike).
    var options = JsonContextRegistry.CreateCombinedOptions();
    var envelopeTypeInfo = options.GetTypeInfo(typeof(Whizbang.Core.Observability.MessageEnvelope<SagaCompletionWatchdogTickEvent>));

    await Assert.That(envelopeTypeInfo).IsNotNull();
    await Assert.That(envelopeTypeInfo!.Type)
      .IsEqualTo(typeof(Whizbang.Core.Observability.MessageEnvelope<SagaCompletionWatchdogTickEvent>));
  }

  [Test]
  public async Task SagasJsonContext_RegistersTypeNameForEnvelopeAsync() {
    // The publisher resolves envelope-type strings (assembly-qualified, wire-side)
    // to concrete generic types via JsonContextRegistry.GetTypeInfoByName. Without
    // an explicit RegisterTypeName for the envelope, the dispatcher throws
    // `Failed to resolve message type ... assembly containing this type is loaded
    // and registered via [ModuleInitializer]` on the first watchdog tick.
    var envelopeTypeName =
      "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Sagas.SagaCompletionWatchdogTickEvent, Whizbang.Sagas]], Whizbang.Core";

    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(envelopeTypeName, options);

    await Assert.That(typeInfo).IsNotNull();
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
