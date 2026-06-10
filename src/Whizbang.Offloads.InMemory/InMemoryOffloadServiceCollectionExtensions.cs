using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Offloads;

namespace Whizbang.Offloads.InMemory;

/// <summary>
/// DI registration for the in-memory body-store provider.
/// </summary>
/// <docs>fundamentals/offloads/providers/in-memory</docs>
public static class InMemoryOffloadServiceCollectionExtensions {

  /// <summary>
  /// Registers an <see cref="InMemoryMessageBodyStore"/> under the supplied
  /// provider name. Dev/test/fixture use only — bodies live in process
  /// memory and disappear at restart.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="providerName">Stable identifier the receive-side resolver uses to find this store. MUST match the <see cref="MessageBodyClaim.ProviderName"/> the sender emits.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <tests>tests/Whizbang.Offloads.InMemory.Tests/InMemoryMessageBodyStoreTests.cs:AddWhizbangInMemoryOffload_RegistersStoreUnderProviderNameAsync</tests>
  public static IServiceCollection AddWhizbangInMemoryOffload(
      this IServiceCollection services,
      string providerName) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

    return services.AddWhizbangMessageBodyStore<InMemoryMessageBodyStore>(providerName);
  }
}
