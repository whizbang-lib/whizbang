using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Issue #939: a collective event large enough to be body-offloaded could not be rehydrated by a
/// consumer, while the same event type delivered inline was fine. The transport reads an inline body
/// with the full registry (<see cref="JsonContextRegistry.CreateCombinedOptions()"/>); the rehydrator
/// read the downloaded body with the host's registered <see cref="JsonSerializerOptions"/>. A host that
/// registered its generated facade's <c>WhizbangJsonContext.CreateOptions()</c> has a chain of four fixed
/// contexts, and the metadata for the polymorphic <see cref="TenantCollectiveScope"/> lives in the
/// context of the assembly that declares the event, which is not one of them.
/// </summary>
/// <remarks>
/// The event here is declared in this test assembly, whose generated context carries the scope's
/// metadata and is registered with the registry, standing in for a shared contracts assembly. The host
/// options stand in for a consumer whose own generated contexts do not carry the event.
/// </remarks>
[Category("Offloads")]
[Category("JsonSerialization")]
public class BodyClaimRehydratorCrossContextTests {

  /// <summary>A collective event from a contracts assembly. Public so its context carries the scope's metadata.</summary>
  public sealed record OffloadedCollectiveProbeEvent : CollectiveEventBase {
    public string Note { get; init; } = string.Empty;
  }

  [Test]
  public async Task MaybeRehydrateAsync_CollectiveEventWithTenantScope_HostOptionsWithoutContractContext_RehydratesAsync() {
    var (sp, claimEnvelope, _) = await _offloadAsync("tenant-offloaded");

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, _consumerHostOptions(), sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsFalse()
      .Because($"an offloaded body must be readable exactly when its inline form is; got: {result.FailureDescription}");
    var payload = (OffloadedCollectiveProbeEvent)result.Envelope!.Payload!;
    await Assert.That(payload.Scope).IsTypeOf<TenantCollectiveScope>();
    await Assert.That(((TenantCollectiveScope)payload.Scope).TenantId).IsEqualTo("tenant-offloaded");
  }

  [Test]
  public async Task OffloadedAndInline_SamePayload_StoreAndDispatchIdenticallyAsync() {
    var (sp, claimEnvelope, body) = await _offloadAsync("tenant-parity");
    var host = _consumerHostOptions();

    // Inline: the transport reads the wire bytes with its own options (the full registry).
    var inlineTypeInfo = JsonContextRegistry.GetTypeInfoByName(_envelopeTypeName(), JsonContextRegistry.CreateCombinedOptions())!;
    var inline = (IMessageEnvelope<OffloadedCollectiveProbeEvent>)JsonSerializer.Deserialize(body, inlineTypeInfo)!;
    // Offloaded: the rehydrator reads the same bytes after download.
    var rehydrated = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, host, sp, CancellationToken.None);
    var offloaded = (IMessageEnvelope<OffloadedCollectiveProbeEvent>)rehydrated.Envelope!;

    // Both become an inbox row through the host's serializer, then are read at dispatch.
    var serializer = new EnvelopeSerializer(host);
    var inlineRow = serializer.SerializeEnvelope(inline);
    var offloadedRow = serializer.SerializeEnvelope(offloaded);
    await Assert.That(offloadedRow.JsonEnvelope.Payload.GetRawText()).IsEqualTo(inlineRow.JsonEnvelope.Payload.GetRawText())
      .Because("the stored form of a message must not depend on whether its body travelled inline or offloaded");
    await Assert.That(offloadedRow.MessageType).IsEqualTo(inlineRow.MessageType);

    var deserializer = new JsonLifecycleMessageDeserializer(host);
    var inlineDispatched = (OffloadedCollectiveProbeEvent)deserializer.DeserializeFromJsonElement(inlineRow.JsonEnvelope.Payload, inlineRow.MessageType);
    var offloadedDispatched = (OffloadedCollectiveProbeEvent)deserializer.DeserializeFromJsonElement(offloadedRow.JsonEnvelope.Payload, offloadedRow.MessageType);
    await Assert.That(offloadedDispatched).IsEqualTo(inlineDispatched);
    await Assert.That(((TenantCollectiveScope)offloadedDispatched.Scope).TenantId).IsEqualTo("tenant-parity");
  }

  // ------------------------------------------------------------------------------------------

  /// <summary>
  /// A consumer's generated facade options, for a consumer whose own contexts do not carry the event:
  /// the framework's identifier and infrastructure contexts only, no registry fallback.
  /// </summary>
  private static JsonSerializerOptions _consumerHostOptions() => new() {
    TypeInfoResolver = JsonTypeInfoResolver.Combine(
      Whizbang.Core.Generated.WhizbangIdJsonContext.Default,
      Whizbang.Core.Generated.InfrastructureJsonContext.Default),
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };

  private static string _envelopeTypeName() =>
    EnvelopeTypeNameHelper.Format(TypeNameFormatter.Format(typeof(OffloadedCollectiveProbeEvent)));

  private static async Task<(ServiceProvider Provider, MessageEnvelope<BodyClaimEnvelopePayload> Claim, byte[] Body)> _offloadAsync(string tenantId) {
    var store = new BodyClaimRehydratorTests.InMemoryStoreImpl("memory");
    var sp = new ServiceCollection()
      .AddKeyedSingleton<IMessageBodyStore>("memory", (_, _) => store)
      .BuildServiceProvider();

    // The publisher serializes with the full registry, as its outbox does.
    var original = new MessageEnvelope<OffloadedCollectiveProbeEvent> {
      MessageId = MessageId.New(),
      Payload = new OffloadedCollectiveProbeEvent {
        StreamId = Guid.CreateVersion7(),
        Scope = new TenantCollectiveScope(tenantId),
        Note = new string('x', 256),
      },
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var publisher = JsonContextRegistry.CreateCombinedOptions();
    var body = JsonSerializer.SerializeToUtf8Bytes(original, publisher.GetTypeInfo(typeof(MessageEnvelope<OffloadedCollectiveProbeEvent>)));
    var claim = await store.UploadAsync(body, "application/json");
    var claimEnvelope = new MessageEnvelope<BodyClaimEnvelopePayload> {
      MessageId = original.MessageId,
      Payload = new BodyClaimEnvelopePayload(claim, "application/json", _envelopeTypeName()),
      Hops = original.Hops,
      DispatchContext = original.DispatchContext,
    };
    return (sp, claimEnvelope, body);
  }
}
