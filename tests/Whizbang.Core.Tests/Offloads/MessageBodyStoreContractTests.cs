#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Locks the Whizbang.Core.Offloads contract surface — the abstractions
/// the body-offload (claim-check) feature builds on. These types live in
/// Whizbang.Core so concrete provider projects (Whizbang.Offloads.InMemory,
/// Whizbang.Offloads.AzureBlob, etc.) can depend on them without pulling
/// Whizbang.Core into provider-specific transitive dependency graphs.
///
/// What gets locked here:
///   - <see cref="MessageBodyClaim"/> record shape: every field load-bearing
///     (StorageKey + ContentHash drive integrity check on download; Size +
///     ContentType drive receiver pre-allocate + deserialization)
///   - Optional-options records: providers MUST be able to honor null
///     options without bespoke null-check code at every call site
///   - <see cref="MessageBodyDeleteOptions.IgnoreMissing"/> defaults true
///     because the production cleanup path should be idempotent
///   - <see cref="IMessageBodyStore"/> contract: a non-abstract impl
///     compiles, ProviderName surfaces, and the three methods reach
///     their override
/// </summary>
/// <docs>fundamentals/offloads/message-body-store</docs>
public class MessageBodyStoreContractTests {

  // ============================================================
  // MessageBodyClaim — record shape + equality
  // ============================================================

  [Test]
  public async Task MessageBodyClaim_Equals_WithSameValues_AreEqualAsync() {
    var uploadedAt = DateTimeOffset.UtcNow;
    var a = new MessageBodyClaim(
      ProviderName: "in-memory",
      StorageKey: "blob://test/abc.bin",
      Size: 4096,
      ContentHash: "sha256-DEADBEEF",
      ContentType: "application/json",
      UploadedAt: uploadedAt);
    var b = new MessageBodyClaim(
      ProviderName: "in-memory",
      StorageKey: "blob://test/abc.bin",
      Size: 4096,
      ContentHash: "sha256-DEADBEEF",
      ContentType: "application/json",
      UploadedAt: uploadedAt);

    await Assert.That(a).IsEqualTo(b);
  }

  [Test]
  public async Task MessageBodyClaim_Equals_WithDifferentContentHash_AreNotEqualAsync() {
    var uploadedAt = DateTimeOffset.UtcNow;
    var a = new MessageBodyClaim("in-memory", "blob://test/abc.bin", 4096, "sha256-A", "application/json", uploadedAt);
    var b = new MessageBodyClaim("in-memory", "blob://test/abc.bin", 4096, "sha256-B", "application/json", uploadedAt);

    await Assert.That(a).IsNotEqualTo(b)
      .Because("ContentHash divergence means the body changed — receiver MUST reject; equality must distinguish.");
  }

  // ============================================================
  // MessageBodyUploadOptions — defaults + null-tolerance
  // ============================================================

  [Test]
  public async Task MessageBodyUploadOptions_Default_AllPropertiesNullAsync() {
    var options = new MessageBodyUploadOptions();

    await Assert.That(options.Metadata).IsNull();
    await Assert.That(options.Ttl).IsNull();
    await Assert.That(options.ContainerOverride).IsNull();
    await Assert.That(options.ProviderHints).IsNull();
  }

  [Test]
  public async Task MessageBodyUploadOptions_WithMetadata_PreservesItAsync() {
    var meta = new Dictionary<string, string> {
      ["correlation_id"] = "corr-123",
      ["source_service"] = "JobService"
    };

    var options = new MessageBodyUploadOptions { Metadata = meta };

    await Assert.That(options.Metadata).IsNotNull();
    await Assert.That(options.Metadata!["correlation_id"]).IsEqualTo("corr-123");
    await Assert.That(options.Metadata["source_service"]).IsEqualTo("JobService");
  }

  // ============================================================
  // MessageBodyDownloadOptions — defaults
  // ============================================================

