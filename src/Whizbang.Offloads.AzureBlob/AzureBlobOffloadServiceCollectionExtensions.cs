// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Offloads;
using Whizbang.Storage.AzureBlob;

namespace Whizbang.Offloads.AzureBlob;

/// <summary>
/// The registration calls of the earlier body-offload package, kept for one release so existing hosts compile and run
/// unchanged (#2502). The provider is <c>Whizbang.Storage.AzureBlob</c>, which activates from configuration with no call.
/// </summary>
/// <docs>fundamentals/storage/azure-blob#migrating-from-the-earlier-packages</docs>
/// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobOffloadDIRegistrationTests.cs</tests>
/// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobOffloadFromConfigurationTests.cs</tests>
public static class AzureBlobOffloadServiceCollectionExtensions {
  internal const string OBSOLETE =
    "Whizbang.Offloads.AzureBlob is replaced by Whizbang.Storage.AzureBlob, which activates from Whizbang:Storage:AzureBlob " +
    "with no call: reference it, move the settings, and remove this call. See fundamentals/storage/azure-blob.";

  /// <summary>
  /// Registers an Azure Blob body store under <paramref name="providerName"/>, configured by <paramref name="configure"/>,
  /// as before. Several stores can coexist under distinct names.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="providerName">The name the sender's claims record and the receiver resolves the store by.</param>
  /// <param name="configure">Sets the store's connection string, container, access tier and download cap.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobOffloadDIRegistrationTests.cs</tests>
  [Obsolete(OBSOLETE)]
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
  /// Registers every body store <c>Whizbang:Offloads:AzureBlob:&lt;providerName&gt;</c> configures, the send-side offload
  /// hook with its selector (<c>Whizbang:BodyOffload</c>), and the cipher <c>Whizbang:BodyOffload:CipherName</c> names,
  /// all read from <paramref name="configuration"/>, as before. The new keys under <c>Whizbang:Storage:AzureBlob</c> are
  /// read too.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configuration">The application's configuration.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobOffloadFromConfigurationTests.cs</tests>
  [Obsolete(OBSOLETE)]
  public static IServiceCollection AddWhizbangAzureBlobOffloadsFromConfiguration(
      this IServiceCollection services,
      IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    services.AddWhizbangAzureBlobStorage(configuration);
    // The selector from the given configuration, as before; Whizbang:BodyOffload is also read from the host's own
    // configuration by Core, whatever the provider.
    var bodyOffload = configuration.GetSection(BODY_OFFLOAD_SECTION);
    services.Configure<MessageBodyOffloadOptions>(opts => _bindBodyOffloadOptions(bodyOffload, opts));
    services.AddWhizbangBodyCipherFromConfiguration(configuration);
    return services;
  }

  private const string BODY_OFFLOAD_SECTION = "Whizbang:BodyOffload";

  private static void _bindBodyOffloadOptions(IConfiguration section, MessageBodyOffloadOptions options) {
    var providerName = section["ProviderName"];
    if (!string.IsNullOrWhiteSpace(providerName)) {
      options.ProviderName = providerName;
    }
    if (long.TryParse(section["SizeThresholdBytes"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var sizeThresholdBytes)) {
      options.SizeThresholdBytes = sizeThresholdBytes;
    }
    if (bool.TryParse(section["ActiveCleanup"], out var activeCleanup)) {
      options.ActiveCleanup = activeCleanup;
    }
    if (bool.TryParse(section["RequireSealedBodies"], out var requireSealed)) {
      options.RequireSealedBodies = requireSealed;
    }
    var cipherName = section["CipherName"];
    if (!string.IsNullOrWhiteSpace(cipherName)) {
      options.CipherName = cipherName;
    }
  }
}
