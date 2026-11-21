// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Whizbang.Core.Configuration;

namespace Whizbang.Storage.AzureBlob;

/// <summary>
/// Reads <see cref="AzureBlobStorageOptions"/> from configuration by hand (no reflection binder), refusing a value it
/// cannot read with the key that holds it (#2502): the new keys, and the keys of the earlier packages, each with a
/// warning naming its new key. Then decides, once code has configured the options too, which features are active and why.
/// </summary>
/// <docs>fundamentals/storage/azure-blob#configuration</docs>
/// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobStorageConfigurationTests.cs</tests>
internal static class AzureBlobStorageConfiguration {
  private const string SERVICE_URI = nameof(AzureBlobStorageOptions.ServiceUri);
  private const string CONNECTION_STRING = nameof(AzureBlobStorageOptions.ConnectionString);
  private const string CONNECTION_NAME = nameof(AzureBlobStorageOptions.ConnectionName);

  /// <summary>Binds every key of <paramref name="configuration"/> the package reads into <paramref name="options"/>.</summary>
  internal static void Bind(IConfiguration configuration, AzureBlobStorageOptions options) {
    var root = configuration.GetSection(AzureBlobStorageOptions.CONFIGURATION_SECTION);
    _bindAccount(configuration, root, options);

    // The archive: the earlier package's section first, then the new one over it, so a new key wins.
    var legacyArchive = configuration.GetSection(AzureBlobStorageOptions.LEGACY_ARCHIVE_SECTION);
    var archive = configuration.GetSection(AzureBlobStorageOptions.ARCHIVE_SECTION);
    if (_hasKeys(legacyArchive)) {
      options.Archive ??= new AzureBlobArchiveOptions();
      BindArchive(legacyArchive, AzureBlobStorageOptions.LEGACY_ARCHIVE_SECTION, options.Archive);
      options.Notices.Add(_moved(AzureBlobStorageOptions.LEGACY_ARCHIVE_SECTION,
        $"{AzureBlobStorageOptions.CONFIGURATION_SECTION} (the account) and {AzureBlobStorageOptions.ARCHIVE_SECTION}"));
    }
    if (_hasKeys(archive)) {
      options.Archive ??= new AzureBlobArchiveOptions();
      BindArchive(archive, AzureBlobStorageOptions.ARCHIVE_SECTION, options.Archive);
    }

    // Body offload: each provider of the earlier package's section under its own name, and the new store.
    foreach (var provider in configuration.GetSection(AzureBlobStorageOptions.LEGACY_OFFLOADS_SECTION).GetChildren()) {
      if (string.IsNullOrWhiteSpace(provider.Key)) {
        continue;
      }
      var named = new AzureBlobOffloadOptions { ProviderName = provider.Key };
      BindOffload(provider, provider.Path, named, unified: false);
      options.NamedOffloads[provider.Key] = named;
      options.Notices.Add(_moved(provider.Path,
        $"{AzureBlobStorageOptions.CONFIGURATION_SECTION} (the account) and {AzureBlobStorageOptions.OFFLOADS_SECTION}, " +
        $"with {AzureBlobStorageOptions.OFFLOADS_SECTION}:ProviderName set to '{provider.Key}' so claims already sent still resolve"));
    }
    var offloads = configuration.GetSection(AzureBlobStorageOptions.OFFLOADS_SECTION);
    if (_hasKeys(offloads)) {
      options.Offloads ??= new AzureBlobOffloadOptions();
      BindOffload(offloads, AzureBlobStorageOptions.OFFLOADS_SECTION, options.Offloads, unified: true);
      if (!string.IsNullOrWhiteSpace(configuration[AzureBlobStorageOptions.LEGACY_PROVIDER_NAME_KEY])) {
        options.Notices.Add(
          $"{AzureBlobStorageOptions.LEGACY_PROVIDER_NAME_KEY} is set, and {AzureBlobStorageOptions.OFFLOADS_SECTION} chooses the body " +
          $"store: {AzureBlobStorageOptions.OFFLOADS_SECTION}:ProviderName wins. Remove {AzureBlobStorageOptions.LEGACY_PROVIDER_NAME_KEY}.");
      }
    }
  }

