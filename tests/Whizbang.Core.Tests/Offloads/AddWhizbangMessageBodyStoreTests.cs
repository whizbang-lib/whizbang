#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// DI registration surface for body-store providers. Locks the contract
/// that:
///   - <c>AddWhizbangMessageBodyStore&lt;TStore&gt;(name)</c> registers
///     the store as a <em>keyed singleton</em> resolvable by provider name.
///   - Multiple providers can coexist (e.g., azure-blob-prod +
///     azure-blob-archive); resolving by key returns the matching one.
///   - Singleton lifetime — the same instance is returned across resolutions,
///     so providers that maintain expensive state (HTTP clients, connection
///     pools) construct once.
///   - The offload-config layer (a future slice) selects which provider to
///     use by name; this DI surface is what makes name-keyed lookup work.
/// </summary>
/// <docs>fundamentals/offloads/di-registration</docs>
public class AddWhizbangMessageBodyStoreTests {

  [Test]
  public async Task AddWhizbangMessageBodyStore_RegistersByProviderName_ResolvesByNameAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangMessageBodyStore<_fakeStore>("in-memory-test");
    var provider = services.BuildServiceProvider();

    var store = provider.GetKeyedService<IMessageBodyStore>("in-memory-test");

    await Assert.That(store).IsNotNull();
    await Assert.That(store!.ProviderName).IsEqualTo("in-memory-test")
      .Because("The DI key matches the provider's declared ProviderName so the receive-side resolver finds the store by the claim's ProviderName.");
  }

  [Test]
  public async Task AddWhizbangMessageBodyStore_TwoProviders_CoexistAndResolveByKeyAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangMessageBodyStore<_fakeStore>("azure-blob-prod");
    services.AddWhizbangMessageBodyStore<_fakeStore>("azure-blob-archive");
    var provider = services.BuildServiceProvider();

    var prod = provider.GetKeyedService<IMessageBodyStore>("azure-blob-prod");
    var archive = provider.GetKeyedService<IMessageBodyStore>("azure-blob-archive");

    await Assert.That(prod).IsNotNull();
    await Assert.That(archive).IsNotNull();
    await Assert.That(prod!.ProviderName).IsEqualTo("azure-blob-prod");
    await Assert.That(archive!.ProviderName).IsEqualTo("azure-blob-archive");
  }

  [Test]
  public async Task AddWhizbangMessageBodyStore_SingletonLifetime_ReturnsSameInstanceAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangMessageBodyStore<_fakeStore>("test");
    var provider = services.BuildServiceProvider();

    var first = provider.GetKeyedService<IMessageBodyStore>("test");
    var second = provider.GetKeyedService<IMessageBodyStore>("test");

    await Assert.That(first).IsSameReferenceAs(second!)
      .Because("Singleton lifetime — providers with expensive state (HTTP clients, connection pools) MUST construct once.");
  }

  [Test]
  public async Task AddWhizbangMessageBodyStore_UnknownKey_ReturnsNullAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangMessageBodyStore<_fakeStore>("known");
    var provider = services.BuildServiceProvider();

    var store = provider.GetKeyedService<IMessageBodyStore>("unknown");

    await Assert.That(store).IsNull()
      .Because("Asking for an unregistered provider returns null (not throws) so the receive-side resolver can dead-letter with a clear MessageBodyClaimProviderUnknown signal instead of an opaque DI exception.");
  }

  /// <summary>
  /// Fake store whose ProviderName is supplied via the DI key — proves the
  /// registration uses the constructor-injected key, not a hard-coded value.
  /// .NET keyed services pass the key as [FromKeyedServices] constructor
  /// param via Microsoft.Extensions.DI infrastructure.
  /// </summary>
  private sealed class _fakeStore : IMessageBodyStore {
    public _fakeStore([Microsoft.Extensions.DependencyInjection.ServiceKey] string providerName) {
      ProviderName = providerName;
    }
    public string ProviderName { get; }
    public Task<MessageBodyClaim> UploadAsync(
      ReadOnlyMemory<byte> body, string contentType,
      MessageBodyUploadOptions? options = null,
      CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<ReadOnlyMemory<byte>> DownloadAsync(
      MessageBodyClaim claim,
      MessageBodyDownloadOptions? options = null,
      CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task DeleteAsync(
      MessageBodyClaim claim,
      MessageBodyDeleteOptions? options = null,
      CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
  }
}
