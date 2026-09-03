#pragma warning disable CA1707

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Tests.Observability;
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

  [Test]
  public async Task MaybeRehydrateAsync_ActiveCleanupDisabled_PendingCleanupClaimIsNullAsync() {
    var sp = _buildProviderWithOptions(out var store, activeCleanup: false);
    var (claim, originalTypeName) = await _uploadSerializedEnvelopeAsync(store, _buildJsonOptions());
    var claimEnvelope = _wrapInClaimEnvelope(claim, originalTypeName);

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, _buildJsonOptions(), sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsFalse();
    await Assert.That(result.PendingCleanupClaim).IsNull()
      .Because("Default ActiveCleanup=false → rely on the provider's storage-level TTL; the rehydrator must not surface a cleanup claim.");
  }

  [Test]
  public async Task MaybeRehydrateAsync_ActiveCleanupEnabled_SurfacesCleanupClaimAsync() {
    var sp = _buildProviderWithOptions(out var store, activeCleanup: true);
    var (claim, originalTypeName) = await _uploadSerializedEnvelopeAsync(store, _buildJsonOptions());
    var claimEnvelope = _wrapInClaimEnvelope(claim, originalTypeName);

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, _buildJsonOptions(), sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsFalse();
    await Assert.That(result.PendingCleanupClaim).IsNotNull()
      .Because("ActiveCleanup=true → rehydrator surfaces the claim so the receive-side worker can delete the body POST-COMMIT.");
    await Assert.That(result.PendingCleanupClaim!.StorageKey).IsEqualTo(claim.StorageKey);
    await Assert.That(result.PendingCleanupClaim.ProviderName).IsEqualTo(claim.ProviderName);
  }

  [Test]
  public async Task MaybeRehydrateAsync_DownloadThrows_ThrowsForRetryNotDeadLetterAsync() {
    // A download failure is very likely TRANSIENT (network blip, momentary store outage — the body
    // is in the store). The rehydrator must THROW (BodyClaimDownloadException) so the transport
    // redelivers the message for a bounded, automatic retry, rather than terminally dead-lettering a
    // body that is almost certainly there on the next attempt. (Permanent failures — integrity /
    // deserialize / unknown-provider — still dead-letter, asserted by the other tests.)
    var services = new ServiceCollection();
    var store = new ThrowingStore("memory");
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (sp, key) => store);
    var sp = services.BuildServiceProvider();
    var claim = new MessageBodyClaim(
      ProviderName: "memory", StorageKey: "test://does-not-exist",
      Size: 4, ContentHash: "sha256-AB",
      ContentType: "application/json", UploadedAt: DateTimeOffset.UtcNow);
    var claimEnvelope = _wrapInClaimEnvelope(claim, "OriginalType, MyAssembly");

    var ex = await Assert.That(async () => await BodyClaimRehydrator.MaybeRehydrateAsync(
        claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, _buildJsonOptions(), sp, CancellationToken.None))
      .Throws<BodyClaimDownloadException>()
      .Because("A transient download failure MUST throw for transport-level redelivery/retry — not terminally dead-letter.");
    await Assert.That(ex!.Message).Contains("memory");
    await Assert.That(ex.InnerException).IsTypeOf<InvalidOperationException>()
      .Because("The original provider exception must be preserved as the inner exception for diagnostics.");
  }

  [Test, Timeout(30_000)]
  public async Task MaybeRehydrateAsync_DownloadExceedsTimeout_ThrowsForRetryAsync(CancellationToken testCt) {
    // A hung body-store download must be aborted at DownloadTimeout and surfaced as a retryable throw —
    // never stall the consumer indefinitely on a blob call that never returns.
    var services = new ServiceCollection();
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (sp, key) => new HangingStore("memory"));
    services.AddSingleton<IOptions<MessageBodyOffloadOptions>>(
      Options.Create(new MessageBodyOffloadOptions { DownloadTimeout = TimeSpan.FromMilliseconds(100) }));
    var sp = services.BuildServiceProvider();
    var claim = new MessageBodyClaim(
      ProviderName: "memory", StorageKey: "test://hangs",
      Size: 4, ContentHash: "sha256-AB",
      ContentType: "application/json", UploadedAt: DateTimeOffset.UtcNow);
    var claimEnvelope = _wrapInClaimEnvelope(claim, "OriginalType, MyAssembly");

    var ex = await Assert.That(async () => await BodyClaimRehydrator.MaybeRehydrateAsync(
        claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, _buildJsonOptions(), sp, testCt))
      .Throws<BodyClaimDownloadException>()
      .Because("A download exceeding DownloadTimeout MUST abort and throw for retry, not hang the consumer forever.");
    await Assert.That(ex!.Message).Contains("timeout");
  }

  [Test]
  public async Task MaybeRehydrateAsync_DownloadCanceled_PropagatesOperationCanceledAsync() {
    // When the RECEIVER's own CancellationToken is canceled (host shutdown), the rehydrator must
    // propagate the OperationCanceledException untouched so the consumer stops cleanly — it must NOT
    // be masked as a retryable download failure (that path is only for timeouts / store errors).
    var services = new ServiceCollection();
    var store = new CancelingStore("memory");
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (sp, key) => store);
    var sp = services.BuildServiceProvider();
    var claim = new MessageBodyClaim(
      ProviderName: "memory", StorageKey: "test://cancel",
      Size: 0, ContentHash: "sha256-XX",
      ContentType: "application/json", UploadedAt: DateTimeOffset.UtcNow);
    var claimEnvelope = _wrapInClaimEnvelope(claim, "OriginalType, MyAssembly");

    using var shutdownCts = new CancellationTokenSource();
    await shutdownCts.CancelAsync();

    await Assert.That(async () => await BodyClaimRehydrator.MaybeRehydrateAsync(
        claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName,
        _buildJsonOptions(), sp, shutdownCts.Token))
      .Throws<OperationCanceledException>()
      .Because("A canceled receiver CT (host shutdown) propagates untouched — distinct from the retryable download-failure path.");
  }

  [Test]
  public async Task MaybeRehydrateAsync_UnknownOriginalTypeName_DeadLettersAsSerializationErrorAsync() {
    // The original type name claims a type that has no JsonTypeInfo registered.
    var sp = _buildProvider(out var store);
    var realBody = "some-bytes"u8.ToArray();
    var realClaim = await store.UploadAsync(realBody, "application/json");
    var claimEnvelope = _wrapInClaimEnvelope(realClaim, originalTypeName: "Some.Unknown.NotRegisteredType, MissingAssembly");

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, _buildJsonOptions(), sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsTrue();
    await Assert.That(result.FailureReason).IsEqualTo(MessageFailureReason.SerializationError)
      .Because("Missing JsonTypeInfo is a config gap — dead-letter with a clear message pointing at the registration so ops can diagnose.");
    await Assert.That(result.FailureDescription!).Contains("Some.Unknown.NotRegisteredType");
  }

  [Test]
  public async Task MaybeRehydrateAsync_MalformedJsonBody_DeadLettersAsSerializationErrorAsync() {
    // Body in storage is not valid JSON for the declared type — JsonException must be
    // caught and translated to a dead-letter result, not bubbled to the worker.
    var sp = _buildProvider(out var store);
    var garbage = "}}}not-json{{{"u8.ToArray();
    var realClaim = await store.UploadAsync(garbage, "application/json");
    var claimEnvelope = _wrapInClaimEnvelope(realClaim, originalTypeName: typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!);

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, _buildJsonOptions(), sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsTrue();
    await Assert.That(result.FailureReason).IsEqualTo(MessageFailureReason.SerializationError)
      .Because("Storage corruption / wrong-type claims must dead-letter with SerializationError — the worker must never bubble JsonException to its outer scope.");
    await Assert.That(result.FailureDescription!).Contains("Failed to deserialize");
  }

  // Helpers
  // ============================================================

  /// <summary>
  /// Serializes a real MessageEnvelope<JsonElement> and uploads it to the
  /// store. Returns the claim + the assembly-qualified type name the rehydrator
  /// will use to deserialize.
  /// </summary>
  private static async Task<(MessageBodyClaim Claim, string OriginalTypeName)> _uploadSerializedEnvelopeAsync(
      InMemoryStoreImpl store, JsonSerializerOptions jsonOptions) {
    var originalEnvelope = new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{\"x\":1}").RootElement,
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
    };
    var typeInfo = jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var json = JsonSerializer.Serialize(originalEnvelope, typeInfo);
    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
    var claim = await store.UploadAsync(bytes, "application/json");
    return (claim, typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!);
  }

  [Test]
  public async Task MaybeRehydrateAsync_Success_EmitsRehydratedMetricsWithBoundedTagsAsync() {
    // Isolated meter via TestMeterFactory — parallel-safe metric capture.
    using var factory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(factory));
    var sp = _buildProviderWithOptions(out var store, activeCleanup: false, metrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var (claim, originalTypeName) = await _uploadSerializedEnvelopeAsync(store, _buildJsonOptions());
    var claimEnvelope = _wrapInClaimEnvelope(claim, originalTypeName);

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, _buildJsonOptions(), sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsFalse();

    var counts = helper.GetByName("whizbang.transport.body_claim.rehydrated.count");
    await Assert.That(counts.Count).IsEqualTo(1);
    await Assert.That(counts[0].Value).IsEqualTo(1d);

    var bytes = helper.GetByName("whizbang.transport.body_claim.rehydrated.bytes");
    await Assert.That(bytes.Count).IsEqualTo(1);
    await Assert.That(bytes[0].Value).IsGreaterThan(0d)
      .Because("Records the downloaded body size on the receive side.");

    await Assert.That(counts[0].Tags.ContainsKey("message.type")).IsTrue();
    await Assert.That(counts[0].Tags.ContainsKey("message.namespace")).IsTrue();
  }

  private static ServiceProvider _buildProviderWithOptions(out InMemoryStoreImpl store, bool activeCleanup, TransportMetrics? metrics = null) {
    var services = new ServiceCollection();
    var instance = new InMemoryStoreImpl("memory");
    store = instance;
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (sp, key) => instance);
    services.AddOptions<MessageBodyOffloadOptions>().Configure(o => o.ActiveCleanup = activeCleanup);
    // Only register metrics when a test supplies an isolated instance (parallel-safe metric capture).
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    return services.BuildServiceProvider();
  }

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

  /// <summary>Store whose DownloadAsync always throws a non-CT exception — exercises the rehydrator's TransportException dead-letter path.</summary>
  private sealed class ThrowingStore : IMessageBodyStore {
    public ThrowingStore(string providerName) { ProviderName = providerName; }
    public string ProviderName { get; }
    public Task<MessageBodyClaim> UploadAsync(ReadOnlyMemory<byte> body, string contentType, MessageBodyUploadOptions? options = null, CancellationToken cancellationToken = default)
      => Task.FromResult(new MessageBodyClaim(ProviderName, "k", body.Length, "sha256-X", contentType, DateTimeOffset.UtcNow));
    public Task<ReadOnlyMemory<byte>> DownloadAsync(MessageBodyClaim claim, MessageBodyDownloadOptions? options = null, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("simulated provider failure");
    public Task DeleteAsync(MessageBodyClaim claim, MessageBodyDeleteOptions? options = null, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  /// <summary>Store whose DownloadAsync throws OperationCanceledException — exercises the rehydrator's cancellation-propagation behavior.</summary>
  private sealed class CancelingStore : IMessageBodyStore {
    public CancelingStore(string providerName) { ProviderName = providerName; }
    public string ProviderName { get; }
    public Task<MessageBodyClaim> UploadAsync(ReadOnlyMemory<byte> body, string contentType, MessageBodyUploadOptions? options = null, CancellationToken cancellationToken = default)
      => Task.FromResult(new MessageBodyClaim(ProviderName, "k", body.Length, "sha256-X", contentType, DateTimeOffset.UtcNow));
    public Task<ReadOnlyMemory<byte>> DownloadAsync(MessageBodyClaim claim, MessageBodyDownloadOptions? options = null, CancellationToken cancellationToken = default)
      => throw new OperationCanceledException();
    public Task DeleteAsync(MessageBodyClaim claim, MessageBodyDeleteOptions? options = null, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  /// <summary>Store whose DownloadAsync never completes until canceled — exercises the rehydrator's bounded download timeout.</summary>
  private sealed class HangingStore : IMessageBodyStore {
    public HangingStore(string providerName) { ProviderName = providerName; }
    public string ProviderName { get; }
    public Task<MessageBodyClaim> UploadAsync(ReadOnlyMemory<byte> body, string contentType, MessageBodyUploadOptions? options = null, CancellationToken cancellationToken = default)
      => Task.FromResult(new MessageBodyClaim(ProviderName, "k", body.Length, "sha256-X", contentType, DateTimeOffset.UtcNow));
    public async Task<ReadOnlyMemory<byte>> DownloadAsync(MessageBodyClaim claim, MessageBodyDownloadOptions? options = null, CancellationToken cancellationToken = default) {
      await Task.Delay(Timeout.Infinite, cancellationToken);  // returns only when the (linked timeout) token cancels
      return ReadOnlyMemory<byte>.Empty;                      // unreachable — Task.Delay throws on cancellation
    }
    public Task DeleteAsync(MessageBodyClaim claim, MessageBodyDeleteOptions? options = null, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }
}