  // The shared account: a service endpoint, a connection string, or a connection name whose ConnectionStrings entry
  // holds either.
  private static void _bindAccount(IConfiguration configuration, IConfigurationSection root, AzureBlobStorageOptions options) {
    const string section = AzureBlobStorageOptions.CONFIGURATION_SECTION;
    if (_uri(root, section, SERVICE_URI) is { } uri) {
      options.ServiceUri = uri;
      options.AccountSource = $"{section}:{SERVICE_URI}";
    }
    ConfigurationValueBinder.BindSensitive(root, CONNECTION_STRING, value => {
      options.ConnectionString = value;
      options.AccountSource = $"{section}:{CONNECTION_STRING}";
    });
    if (_logicalName(root, section) is not { } name) {
      return;
    }
    options.ConnectionName = name;
    if (options.ServiceUri is not null || options.ConnectionString is not null) {
      return;
    }
    var named = configuration.GetConnectionString(name);
    if (string.IsNullOrWhiteSpace(named)) {
      return;
    }
    // A connection entry holds a service endpoint or a connection string: an absolute https or http address is the
    // endpoint (authenticated with the registered credential), anything else a connection string.
    if (Uri.TryCreate(named, UriKind.Absolute, out var endpoint) && (endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp)) {
      options.ServiceUri = endpoint;
    } else {
      options.ConnectionString = new SensitiveString(named);
    }
    options.AccountSource = $"ConnectionStrings:{name} (named by {section}:{CONNECTION_NAME})";
  }

  /// <summary>Reads the archive's settings from one section, refusing a value it cannot read rather than ignoring it.</summary>
  internal static void BindArchive(IConfiguration section, string sectionName, AzureBlobArchiveOptions options) {
    if (_uri(section, sectionName, SERVICE_URI) is { } uri) {
      options.ServiceUri = uri;
      options.AccountSource = $"{sectionName}:{SERVICE_URI}";
    }
    ConfigurationValueBinder.BindSensitive(section, CONNECTION_STRING, value => {
      options.ConnectionString = value;
      options.AccountSource = $"{sectionName}:{CONNECTION_STRING}";
    });
    var containerName = section[nameof(AzureBlobArchiveOptions.ContainerName)];
    if (!string.IsNullOrWhiteSpace(containerName)) {
      options.ContainerName = containerName;
    }
    if (_logicalName(section, sectionName) is { } connectionName) {
      options.ConnectionName = connectionName;
      options.ConnectionNameBound = true;
    }
    var verifyLocation = section[nameof(AzureBlobArchiveOptions.VerifyLocation)];
    if (!string.IsNullOrWhiteSpace(verifyLocation)) {
      options.VerifyLocation = bool.TryParse(verifyLocation, out var verify)
        ? verify
        : throw _unreadable(sectionName, nameof(AzureBlobArchiveOptions.VerifyLocation), verifyLocation);
    }
    options.CompressionLevel = _int(section, sectionName, nameof(AzureBlobArchiveOptions.CompressionLevel), options.CompressionLevel);
    options.MaxEventsPerChunk = _int(section, sectionName, nameof(AzureBlobArchiveOptions.MaxEventsPerChunk), options.MaxEventsPerChunk);
    var deleteAfter = section[nameof(AzureBlobArchiveOptions.DeleteAfter)];
    if (!string.IsNullOrWhiteSpace(deleteAfter)) {
      options.DeleteAfter = TimeSpan.TryParse(deleteAfter, CultureInfo.InvariantCulture, out var delay)
        && AzureBlobArchiveOptions.RefusalForDeleteAfter(delay) is null
        ? delay
        : throw new InvalidOperationException(
          $"{sectionName}:{nameof(AzureBlobArchiveOptions.DeleteAfter)} is '{deleteAfter}'. {AzureBlobArchiveOptions.DELETE_AFTER_REFUSAL}");
    }
    var accessTier = section[nameof(AzureBlobArchiveOptions.AccessTier)];
    if (!string.IsNullOrWhiteSpace(accessTier)) {
      // Hot, Cool or Cold in any case, kept in the SDK's spelling; anything else is refused with the reason.
      options.AccessTier = AzureBlobArchiveOptions.Online(new AccessTier(accessTier))
        ?? throw TierRefused(sectionName, accessTier, AzureBlobArchiveOptions.RefusalFor(new AccessTier(accessTier))!);
    }
  }

