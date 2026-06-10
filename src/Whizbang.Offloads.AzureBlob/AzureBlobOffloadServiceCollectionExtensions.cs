using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Offloads;

namespace Whizbang.Offloads.AzureBlob;

/// <summary>
/// DI registration for the Azure Blob body-store provider.
/// </summary>
/// <docs>fundamentals/offloads/providers/azure-blob</docs>
public static class AzureBlobOffloadServiceCollectionExtensions {

  /// <summary>
  /// Registers an <see cref="AzureBlobMessageBodyStore"/> under the supplied
  /// provider name. The <paramref name="configure"/> action binds
  /// <see cref="AzureBlobOffloadOptions"/> for this specific provider —
  /// multiple Azure Blob providers can coexist (e.g., <c>"azure-blob-prod"</c>
  /// + <c>"azure-blob-archive"</c>) with different connection strings,
  /// containers, or access tiers.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="providerName">Stable identifier the receive-side resolver uses to find this store. MUST match the <see cref="MessageBodyClaim.ProviderName"/> the sender emits.</param>
  /// <param name="configure">Action that populates <see cref="AzureBlobOffloadOptions"/> for this provider (ConnectionString, ContainerName, optional DefaultAccessTier, MaxDownloadBytes).</param>
  /// <returns>The service collection for chaining.</returns>
  /// <tests>tests/Whizbang.Offloads.AzureBlob.Tests/AzureBlobOffloadDIRegistrationTests.cs</tests>
  public static IServiceCollection AddWhizbangAzureBlobOffload(
      this IServiceCollection services,
      string providerName,
      Action<AzureBlobOffloadOptions> configure) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
    ArgumentNullException.ThrowIfNull(configure);

    services.AddOptions<AzureBlobOffloadOptions>(providerName).Configure(configure);
    return services.AddWhizbangMessageBodyStore<AzureBlobMessageBodyStore>(providerName);
  }
}
