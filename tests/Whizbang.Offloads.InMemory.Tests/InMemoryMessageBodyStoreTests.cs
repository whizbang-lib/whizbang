#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;
using Whizbang.Offloads.InMemory;

namespace Whizbang.Offloads.InMemory.Tests;

/// <summary>
/// Locks the in-memory provider's behavior end-to-end: register, upload,
/// download (with integrity check), delete, idempotent delete, and
/// MaxBytes cap.
/// </summary>
/// <docs>fundamentals/offloads/providers/in-memory</docs>
public class InMemoryMessageBodyStoreTests {

  [Test]
  public async Task AddWhizbangInMemoryOffload_RegistersStoreUnderProviderNameAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangInMemoryOffload("memory-test");
    var provider = services.BuildServiceProvider();

    var store = provider.GetKeyedService<IMessageBodyStore>("memory-test");

    await Assert.That(store).IsNotNull();
    await Assert.That(store!.ProviderName).IsEqualTo("memory-test");
    await Assert.That(store).IsTypeOf<InMemoryMessageBodyStore>();
  }

  [Test]
  public async Task UploadAsync_ReturnsClaimWithProviderAndSizeAndContentTypeAsync() {
    var store = _newStore("memory");
    var body = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

    var claim = await store.UploadAsync(body, "application/octet-stream");

    await Assert.That(claim.ProviderName).IsEqualTo("memory");
    await Assert.That(claim.Size).IsEqualTo(4L);
    await Assert.That(claim.ContentType).IsEqualTo("application/octet-stream");
    await Assert.That(claim.StorageKey).StartsWith("inmemory://");
    await Assert.That(claim.ContentHash).StartsWith("sha256-");
  }

  [Test]
  public async Task UploadAsync_ContentHashMatchesSHA256OfBodyAsync() {
    var store = _newStore("memory");
    // SHA-256 of "hello" = 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824
    var body = "hello"u8.ToArray();

    var claim = await store.UploadAsync(body, "text/plain");

    await Assert.That(claim.ContentHash).IsEqualTo(
      "sha256-2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824");
  }

  [Test]
  public async Task DownloadAsync_RoundTripsBodyAsync() {
    var store = _newStore("memory");
    var body = new byte[] { 1, 2, 3, 4, 5 };
    var claim = await store.UploadAsync(body, "application/octet-stream");

    var fetched = await store.DownloadAsync(claim);

    await Assert.That(fetched.ToArray()).IsEquivalentTo(body);
  }

  [Test]
  public async Task DownloadAsync_UnknownStorageKey_ThrowsAsync() {
    var store = _newStore("memory");
    var bogus = new MessageBodyClaim(
      "memory", "inmemory://nonexistent", 100, "sha256-x", "application/json", DateTimeOffset.UtcNow);

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await store.DownloadAsync(bogus));
  }

  [Test]
  public async Task DownloadAsync_MaxBytesExceeded_ThrowsAsync() {
    var store = _newStore("memory");
    var body = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
    var claim = await store.UploadAsync(body, "application/octet-stream");

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await store.DownloadAsync(claim, new MessageBodyDownloadOptions { MaxBytes = 4 }));
  }

  [Test]
  public async Task DeleteAsync_RemovesBodyAsync() {
    var store = _newStore("memory");
    var body = new byte[] { 1, 2, 3 };
    var claim = await store.UploadAsync(body, "application/octet-stream");

    await store.DeleteAsync(claim);

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await store.DownloadAsync(claim));
  }

  [Test]
  public async Task DeleteAsync_DefaultIgnoreMissing_NotFoundDoesNotThrowAsync() {
    var store = _newStore("memory");
    var bogus = new MessageBodyClaim(
      "memory", "inmemory://nonexistent", 0, "sha256-x", "application/json", DateTimeOffset.UtcNow);

    // Default options.IgnoreMissing is true — must not throw.
    await store.DeleteAsync(bogus);
  }

  [Test]
  public async Task DeleteAsync_StrictMode_NotFoundThrowsAsync() {
    var store = _newStore("memory");
    var bogus = new MessageBodyClaim(
      "memory", "inmemory://nonexistent", 0, "sha256-x", "application/json", DateTimeOffset.UtcNow);

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await store.DeleteAsync(bogus, new MessageBodyDeleteOptions { IgnoreMissing = false }));
  }

  private static InMemoryMessageBodyStore _newStore(string providerName) =>
    new(providerName);
}
