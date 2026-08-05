using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Offloads;

/// <summary>
/// DI registration surface for body-store providers. Each
/// <see cref="IMessageBodyStore"/> implementation registers under a stable
/// provider name; the receive-side resolver looks up the matching store by
/// <see cref="MessageBodyClaim.ProviderName"/> when a claim arrives on the
/// wire.
/// </summary>
/// <docs>fundamentals/offloads/message-body-store</docs>
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

  /// <summary>
  /// Registers a post-serialize hook in the publish pipeline. The chain
  /// is built at <see cref="PostSerializeHookChain"/> construction from
  /// all registered <see cref="IPostSerializeHook"/> instances ordered by
  /// <see cref="IPostSerializeHook.Order"/>.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/PostSerializeHookChainTests.cs</tests>
  public static IServiceCollection AddWhizbangPostSerializeHook<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THook>(
      this IServiceCollection services) where THook : class, IPostSerializeHook {
    ArgumentNullException.ThrowIfNull(services);
    services.AddSingleton<IPostSerializeHook, THook>();
    services.TryAddSingleton<PostSerializeHookChain>();
    return services;
  }

  /// <summary>
  /// Convenience: register the built-in body-offload hook (claim-check
  /// pattern) along with the options binding. Use alongside
  /// <see cref="AddWhizbangMessageBodyStore{TStore}"/> and
  /// <c>services.Configure&lt;MessageBodyOffloadOptions&gt;(...)</c>;
  /// without a configured ProviderName the hook is a no-op (pass-through).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/BodyOffloadPostSerializeHookTests.cs</tests>
  public static IServiceCollection AddWhizbangBodyOffload(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    // AddOptions registers IOptions/IOptionsMonitor/IOptionsSnapshot for the
    // type even when no Configure is called — defaults still resolve.
    services.AddOptions<MessageBodyOffloadOptions>();
    return services.AddWhizbangPostSerializeHook<BodyOffloadPostSerializeHook>();
  }
}
