using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Offloads;

/// <summary>
/// DI registration surface for body-store providers. Each
/// <see cref="IMessageBodyStore"/> implementation registers under a stable
/// provider name; the receive-side resolver looks up the matching store by
/// <see cref="MessageBodyClaim.ProviderName"/> when a claim arrives on the
/// wire.
/// </summary>
/// <docs>fundamentals/offloads/di-registration</docs>
public static class OffloadServiceCollectionExtensions {

  /// <summary>
  /// Registers a body-store implementation as a keyed singleton under
  /// <paramref name="providerName"/>. Multiple providers can be registered
  /// with distinct names (e.g., <c>"azure-blob-prod"</c>,
  /// <c>"azure-blob-archive"</c>); the offload config picks one by name.
  /// </summary>
  /// <typeparam name="TStore">Concrete <see cref="IMessageBodyStore"/> implementation type. Must support construction from the DI container; if the impl wants the provider name injected, it accepts a <c>[ServiceKey] string</c> parameter.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="providerName">Stable identifier that the sender's claim and the receiver's resolver both reference. MUST be unique across registrations.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// Singleton lifetime: the same instance is reused across resolutions so
  /// providers that maintain expensive state (HTTP clients, connection
  /// pools, blob-service clients) construct once.
  /// </para>
  /// <para>
  /// Provider-project ergonomics: each provider project typically wraps
  /// this with a typed extension that takes its options
  /// (e.g., <c>AddWhizbangInMemoryOffload(name)</c>,
  /// <c>AddWhizbangAzureBlobOffload(name, opts =&gt; …)</c>) so consumers
  /// don't construct providers by hand.
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/AddWhizbangMessageBodyStoreTests.cs:AddWhizbangMessageBodyStore_RegistersByProviderName_ResolvesByNameAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/AddWhizbangMessageBodyStoreTests.cs:AddWhizbangMessageBodyStore_TwoProviders_CoexistAndResolveByKeyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/AddWhizbangMessageBodyStoreTests.cs:AddWhizbangMessageBodyStore_SingletonLifetime_ReturnsSameInstanceAsync</tests>
  public static IServiceCollection AddWhizbangMessageBodyStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
      this IServiceCollection services,
      string providerName) where TStore : class, IMessageBodyStore {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

    services.AddKeyedSingleton<IMessageBodyStore, TStore>(providerName);
    return services;
  }
}
