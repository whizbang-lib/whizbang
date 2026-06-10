#pragma warning disable CA1707

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Receive-side rehydrate for body-offload claim envelopes. Pass-through when
/// the envelope isn't a claim, dead-letter on provider-unknown / hash-mismatch
/// / deserialization-failure, rehydrate on success.
/// </summary>
/// <docs>fundamentals/offloads/body-offload-receive</docs>
public class BodyClaimRehydratorTests {

  [Test]
  public async Task MaybeRehydrateAsync_NotAClaim_PassesThroughAsync() {
    var envelope = _buildOriginalEnvelope();
    var sp = _buildProvider(out var _);

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      envelope, "OriginalType, MyAssembly", _buildJsonOptions(), sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsFalse();
    await Assert.That(result.WasRehydrated).IsFalse()
      .Because("Non-claim envelopes MUST pass through untouched — the rehydrator is a no-op for ordinary messages so the receive hot path pays nothing.");
    await Assert.That(result.Envelope).IsSameReferenceAs(envelope);
  }

  [Test]
  public async Task MaybeRehydrateAsync_UnknownProvider_DeadLettersAsync() {
    var claimEnvelope = _buildClaimEnvelope(providerName: "unregistered-provider", originalBody: "hello"u8.ToArray(), out var _);
    var sp = _buildProvider(out var _);   // no store registered under that name

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, _buildJsonOptions(), sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsTrue();
    await Assert.That(result.FailureReason).IsEqualTo(MessageFailureReason.BodyClaimProviderUnknown)
      .Because("Unknown provider MUST dead-letter with the typed reason — silently dropping would lose the message, processing without the body would skip the payload.");
    await Assert.That(result.FailureDescription).IsNotNull();
    await Assert.That(result.FailureDescription!).Contains("AddWhizbang");
  }

  [Test]
  public async Task MaybeRehydrateAsync_HashMismatch_DeadLettersAsync() {
    var sp = _buildProvider(out var store);
    var realBody = "real-body-bytes"u8.ToArray();

    // Build a claim that points at the real upload but advertises a different content hash.
    var realClaim = await store.UploadAsync(realBody, "application/octet-stream");
    var tamperedClaim = realClaim with { ContentHash = "sha256-TAMPERED" };
    var claimEnvelope = _wrapInClaimEnvelope(tamperedClaim, originalTypeName: "OriginalType, MyAssembly");

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, _buildJsonOptions(), sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsTrue();
    await Assert.That(result.FailureReason).IsEqualTo(MessageFailureReason.BodyClaimIntegrityFailure)
      .Because("Hash mismatch indicates storage corruption / MITM / provider bug — receiver MUST refuse to process a body the sender did not write.");
  }

  // Helpers
  // ============================================================

  private static JsonSerializerOptions _buildJsonOptions() {
    // Use the Whizbang infrastructure context which registers
    // MessageEnvelope<BodyClaimEnvelopePayload> + BodyClaimEnvelopePayload.
    return Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
  }

  private static ServiceProvider _buildProvider(out InMemoryStoreImpl _store) {
    var services = new ServiceCollection();
    var instance = new InMemoryStoreImpl("memory");
    _store = instance;
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (sp, key) => instance);
    return services.BuildServiceProvider();
  }

  private static MessageEnvelope<JsonElement> _buildOriginalEnvelope() {
    return new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{\"x\":1}").RootElement,
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
    };
  }

  private static MessageEnvelope<BodyClaimEnvelopePayload> _buildClaimEnvelope(
      string providerName, byte[] originalBody, out MessageBodyClaim claim) {
    var hash = "sha256-" + Convert.ToHexString(SHA256.HashData(originalBody));
    claim = new MessageBodyClaim(
      ProviderName: providerName, StorageKey: $"test://{Guid.NewGuid():N}",
      Size: originalBody.Length, ContentHash: hash,
      ContentType: "application/json", UploadedAt: DateTimeOffset.UtcNow);
    return _wrapInClaimEnvelope(claim, "OriginalType, MyAssembly");
  }

  private static MessageEnvelope<BodyClaimEnvelopePayload> _wrapInClaimEnvelope(MessageBodyClaim claim, string originalTypeName) {
    var sentinel = new BodyClaimEnvelopePayload(claim, "application/json", originalTypeName);
    return new MessageEnvelope<BodyClaimEnvelopePayload> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = sentinel,
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
    };
  }

  /// <summary>Minimal store impl that captures bytes by claim's StorageKey so tests can introspect.</summary>
  internal sealed class InMemoryStoreImpl : IMessageBodyStore {
    private readonly Dictionary<string, byte[]> _bodies = [];
    public InMemoryStoreImpl(string providerName) {
      ProviderName = providerName;
    }
    public string ProviderName { get; }
    public Task<MessageBodyClaim> UploadAsync(
        ReadOnlyMemory<byte> body, string contentType,
        MessageBodyUploadOptions? options = null,
        CancellationToken cancellationToken = default) {
      var key = $"test://{Guid.NewGuid():N}";
      _bodies[key] = body.ToArray();
      var hash = "sha256-" + Convert.ToHexString(SHA256.HashData(body.Span));
      return Task.FromResult(new MessageBodyClaim(ProviderName, key, body.Length, hash, contentType, DateTimeOffset.UtcNow));
    }
    public Task<ReadOnlyMemory<byte>> DownloadAsync(
        MessageBodyClaim claim, MessageBodyDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
          => Task.FromResult<ReadOnlyMemory<byte>>(_bodies[claim.StorageKey]);
    public Task DeleteAsync(
        MessageBodyClaim claim, MessageBodyDeleteOptions? options = null,
        CancellationToken cancellationToken = default) {
      _bodies.Remove(claim.StorageKey);
      return Task.CompletedTask;
    }
  }
}
