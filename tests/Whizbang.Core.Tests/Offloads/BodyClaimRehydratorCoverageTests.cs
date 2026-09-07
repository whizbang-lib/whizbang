using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Coverage for the one <see cref="BodyClaimRehydrator.MaybeRehydrateAsync"/> dead-letter path
/// <see cref="BodyClaimRehydratorTests"/> doesn't reach: a body that deserializes WITHOUT throwing
/// but produces a <c>null</c> reference (the storage holds the literal JSON <c>null</c> for a
/// registered envelope type). Every existing malformed-body test triggers a
/// <see cref="JsonException"/> instead; this is the sibling branch — no exception, just a null
/// result — that must still dead-letter rather than let a null envelope flow downstream into
/// receptors and perspectives as a <see cref="NullReferenceException"/>.
/// </summary>
public class BodyClaimRehydratorCoverageTests {

  private static JsonSerializerOptions _jsonOptions() =>
    Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static ServiceProvider _providerWithStore(out _inMemoryStore store) {
    var services = new ServiceCollection();
    var instance = new _inMemoryStore("memory");
    store = instance;
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (sp, key) => instance);
    return services.BuildServiceProvider();
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

  /// <summary>What breaks: a body that deserializes successfully to <c>null</c> (valid JSON, wrong
  /// shape) must dead-letter with a diagnosable reason — letting it through would hand downstream
  /// receptor/perspective code a null envelope to crash on instead of a clean, routed failure.</summary>
  [Test]
  public async Task MaybeRehydrateAsync_BodyDeserializesToNull_DeadLettersAsSerializationErrorAsync() {
    var sp = _providerWithStore(out var store);
    var nullBody = "null"u8.ToArray();
    var realClaim = await store.UploadAsync(nullBody, "application/json");
    var claimEnvelope = _wrapInClaimEnvelope(realClaim, originalTypeName: typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!);

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, _jsonOptions(), sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsTrue()
      .Because("a body that resolves to null must never be treated as a successful rehydrate");
    await Assert.That(result.FailureReason).IsEqualTo(MessageFailureReason.SerializationError)
      .Because("a null-but-not-throwing deserialize is a wrong-shape body, same family of failure as a JsonException, and must dead-letter the same way");
    await Assert.That(result.FailureDescription!).Contains("null or wrong shape");
  }

  /// <summary>Minimal store impl that captures bytes by claim's StorageKey.</summary>
  private sealed class _inMemoryStore(string providerName) : IMessageBodyStore {
    private readonly Dictionary<string, byte[]> _bodies = [];
    public string ProviderName { get; } = providerName;

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
