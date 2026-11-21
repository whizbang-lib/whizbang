// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Archival;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Archive.AzureBlob;

/// <summary>
/// Makes the Azure Blob store the service's <see cref="IArchiveStore"/>, in place of the Postgres-table store both
/// drivers register.
/// </summary>
/// <docs>fundamentals/events/archive-store#registering-the-azure-blob-store</docs>
/// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs</tests>
public static class AzureBlobArchiveServiceCollectionExtensions {
  /// <summary>The configuration section the settings are read from.</summary>
  public const string CONFIGURATION_SECTION = "Whizbang:Archive:AzureBlob";

  /// <summary>
  /// Registers the Azure Blob store as the <see cref="IArchiveStore"/>, whatever was registered before or is registered
  /// after (the drivers add theirs only when none is present), with settings from <paramref name="configure"/>.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configure">Sets the account (<see cref="AzureBlobArchiveOptions.ServiceUri"/> or
  /// <see cref="AzureBlobArchiveOptions.ConnectionString"/>) and, optionally, the container, quality and chunk size.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Registration_ReplacesTheTableStore_WhetherItComesBeforeOrAfterTheDriverAsync</tests>
  public static IServiceCollection AddWhizbangAzureBlobArchive(this IServiceCollection services, Action<AzureBlobArchiveOptions> configure) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configure);
    // Validated when the host starts (#1897): a tier the store cannot write at fails the start, not the first archive.
    services.AddOptions<AzureBlobArchiveOptions>().Configure(configure).ValidateOnStart();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AzureBlobArchiveOptions>, AccessTierValidator>());
    services.RemoveAll<IArchiveStore>();
    services.TryAddSingleton(_create);
    services.AddSingleton<IArchiveStore>(sp => sp.GetRequiredService<AzureBlobArchiveStore>());
    // Deletes the objects a purge (or a restore or delete whose own deletion failed) left queued (269).
    services.AddScoped<IMaintenanceStep>(sp => new ArchiveObjectDeletionStep(sp.GetRequiredService<AzureBlobArchiveStore>()));
    return services;
  }

  /// <summary>
  /// Registers the Azure Blob store as the <see cref="IArchiveStore"/>, with settings read from
  /// <c>Whizbang:Archive:AzureBlob</c> (<c>ServiceUri</c>, <c>ConnectionString</c>, <c>ContainerName</c>, <c>ConnectionName</c>,
  /// <c>CompressionLevel</c>, <c>MaxEventsPerChunk</c>, <c>AccessTier</c>, <c>LocationMismatch</c>, <c>DeleteAfter</c>; environment form <c>Whizbang__Archive__AzureBlob__ServiceUri</c>).
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configuration">The application's configuration.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Configuration_BindsEverySetting_AndKeepsTheDefaultsForWhatIsAbsentAsync</tests>
  public static IServiceCollection AddWhizbangAzureBlobArchive(this IServiceCollection services, IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(configuration);
    var section = configuration.GetSection(CONFIGURATION_SECTION);
    return services.AddWhizbangAzureBlobArchive(options => Bind(section, options));
  }

  /// <summary>Reads the settings by hand (no reflection binder), refusing a value it cannot read rather than ignoring it.</summary>
  internal static void Bind(IConfiguration section, AzureBlobArchiveOptions options) {
    var serviceUri = section[nameof(AzureBlobArchiveOptions.ServiceUri)];
    if (!string.IsNullOrWhiteSpace(serviceUri)) {
      options.ServiceUri = Uri.TryCreate(serviceUri, UriKind.Absolute, out var uri)
        ? uri
        : throw _unreadable(nameof(AzureBlobArchiveOptions.ServiceUri), serviceUri);
    }
    Whizbang.Core.Configuration.ConfigurationValueBinder.BindSensitive(
      section, nameof(AzureBlobArchiveOptions.ConnectionString), value => options.ConnectionString = value);
    var containerName = section[nameof(AzureBlobArchiveOptions.ContainerName)];
    if (!string.IsNullOrWhiteSpace(containerName)) {
      options.ContainerName = containerName;
    }
    var connectionName = section[nameof(AzureBlobArchiveOptions.ConnectionName)];
    if (!string.IsNullOrWhiteSpace(connectionName)) {
      // Never echoed: a value that looks like a connection string may be one.
      options.ConnectionName = AzureBlobArchiveOptions.IsLogicalName(connectionName)
        ? connectionName
        : throw new InvalidOperationException(
          $"{CONFIGURATION_SECTION}:{nameof(AzureBlobArchiveOptions.ConnectionName)} is a logical name, not a connection string: " +
          "it may not contain ';' or '='.");
    }
    // #2141: VerifyLocation was replaced by LocationMismatch. Refused rather than ignored, so an archive configured
    // with it never changes behavior unannounced.
    if (!string.IsNullOrWhiteSpace(section[REMOVED_VERIFY_LOCATION])) {
      throw new InvalidOperationException(
        $"{CONFIGURATION_SECTION}:{REMOVED_VERIFY_LOCATION} was replaced by {CONFIGURATION_SECTION}:{nameof(AzureBlobArchiveOptions.LocationMismatch)} " +
        "(#2141): VerifyLocation true is LocationMismatch Error, the default, and false is Ignore. Remove VerifyLocation, and set " +
        "LocationMismatch to Error, Warning, Info or Ignore where the default does not fit.");
    }
    var locationMismatch = section[nameof(AzureBlobArchiveOptions.LocationMismatch)];
    if (!string.IsNullOrWhiteSpace(locationMismatch)) {
      options.LocationMismatch = AzureBlobArchiveOptions.LocationMismatchNamed(locationMismatch)
        ?? throw _locationMismatchRefused(locationMismatch);
    }
    options.CompressionLevel = _int(section, nameof(AzureBlobArchiveOptions.CompressionLevel), options.CompressionLevel);
    options.MaxEventsPerChunk = _int(section, nameof(AzureBlobArchiveOptions.MaxEventsPerChunk), options.MaxEventsPerChunk);
    var deleteAfter = section[nameof(AzureBlobArchiveOptions.DeleteAfter)];
    if (!string.IsNullOrWhiteSpace(deleteAfter)) {
      options.DeleteAfter = TimeSpan.TryParse(deleteAfter, CultureInfo.InvariantCulture, out var delay)
        && AzureBlobArchiveOptions.RefusalForDeleteAfter(delay) is null
        ? delay
        : throw new InvalidOperationException(
          $"{CONFIGURATION_SECTION}:{nameof(AzureBlobArchiveOptions.DeleteAfter)} is '{deleteAfter}'. " +
          AzureBlobArchiveOptions.DELETE_AFTER_REFUSAL);
    }
    var accessTier = section[nameof(AzureBlobArchiveOptions.AccessTier)];
    if (!string.IsNullOrWhiteSpace(accessTier)) {
      // Hot, Cool or Cold in any case, kept in the SDK's spelling; anything else is refused with the reason.
      options.AccessTier = AzureBlobArchiveOptions.Online(new AccessTier(accessTier))
        ?? throw _tierRefused(accessTier, AzureBlobArchiveOptions.RefusalFor(new AccessTier(accessTier))!);
    }
  }

  /// <summary>
  /// Refuses, when the host starts, a tier set in code that the store cannot write at, the same tiers
  /// <see cref="Bind"/> refuses from configuration, and a location-mismatch handling that is not defined.
  /// </summary>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Startup_AnUndefinedLocationMismatchSetInCode_FailsTheHostAtStartAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Startup_AnArchiveTierSetInCode_FailsTheHostAtStart_ACoolTierDoesNotAsync</tests>
  private sealed class AccessTierValidator : IValidateOptions<AzureBlobArchiveOptions> {
    public ValidateOptionsResult Validate(string? name, AzureBlobArchiveOptions options) {
      if (AzureBlobArchiveOptions.RefusalFor(options.AccessTier) is { } refusal) {
        return ValidateOptionsResult.Fail(_tierRefused($"{options.AccessTier}", refusal).Message);
      }
      if (AzureBlobArchiveOptions.RefusalForLocationMismatch(options.LocationMismatch) is not null) {
        return ValidateOptionsResult.Fail(_locationMismatchRefused($"{options.LocationMismatch}").Message);
      }
      return AzureBlobArchiveOptions.RefusalForDeleteAfter(options.DeleteAfter) is { } delayRefusal
        ? ValidateOptionsResult.Fail($"{CONFIGURATION_SECTION}:{nameof(AzureBlobArchiveOptions.DeleteAfter)} is '{options.DeleteAfter}'. {delayRefusal}")
        : ValidateOptionsResult.Success;
    }
  }

  // The setting an earlier release read in place of LocationMismatch.
  private const string REMOVED_VERIFY_LOCATION = "VerifyLocation";

  private static InvalidOperationException _locationMismatchRefused(string value) =>
    new($"{CONFIGURATION_SECTION}:{nameof(AzureBlobArchiveOptions.LocationMismatch)} is '{value}'. {AzureBlobArchiveOptions.LOCATION_MISMATCH_VALUES}");

  private static InvalidOperationException _tierRefused(string value, string refusal) =>
    new($"{CONFIGURATION_SECTION}:{nameof(AzureBlobArchiveOptions.AccessTier)} is '{value}'. {refusal}");

  /// <summary>
  /// The container client the settings name: a connection string as given, or the service endpoint with the registered
  /// <see cref="TokenCredential"/>.
  /// </summary>
  /// <exception cref="InvalidOperationException">Neither or both of the endpoint and the connection string are set, or
  /// the endpoint is set and no credential is registered.</exception>
  internal static BlobContainerClient CreateContainerClient(AzureBlobArchiveOptions options, TokenCredential? credential) {
    var hasUri = options.ServiceUri is not null;
    var hasConnectionString = !string.IsNullOrWhiteSpace(options.ConnectionString?.Reveal());
    if (hasUri == hasConnectionString) {
      throw new InvalidOperationException(
        $"Set exactly one of {CONFIGURATION_SECTION}:ServiceUri (with a TokenCredential registered for its identity) or " +
        $"{CONFIGURATION_SECTION}:ConnectionString for the Azure Blob archive store.");
    }
    if (hasConnectionString) {
      return new BlobContainerClient(options.ConnectionString!.Reveal(), options.ContainerName);
    }
    if (credential is null) {
      throw new InvalidOperationException(
        $"{CONFIGURATION_SECTION}:ServiceUri is set, so the Azure Blob archive store authenticates with the " +
        "Azure.Core.TokenCredential in the service container, and none is registered. Register the credential for the " +
        "service's identity (for example a managed identity credential).");
    }
    return new BlobServiceClient(options.ServiceUri, credential).GetBlobContainerClient(options.ContainerName);
  }

  private static AzureBlobArchiveStore _create(IServiceProvider services) {
    var options = services.GetRequiredService<IOptions<AzureBlobArchiveOptions>>().Value;
    var database = services.GetService<PostgresArchiveConnection>()
      ?? throw new InvalidOperationException(
        "The Azure Blob archive store keeps its manifest in the service's Postgres database, and no Postgres driver is " +
        "registered. Register the EF Core or the Dapper Postgres driver as well.");
    return new AzureBlobArchiveStore(
      database,
      CreateContainerClient(options, services.GetService<TokenCredential>()),
      options,
      services.GetService<ILogger<AzureBlobArchiveStore>>(),
      services.GetService<TimeProvider>(),
      services.GetService<Whizbang.Core.Observability.WhizbangMetrics>() is { } metrics ? new ArchiveSweepMetrics(metrics) : null);
  }

  private static int _int(IConfiguration section, string key, int current) {
    var text = section[key];
    if (string.IsNullOrWhiteSpace(text)) {
      return current;
    }
    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw _unreadable(key, text);
  }

  private static InvalidOperationException _unreadable(string key, string value) =>
    new($"{CONFIGURATION_SECTION}:{key} is '{value}', which is not a valid value.");
}