  /// <summary>
  /// Reads one body store's settings. The earlier package's section reads what it always read: the connection string,
  /// the container, the access tier and the download cap, a value it cannot parse leaving the default; the new section
  /// adds the service endpoint and the provider name.
  /// </summary>
  internal static void BindOffload(IConfiguration section, string sectionName, AzureBlobOffloadOptions options, bool unified) {
    ConfigurationValueBinder.BindSensitive(section, CONNECTION_STRING, value => {
      options.ConnectionString = value;
      options.AccountSource = $"{sectionName}:{CONNECTION_STRING}";
    });
    var containerName = section[nameof(AzureBlobOffloadOptions.ContainerName)];
    if (!string.IsNullOrWhiteSpace(containerName)) {
      options.ContainerName = containerName;
    }
    var accessTier = section[nameof(AzureBlobOffloadOptions.DefaultAccessTier)];
    if (!string.IsNullOrWhiteSpace(accessTier)) {
      options.DefaultAccessTier = new AccessTier(accessTier);
    }
    if (long.TryParse(section[nameof(AzureBlobOffloadOptions.MaxDownloadBytes)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxDownloadBytes)) {
      options.MaxDownloadBytes = maxDownloadBytes;
    }
    if (!unified) {
      return;
    }
    if (_uri(section, sectionName, SERVICE_URI) is { } uri) {
      options.ServiceUri = uri;
      options.AccountSource = $"{sectionName}:{SERVICE_URI}";
    }
    var providerName = section[nameof(AzureBlobOffloadOptions.ProviderName)];
    if (!string.IsNullOrWhiteSpace(providerName)) {
      options.ProviderName = providerName;
    }
  }

  /// <summary>
  /// Decides, after configuration and code have both set the options, which features are active: each gets the shared
  /// account unless it names its own, and a feature with no account at all is left off with a warning.
  /// </summary>
  internal static void Complete(AzureBlobStorageOptions options) {
    if (options.Archive is { } archive) {
      // The location's connection name: the archive's own (new section, then the earlier one), else the shared name. An
      // environment's archive keeps the name its records were written with, so the shared name never replaces the
      // archive's own.
      if (!archive.ConnectionNameBound && options.ConnectionName is { } shared
          && string.Equals(archive.ConnectionName, AzureBlobArchiveOptions.DEFAULT_CONNECTION_NAME, StringComparison.Ordinal)) {
        archive.ConnectionName = shared;
      }
      var reason = _inherit(options, archive.ServiceUri, archive.ConnectionString, archive.AccountSource,
        (uri, connectionString) => { archive.ServiceUri = uri; archive.ConnectionString = connectionString; });
      if (reason is null) {
        options.Archive = null;
        options.Notices.Add($"{AzureBlobStorageOptions.ARCHIVE_SECTION} is set, but no account is named for it, so the archive stays in " +
          $"the database driver's store. Set {AzureBlobStorageOptions.CONFIGURATION_SECTION}:ServiceUri, :ConnectionString or :ConnectionName.");
        options.ArchiveReason = $"{AzureBlobStorageOptions.ARCHIVE_SECTION} names no account";
      } else {
        options.ArchiveReason = $"azure-blob, account from {reason}";
      }
    }
    if (options.Offloads is { } offloads) {
      var reason = _inherit(options, offloads.ServiceUri, offloads.ConnectionString, offloads.AccountSource,
        (uri, connectionString) => { offloads.ServiceUri = uri; offloads.ConnectionString = connectionString; });
      if (reason is null) {
        options.Offloads = null;
        options.Notices.Add($"{AzureBlobStorageOptions.OFFLOADS_SECTION} is set, but no account is named for it, so bodies are not " +
          $"offloaded to Azure Blob Storage. Set {AzureBlobStorageOptions.CONFIGURATION_SECTION}:ServiceUri, :ConnectionString or :ConnectionName.");
      } else {
        options.OffloadReason = $"azure-blob, account from {reason}";
      }
    }
  }

  // The feature's account, or the shared one when it names none; null when there is none at all. Returns where it came
  // from, as the startup report says it.
  private static string? _inherit(
      AzureBlobStorageOptions options, Uri? uri, SensitiveString? connectionString, string? source, Action<Uri?, SensitiveString?> apply) {
    if (uri is not null || !string.IsNullOrWhiteSpace(connectionString?.Reveal())) {
      return source ?? "code";
    }
    if (options.ServiceUri is null && string.IsNullOrWhiteSpace(options.ConnectionString?.Reveal())) {
      return null;
    }
    apply(options.ServiceUri, options.ConnectionString);
    return options.AccountSource ?? "code";
  }

  /// <summary>
  /// The container client an account names: a connection string as given, or the service endpoint with the registered
  /// <see cref="TokenCredential"/>.
  /// </summary>
  /// <exception cref="InvalidOperationException">Neither or both of the endpoint and the connection string are set, or
  /// the endpoint is set and no credential is registered.</exception>
  internal static BlobContainerClient CreateContainerClient(
      Uri? serviceUri, SensitiveString? connectionString, string containerName, TokenCredential? credential, string feature) {
    var hasUri = serviceUri is not null;
    var hasConnectionString = !string.IsNullOrWhiteSpace(connectionString?.Reveal());
    if (hasUri == hasConnectionString) {
      throw new InvalidOperationException(
        "Set exactly one of ServiceUri (with a TokenCredential registered for its identity) or ConnectionString for the Azure Blob " +
        $"{feature}, in {AzureBlobStorageOptions.CONFIGURATION_SECTION} or its own section.");
    }
    if (hasConnectionString) {
      return new BlobContainerClient(connectionString!.Reveal(), containerName);
    }
    if (credential is null) {
      throw new InvalidOperationException(
        $"ServiceUri is set for the Azure Blob {feature}, so it authenticates with the Azure.Core.TokenCredential in the service " +
        "container, and none is registered. Register the credential for the service's identity (for example a managed identity credential).");
    }
    return new BlobServiceClient(serviceUri, credential).GetBlobContainerClient(containerName);
  }

  internal static InvalidOperationException TierRefused(string sectionName, string value, string refusal) =>
    new($"{sectionName}:{nameof(AzureBlobArchiveOptions.AccessTier)} is '{value}'. {refusal}");

  // Whether a section holds any key: read through its children, never its own value, so no section is read as a setting.
  private static bool _hasKeys(IConfiguration section) => section.GetChildren().Any();

  private static string _moved(string oldSection, string newKeys) =>
    $"{oldSection} is read for compatibility and will not be read in a later release; move it to {newKeys}. A key set in both places takes the new value.";

  private static Uri? _uri(IConfiguration section, string sectionName, string key) {
    var text = section[key];
    if (string.IsNullOrWhiteSpace(text)) {
      return null;
    }
    return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : throw _unreadable(sectionName, key, text);
  }

  private static string? _logicalName(IConfiguration section, string sectionName) {
    var name = section[CONNECTION_NAME];
    if (string.IsNullOrWhiteSpace(name)) {
      return null;
    }
    // Never echoed: a value that looks like a connection string may be one.
    return AzureBlobArchiveOptions.IsLogicalName(name)
      ? name
      : throw new InvalidOperationException(
        $"{sectionName}:{CONNECTION_NAME} is a logical name, not a connection string: it may not contain ';' or '='.");
  }

  private static int _int(IConfiguration section, string sectionName, string key, int current) {
    var text = section[key];
    if (string.IsNullOrWhiteSpace(text)) {
      return current;
    }
    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw _unreadable(sectionName, key, text);
  }

  private static InvalidOperationException _unreadable(string sectionName, string key, string value) =>
    new($"{sectionName}:{key} is '{value}', which is not a valid value.");
}
