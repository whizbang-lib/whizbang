#pragma warning disable CA1707

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Offloaded bodies reach the body store sealed (issue #704). The encryption hook slot at 2000
/// runs after the upload, so it protected the claim envelope and never the body: the largest
/// messages a system produces were written in the clear to the component with the longest
/// retention and the widest access. Sealing now happens inside the offload path, before the
/// upload call is made; the claim hash covers the stored bytes so the receiver verifies before it
/// decrypts; a claim without a descriptor downloads exactly as before.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Offloads/BodyOffloadPostSerializeHook.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Offloads/BodyClaimRehydrator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Offloads/OffloadServiceCollectionExtensions.cs</code-under-test>
/// <docs>fundamentals/offloads/message-body-store</docs>
public class BodyOffloadCipherTests {
  private static readonly byte[] _kek = RandomNumberGenerator.GetBytes(32);

  [Test]
  public async Task Hook_WithACipher_UploadsSealedBytes_NeverThePlaintextAsync() {
    var (hook, store) = _buildHook(o => { o.ProviderName = "memory"; o.CipherName = "kv"; o.SizeThresholdBytes = 100; });
    var body = Encoding.UTF8.GetBytes("{\"caseId\":\"CASE-88213\",\"claimant\":{\"name\":\"Dana\"}}".PadRight(5_000, ' '));

    var result = await hook.RunAsync(_buildContext(body), CancellationToken.None);

    await Assert.That(store.UploadCount).IsEqualTo(1);
    await Assert.That(store.LastUploadedBody!.AsSpan().IndexOf("CASE-88213"u8)).IsEqualTo(-1)
      .Because("the upload call receives sealed bytes; the store cannot receive plaintext because sealing precedes the upload");
    await Assert.That(store.LastUploadedBody!.Length).IsEqualTo(body.Length + 16);
    var claim = ((MessageEnvelope<BodyClaimEnvelopePayload>)result.NewEnvelope!).Payload.Claim;
    await Assert.That(claim.Cipher).IsNotNull();
    await Assert.That(claim.Cipher!.CipherName).IsEqualTo("kv");
    await Assert.That(claim.ContentHash).IsEqualTo("sha256-" + Convert.ToHexString(SHA256.HashData(store.LastUploadedBody!)))
      .Because("the hash covers the STORED bytes, so the receiver verifies before it decrypts");
  }

  [Test]
  public async Task Hook_WithoutACipher_UploadsThePlaintextAndNoDescriptor_AsBeforeAsync() {
    var (hook, store) = _buildHook(o => { o.ProviderName = "memory"; o.SizeThresholdBytes = 100; });
    var body = new byte[5_000];

    var result = await hook.RunAsync(_buildContext(body), CancellationToken.None);

    await Assert.That(store.LastUploadedBody).IsEquivalentTo(body);
    var claim = ((MessageEnvelope<BodyClaimEnvelopePayload>)result.NewEnvelope!).Payload.Claim;
    await Assert.That(claim.Cipher).IsNull().Because("no cipher configured is today's behavior, unchanged");
  }

  [Test]
  public async Task Hook_CipherNamedButNotRegistered_FailsLoudlyBeforeUploadingAsync() {
    var (hook, store) = _buildHook(o => { o.ProviderName = "memory"; o.CipherName = "missing"; o.SizeThresholdBytes = 100; }, registerCipher: false);

    var ex = await Assert.That(async () => await hook.RunAsync(_buildContext(new byte[5_000]), CancellationToken.None))
      .Throws<InvalidOperationException>();

    await Assert.That(ex!.Message).Contains("missing");
    await Assert.That(store.UploadCount).IsEqualTo(0)
      .Because("a misconfigured cipher must never fall back to uploading plaintext");
  }

  [Test]
  public async Task Rehydrate_SealedBody_VerifiesTheHashThenOpens_AndReturnsTheOriginalAsync() {
    var jsonOptions = _jsonOptions();
    var original = _originalEnvelope();
    var body = JsonSerializer.SerializeToUtf8Bytes(original, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
    var (sp, store, cipher) = _buildReceiver();
    var sealedBody = await cipher.SealAsync(body);
    var claim = (await store.UploadAsync(sealedBody.Bytes, "application/json")) with { Cipher = sealedBody.Descriptor };
    var claimEnvelope = _wrapInClaimEnvelope(claim, typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!);

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, jsonOptions, sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsFalse();
    var rehydrated = (MessageEnvelope<JsonElement>)result.Envelope!;
    await Assert.That(rehydrated.MessageId).IsEqualTo(original.MessageId);
    await Assert.That(rehydrated.Payload.GetProperty("x").GetInt32()).IsEqualTo(1)
      .Because("receptors see the message the sender wrote; only the store's view changed");
  }

  [Test]
  public async Task Rehydrate_TamperedStoredBytes_DeadLettersOnTheHash_BeforeTheCipherRunsAsync() {
    var jsonOptions = _jsonOptions();
    var (sp, store, cipher) = _buildReceiver();
    var sealedBody = await cipher.SealAsync("{\"x\":1}"u8.ToArray());
    var claim = (await store.UploadAsync(sealedBody.Bytes, "application/json")) with { Cipher = sealedBody.Descriptor };
    store.Corrupt(claim.StorageKey);
    var claimEnvelope = _wrapInClaimEnvelope(claim, typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!);

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, jsonOptions, sp, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsTrue();
    await Assert.That(result.FailureReason).IsEqualTo(MessageFailureReason.BodyClaimIntegrityFailure);
    await Assert.That(cipher.OpenCalls).IsEqualTo(0)
      .Because("unverified bytes are never fed to a cipher: hash first, then open");
  }

  [Test]
  public async Task Rehydrate_CipherNotRegisteredOnTheReceiver_DeadLettersAsCipherUnknownAsync() {
    var jsonOptions = _jsonOptions();
    var (_, store, cipher) = _buildReceiver();
    var sealedBody = await cipher.SealAsync("{\"x\":1}"u8.ToArray());
    var claim = (await store.UploadAsync(sealedBody.Bytes, "application/json")) with { Cipher = sealedBody.Descriptor };
    var services = new ServiceCollection();
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (_, _) => store);
    var receiverWithoutCipher = services.BuildServiceProvider();
    var claimEnvelope = _wrapInClaimEnvelope(claim, typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!);

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, jsonOptions, receiverWithoutCipher, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsTrue();
    await Assert.That(result.FailureReason).IsEqualTo(MessageFailureReason.BodyClaimCipherUnknown);
    await Assert.That(result.FailureDescription!).Contains("kv");
  }

  [Test]
  public async Task Rehydrate_WrongKeyOnTheReceiver_DeadLettersAsIntegrityFailure_NamingTheKeyAsync() {
    var jsonOptions = _jsonOptions();
    var (_, store, senderCipher) = _buildReceiver();
    var sealedBody = await senderCipher.SealAsync("{\"x\":1}"u8.ToArray());
    var claim = (await store.UploadAsync(sealedBody.Bytes, "application/json")) with { Cipher = sealedBody.Descriptor };
    var services = new ServiceCollection();
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (_, _) => store);
    services.AddWhizbangAesGcmBodyCipher("kv", "kek-1", RandomNumberGenerator.GetBytes(32));
    var receiverWithWrongKey = services.BuildServiceProvider();
    var claimEnvelope = _wrapInClaimEnvelope(claim, typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!);

    var result = await BodyClaimRehydrator.MaybeRehydrateAsync(
      claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName, jsonOptions, receiverWithWrongKey, CancellationToken.None);

    await Assert.That(result.IsDeadLetter).IsTrue();
    await Assert.That(result.FailureReason).IsEqualTo(MessageFailureReason.BodyClaimIntegrityFailure);
    await Assert.That(result.FailureDescription!).Contains("kek-1");
  }

  [Test]
  public async Task Claim_WithADescriptor_RoundTripsThroughTheInfrastructureJsonContextAsync() {
    var jsonOptions = _jsonOptions();
    var descriptor = new MessageBodyCipherDescriptor("kv", AesGcmEnvelopeCipher.ALGORITHM, "kek-1", RandomNumberGenerator.GetBytes(12), RandomNumberGenerator.GetBytes(60));
    var claim = new MessageBodyClaim("memory", "test://k", 10, "sha256-AB", "application/json", DateTimeOffset.UtcNow, descriptor);
    var typeInfo = (JsonTypeInfo<MessageBodyClaim>)jsonOptions.GetTypeInfo(typeof(MessageBodyClaim));

    var json = JsonSerializer.Serialize(claim, typeInfo);
    var back = JsonSerializer.Deserialize(json, typeInfo)!;

    await Assert.That(back.Cipher).IsNotNull();
    await Assert.That(back.Cipher!.CipherName).IsEqualTo("kv");
    await Assert.That(back.Cipher.KeyId).IsEqualTo("kek-1");
    await Assert.That(back.Cipher.Nonce.ToArray()).IsEquivalentTo(descriptor.Nonce.ToArray());
    await Assert.That(back.Cipher.WrappedKey.ToArray()).IsEquivalentTo(descriptor.WrappedKey.ToArray());
    await Assert.That(json).DoesNotContain(Convert.ToBase64String(_kek)).Because("the claim never carries a key encryption key");
  }

  [Test]
  public async Task Claim_WithoutADescriptor_DeserializesFromTheOldShapeAsync() {
    var jsonOptions = _jsonOptions();
    var typeInfo = (JsonTypeInfo<MessageBodyClaim>)jsonOptions.GetTypeInfo(typeof(MessageBodyClaim));
    const string oldShape = "{\"ProviderName\":\"memory\",\"StorageKey\":\"k\",\"Size\":3,\"ContentHash\":\"sha256-AB\",\"ContentType\":\"application/json\",\"UploadedAt\":\"2026-01-01T00:00:00+00:00\"}";

    var claim = JsonSerializer.Deserialize(oldShape, typeInfo)!;

    await Assert.That(claim.Cipher).IsNull().Because("existing claims in flight keep working across the upgrade");
  }

  [Test]
  public async Task AddWhizbangMessageBodyCipher_ResolvesByNameAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IMessageBodyKeyWrapper>(new LocalAesKeyWrapper("kek-1", _kek));
    services.AddWhizbangMessageBodyCipher<_namedCipher>("custom");
    var sp = services.BuildServiceProvider();

    var cipher = sp.GetKeyedService<IMessageBodyCipher>("custom");

    await Assert.That(cipher).IsNotNull();
    await Assert.That(cipher).IsTypeOf<_namedCipher>();
    await Assert.That(sp.GetKeyedService<IMessageBodyCipher>("other")).IsNull();
  }

  [Test]
  public async Task AddWhizbangAesGcmBodyCipher_ResolvesByName_WithTheContainerBuiltWrapperAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IMessageBodyKeyWrapper>(new LocalAesKeyWrapper("kek-vault", _kek));
    services.AddWhizbangAesGcmBodyCipher("kv", sp => sp.GetRequiredService<IMessageBodyKeyWrapper>());
    var sp = services.BuildServiceProvider();

    var cipher = sp.GetKeyedService<IMessageBodyCipher>("kv");

    await Assert.That(cipher).IsNotNull();
    await Assert.That(cipher!.CipherName).IsEqualTo("kv");
    var sealedBody = await cipher.SealAsync("x"u8.ToArray());
    await Assert.That(sealedBody.Descriptor.KeyId).IsEqualTo("kek-vault");
  }

  [Test]
  public async Task AddWhizbangAesGcmBodyCipher_RejectsBlankNamesAsync() {
    var services = new ServiceCollection();

    await Assert.That(() => services.AddWhizbangAesGcmBodyCipher("", "k", _kek)).Throws<ArgumentException>();
    await Assert.That(() => services.AddWhizbangMessageBodyCipher<_namedCipher>(" ")).Throws<ArgumentException>();
    await Assert.That(() => services.AddWhizbangAesGcmBodyCipher("kv", null!)).Throws<ArgumentNullException>();
  }

  // -------------------------------------------------------------------------------------------

  private static (BodyOffloadPostSerializeHook Hook, _hashingStore Store) _buildHook(Action<MessageBodyOffloadOptions> configure, bool registerCipher = true) {
    var services = new ServiceCollection();
    var store = new _hashingStore("memory");
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (_, _) => store);
    if (registerCipher) {
      services.AddWhizbangAesGcmBodyCipher("kv", "kek-1", _kek);
    }
    services.AddOptions<MessageBodyOffloadOptions>().Configure(configure);
    var sp = services.BuildServiceProvider();
    return (new BodyOffloadPostSerializeHook(sp, sp.GetRequiredService<IOptionsMonitor<MessageBodyOffloadOptions>>()), store);
  }

  private static (ServiceProvider Provider, _hashingStore Store, _countingCipher Cipher) _buildReceiver() {
    var services = new ServiceCollection();
    var store = new _hashingStore("memory");
    var cipher = new _countingCipher(new AesGcmEnvelopeCipher("kv", new LocalAesKeyWrapper("kek-1", _kek)));
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (_, _) => store);
    services.AddKeyedSingleton<IMessageBodyCipher>("kv", (_, _) => cipher);
    return (services.BuildServiceProvider(), store, cipher);
  }

  private static JsonSerializerOptions _jsonOptions() => Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static PostSerializeContext _buildContext(byte[] bytes) {
    var envelope = new MessageEnvelope<_payload> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = new _payload("x"),
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
    };
    return new PostSerializeContext(
      Envelope: envelope,
      EnvelopeType: envelope.GetType().AssemblyQualifiedName!,
      SerializedBytes: bytes,
      ContentType: "application/json",
      TransportMaxMessageSizeBytes: null,
      JsonOptions: new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() },
      Destination: new TransportDestination("test"));
  }

  private static MessageEnvelope<JsonElement> _originalEnvelope() => new() {
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    MessageId = MessageId.New(),
    Payload = JsonDocument.Parse("{\"x\":1}").RootElement,
    Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
  };

  private static MessageEnvelope<BodyClaimEnvelopePayload> _wrapInClaimEnvelope(MessageBodyClaim claim, string originalTypeName) => new() {
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    MessageId = MessageId.New(),
    Payload = new BodyClaimEnvelopePayload(claim, "application/json", originalTypeName),
    Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
  };

  private sealed record _payload(string Content);

  private sealed class _namedCipher(IMessageBodyKeyWrapper wrapper) : IMessageBodyCipher {
    private readonly AesGcmEnvelopeCipher _inner = new("custom", wrapper);
    public string CipherName => "custom";
    public ValueTask<SealedBody> SealAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) => _inner.SealAsync(body, cancellationToken);
    public ValueTask<ReadOnlyMemory<byte>> OpenAsync(ReadOnlyMemory<byte> sealedBody, MessageBodyCipherDescriptor descriptor, CancellationToken cancellationToken = default) => _inner.OpenAsync(sealedBody, descriptor, cancellationToken);
  }

  private sealed class _countingCipher(IMessageBodyCipher inner) : IMessageBodyCipher {
    public int OpenCalls;
    public string CipherName => inner.CipherName;
    public ValueTask<SealedBody> SealAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) => inner.SealAsync(body, cancellationToken);
    public ValueTask<ReadOnlyMemory<byte>> OpenAsync(ReadOnlyMemory<byte> sealedBody, MessageBodyCipherDescriptor descriptor, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref OpenCalls);
      return inner.OpenAsync(sealedBody, descriptor, cancellationToken);
    }
  }

  /// <summary>A store that hashes exactly what it is given, like the real providers.</summary>
  private sealed class _hashingStore(string providerName) : IMessageBodyStore {
    private readonly Dictionary<string, byte[]> _bodies = [];
    public string ProviderName { get; } = providerName;
    public int UploadCount { get; private set; }
    public byte[]? LastUploadedBody { get; private set; }

    public Task<MessageBodyClaim> UploadAsync(ReadOnlyMemory<byte> body, string contentType, MessageBodyUploadOptions? options = null, CancellationToken cancellationToken = default) {
      var key = $"test://{Guid.NewGuid():N}";
      var copy = body.ToArray();
      _bodies[key] = copy;
      UploadCount++;
      LastUploadedBody = copy;
      var hash = "sha256-" + Convert.ToHexString(SHA256.HashData(copy));
      return Task.FromResult(new MessageBodyClaim(ProviderName, key, copy.Length, hash, contentType, DateTimeOffset.UtcNow));
    }

    public Task<ReadOnlyMemory<byte>> DownloadAsync(MessageBodyClaim claim, MessageBodyDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
      Task.FromResult<ReadOnlyMemory<byte>>(_bodies[claim.StorageKey]);

    public Task DeleteAsync(MessageBodyClaim claim, MessageBodyDeleteOptions? options = null, CancellationToken cancellationToken = default) {
      _bodies.Remove(claim.StorageKey);
      return Task.CompletedTask;
    }

    public void Corrupt(string storageKey) => _bodies[storageKey][0] ^= 0x01;
  }
}
