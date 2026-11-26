// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Storage.AzureBlob;

namespace Whizbang.Archive.AzureBlob;

/// <summary>
/// The registration calls of the earlier archive package, kept for one release so existing hosts compile and run
/// unchanged (#2502). The provider is <c>Whizbang.Storage.AzureBlob</c>, which activates from configuration with no call,
/// and the archive's transaction is the database driver's (#2503).
/// </summary>
/// <docs>fundamentals/storage/azure-blob#migrating-from-the-earlier-packages</docs>
/// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs</tests>
public static class AzureBlobArchiveServiceCollectionExtensions {
  /// <summary>The earlier package's configuration section, still read.</summary>
  public const string CONFIGURATION_SECTION = AzureBlobStorageOptions.LEGACY_ARCHIVE_SECTION;

  internal const string OBSOLETE =
    "Whizbang.Archive.AzureBlob is replaced by Whizbang.Storage.AzureBlob, which activates from Whizbang:Storage:AzureBlob " +
    "with no call: reference it, move the settings, and remove this call. See fundamentals/storage/azure-blob.";

  /// <summary>
  /// Archives through Azure Blob Storage, with settings from <paramref name="configure"/>, as before: the database driver's
  /// archive coordinator writes the objects.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configure">Sets the account (<c>ServiceUri</c> or <c>ConnectionString</c>) and, optionally, the
  /// container, quality and chunk size.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Registration_TheDriverArchivesThroughBlob_WhetherItComesBeforeOrAfterTheDriverAsync</tests>
  [Obsolete(OBSOLETE)]
  public static IServiceCollection AddWhizbangAzureBlobArchive(this IServiceCollection services, Action<AzureBlobArchiveOptions> configure) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configure);
    return services.AddWhizbangAzureBlobStorage(options => {
      options.Archive ??= new AzureBlobArchiveOptions();
      configure(options.Archive);
    });
  }

  /// <summary>
  /// Archives through Azure Blob Storage, with settings read from <c>Whizbang:Archive:AzureBlob</c> of
  /// <paramref name="configuration"/>, as before. The new keys under <c>Whizbang:Storage:AzureBlob</c> are read too.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configuration">The application's configuration.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Configuration_BindsEverySetting_AndKeepsTheDefaultsForWhatIsAbsentAsync</tests>
  [Obsolete(OBSOLETE)]
  public static IServiceCollection AddWhizbangAzureBlobArchive(this IServiceCollection services, IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);
    return services.AddWhizbangAzureBlobStorage(configuration);
  }
}
