using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Offloads;

namespace Whizbang.Offloads.AzureBlob;

/// <summary>
/// DI registration for the Azure Blob body-store provider.
/// </summary>
/// <docs>fundamentals/offloads/providers/azure-blob</docs>
public static class AzureBlobOffloadServiceCollectionExtensions {

  // Config section holding one subsection per Azure Blob offload provider
  // (Whizbang:Offloads:AzureBlob:<providerName>).
  private const string AZURE_BLOB_PROVIDERS_SECTION = "Whizbang:Offloads:AzureBlob";

  // Config section holding the send-side body-offload selector (MessageBodyOffloadOptions):
  // Whizbang:BodyOffload.
  private const string BODY_OFFLOAD_SECTION = "Whizbang:BodyOffload";

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

  /// <summary>
  /// Convention-based, config-driven offload wiring — the one call a host needs. Registers an
  /// Azure Blob body store for EVERY provider found under
  /// <c>Whizbang:Offloads:AzureBlob:&lt;providerName&gt;</c>, then enables the send-side claim-check
  /// hook and binds the selector (<see cref="MessageBodyOffloadOptions"/>) from
  /// <c>Whizbang:BodyOffload</c>. The provider whose name matches
  /// <c>Whizbang:BodyOffload:ProviderName</c> becomes the active offload target.
  ///
  /// <para>A no-op when no providers are configured, so a service runs with inline message bodies
  /// until the environment (env vars / appsettings) supplies the keys — offload is opt-in by config
  /// presence, with nothing to wire per-service.</para>
  ///
  /// <para>Options bind explicitly (not <c>configuration.Bind</c>) so the Azure SDK extensible-enum
  /// <see cref="AzureBlobOffloadOptions.DefaultAccessTier"/> can be constructed from its string form
  /// (e.g. <c>"Cool"</c>), which <c>ConfigurationBinder</c> cannot convert.</para>
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configuration">Application configuration (root). Read paths:
  /// <c>Whizbang:Offloads:AzureBlob</c> and <c>Whizbang:BodyOffload</c>.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <docs>fundamentals/offloads/providers/azure-blob</docs>
  /// <tests>tests/Whizbang.Offloads.AzureBlob.Tests/AzureBlobOffloadFromConfigurationTests.cs</tests>
  public static IServiceCollection AddWhizbangAzureBlobOffloadsFromConfiguration(
      this IServiceCollection services,
      IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    var anyProvider = false;
    foreach (var providerSection in configuration.GetSection(AZURE_BLOB_PROVIDERS_SECTION).GetChildren()) {
      var providerName = providerSection.Key;
      if (string.IsNullOrWhiteSpace(providerName)) {
        continue;
      }

      services.AddWhizbangAzureBlobOffload(providerName, opts => _bindProviderOptions(providerSection, opts));
      anyProvider = true;
    }

    // No providers configured → leave offload off (hook unregistered = pass-through publish).
    if (!anyProvider) {
      return services;
    }

    // Send-side claim-check hook + the selector that points it at a provider.
    services.AddWhizbangBodyOffload();
    var bodyOffload = configuration.GetSection(BODY_OFFLOAD_SECTION);
    services.Configure<MessageBodyOffloadOptions>(opts => _bindBodyOffloadOptions(bodyOffload, opts));
    return services;
  }

  private static void _bindProviderOptions(IConfiguration section, AzureBlobOffloadOptions options) {
    var connectionString = section["ConnectionString"];
    if (!string.IsNullOrWhiteSpace(connectionString)) {
      options.ConnectionString = connectionString;
    }

    var containerName = section["ContainerName"];
    if (!string.IsNullOrWhiteSpace(containerName)) {
      options.ContainerName = containerName;
    }

    var accessTier = section["DefaultAccessTier"];
    if (!string.IsNullOrWhiteSpace(accessTier)) {
      options.DefaultAccessTier = new AccessTier(accessTier);
    }

    if (long.TryParse(section["MaxDownloadBytes"], out var maxDownloadBytes)) {
      options.MaxDownloadBytes = maxDownloadBytes;
    }
  }

  private static void _bindBodyOffloadOptions(IConfiguration section, MessageBodyOffloadOptions options) {
    var providerName = section["ProviderName"];
    if (!string.IsNullOrWhiteSpace(providerName)) {
      options.ProviderName = providerName;
    }

    if (long.TryParse(section["SizeThresholdBytes"], out var sizeThresholdBytes)) {
      options.SizeThresholdBytes = sizeThresholdBytes;
    }

    if (bool.TryParse(section["ActiveCleanup"], out var activeCleanup)) {
      options.ActiveCleanup = activeCleanup;
    }
  }
}