  [Test]
  public async Task MessageBodyDownloadOptions_Default_AllPropertiesNullAsync() {
    var options = new MessageBodyDownloadOptions();

    await Assert.That(options.MaxBytes).IsNull();
    await Assert.That(options.ProviderHints).IsNull();
  }

  // ============================================================
  // MessageBodyDeleteOptions — IgnoreMissing defaults true
  // ============================================================

  [Test]
  public async Task MessageBodyDeleteOptions_Default_IgnoreMissingIsTrueAsync() {
    var options = new MessageBodyDeleteOptions();

    await Assert.That(options.IgnoreMissing).IsTrue()
      .Because("Production cleanup path: a not-found body during PostInbox delete is success, not failure — TTL backstop may have already removed it.");
  }

  [Test]
  public async Task MessageBodyDeleteOptions_ExplicitFalse_PreservedAsync() {
    var options = new MessageBodyDeleteOptions { IgnoreMissing = false };

    await Assert.That(options.IgnoreMissing).IsFalse();
  }

  // ============================================================
  // IMessageBodyStore — contract compiles + members surface correctly
  // ============================================================

  [Test]
  public async Task IMessageBodyStore_NoOpImpl_ProviderNameSurfacesAsync() {
    IMessageBodyStore store = new _noOpStore("test-noop");

    await Assert.That(store.ProviderName).IsEqualTo("test-noop");
  }

  [Test]
  public async Task IMessageBodyStore_NoOpImpl_UploadAndDownloadRoundTripAsync() {
    IMessageBodyStore store = new _noOpStore("test-noop");
    var body = new byte[] { 1, 2, 3, 4 };

    var claim = await store.UploadAsync(body, "application/octet-stream");
    var fetched = await store.DownloadAsync(claim);

    await Assert.That(claim.ProviderName).IsEqualTo("test-noop");
    await Assert.That(claim.Size).IsEqualTo(4L);
    await Assert.That(fetched.ToArray()).IsEquivalentTo(body);
  }

  [Test]
  public async Task IMessageBodyStore_NoOpImpl_DeleteWithNullOptionsAsync() {
    IMessageBodyStore store = new _noOpStore("test-noop");
    var claim = await store.UploadAsync(new byte[] { 0xAA }, "application/octet-stream");

    // Must not throw — provider impls MUST tolerate null options for all three operations.
    await store.DeleteAsync(claim);
  }

  /// <summary>
  /// Minimal IMessageBodyStore impl used to verify the contract compiles and
  /// dispatches. Not exported — provider projects (Slice 4) ship the real
  /// in-memory + Azure Blob impls.
  /// </summary>
  private sealed class _noOpStore : IMessageBodyStore {
    private readonly Dictionary<string, byte[]> _bodies = [];
    public _noOpStore(string providerName) {
      ProviderName = providerName;
    }
    public string ProviderName { get; }

    public Task<MessageBodyClaim> UploadAsync(
      ReadOnlyMemory<byte> body,
      string contentType,
      MessageBodyUploadOptions? options = null,
      CancellationToken cancellationToken = default) {
      var key = $"noop://{Guid.NewGuid():N}";
      _bodies[key] = body.ToArray();
      var claim = new MessageBodyClaim(
        ProviderName: ProviderName,
        StorageKey: key,
        Size: body.Length,
        ContentHash: "sha256-noop",
        ContentType: contentType,
        UploadedAt: DateTimeOffset.UtcNow);
      return Task.FromResult(claim);
    }

    public Task<ReadOnlyMemory<byte>> DownloadAsync(
      MessageBodyClaim claim,
      MessageBodyDownloadOptions? options = null,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<ReadOnlyMemory<byte>>(_bodies[claim.StorageKey]);
    }

    public Task DeleteAsync(
      MessageBodyClaim claim,
      MessageBodyDeleteOptions? options = null,
      CancellationToken cancellationToken = default) {
      _bodies.Remove(claim.StorageKey);
      return Task.CompletedTask;
    }
  }
}
