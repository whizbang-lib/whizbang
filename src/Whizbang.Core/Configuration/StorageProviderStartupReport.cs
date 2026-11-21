// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Archival;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Configuration;

/// <summary>
/// Why a storage feature uses the provider it does (#2502), as a storage provider package records it when its
/// configuration activates the feature: the startup report writes it beside the provider.
/// </summary>
/// <param name="Feature">The feature: <see cref="ARCHIVE"/> or <see cref="OFFLOAD"/>.</param>
/// <param name="Provider">The provider the feature uses: for body offload, the store's provider name.</param>
/// <param name="Reason">Why, for example the configuration key that named the account.</param>
/// <docs>fundamentals/storage/azure-blob#startup-report</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Configuration/StorageProviderTurnkeyTests.cs</tests>
public sealed record StorageFeatureActivation(string Feature, string Provider, string Reason) {
  /// <summary>The event archive.</summary>
  public const string ARCHIVE = "archive";

  /// <summary>Message body offload.</summary>
  public const string OFFLOAD = "offload";
}

/// <summary>
/// Says at start which provider each storage feature uses and why (#2502): the archive store (the location of an object
/// store the driver's coordinator writes through, or the driver's built-in store) and the body offload store. Warns when
/// configuration names a storage account for a provider package that is not loaded, naming the package, since that
/// configuration then does nothing.
/// </summary>
/// <param name="services">The host's services.</param>
/// <docs>fundamentals/storage/azure-blob#startup-report</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Configuration/StorageProviderTurnkeyTests.cs</tests>
internal sealed partial class StorageProviderStartupReport(IServiceProvider services) : IHostedService {
  /// <summary>The storage provider packages the framework knows, with every key that names an account for each.</summary>
  internal static readonly IReadOnlyList<(string Provider, string Package, string[] AccountKeys)> KnownPackages = [
    ("azure-blob", "SoftwareExtravaganza.Whizbang.Storage.AzureBlob", [
      "Whizbang:Storage:AzureBlob:ConnectionString",
      "Whizbang:Storage:AzureBlob:ServiceUri",
      "Whizbang:Storage:AzureBlob:ConnectionName",
      "Whizbang:Archive:AzureBlob:ConnectionString",
      "Whizbang:Archive:AzureBlob:ServiceUri",
      "Whizbang:Offloads:AzureBlob:*:ConnectionString",
    ]),
  ];

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    var logger = (ILogger?)services.GetService<ILogger<StorageProviderStartupReport>>() ?? NullLogger.Instance;
    var configuration = services.GetService<IConfiguration>();
    var loaded = ServiceRegistrationCallbacks.StorageProviders;
    foreach (var (provider, package, keys) in KnownPackages) {
      if (!loaded.Contains(provider) && NamedAccount(configuration, keys) is { } key) {
        LogPackageNotLoaded(logger, key, package);
      }
    }

    // Read from the selection, never by building the driver's archive store: the selection is built here, so an object
    // store whose configuration cannot work fails the start rather than the first archive.
    var selection = services.GetService<ArchiveObjectStoreSelection>();
    if (selection?.Store is { } objects) {
      LogArchive(logger, objects.Location, selection.Reason);
    } else if (services.GetService<IServiceProviderIsService>()?.IsService(typeof(IArchiveStore)) == true) {
      LogArchiveBuiltIn(logger, selection?.Reason ?? "no archive object store is configured");
    }

    var offload = services.GetService<IOptions<MessageBodyOffloadOptions>>()?.Value.ProviderName;
    if (!string.IsNullOrWhiteSpace(offload)) {
      var store = services.GetKeyedService<IMessageBodyStore>(offload);
      var storeName = store is null ? "no store is registered under that name" : TypeNameFormatter.ForLog(store.GetType());
      var reason = services.GetServices<StorageFeatureActivation>()
        .FirstOrDefault(a => a.Feature == StorageFeatureActivation.OFFLOAD && a.Provider == offload)?.Reason ?? "registered in code";
      LogOffload(logger, offload, storeName, reason);
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <summary>
  /// The first of <paramref name="keys"/> that holds a value, a <c>*</c> segment matching any child, or
  /// <see langword="null"/>.
  /// </summary>
  internal static string? NamedAccount(IConfiguration? configuration, IEnumerable<string> keys) {
    if (configuration is null) {
      return null;
    }
    foreach (var key in keys) {
      var star = key.IndexOf(":*:", StringComparison.Ordinal);
      if (star < 0) {
        if (!string.IsNullOrWhiteSpace(configuration[key])) {
          return key;
        }
        continue;
      }
      var leaf = key[(star + 3)..];
      if (configuration.GetSection(key[..star]).GetChildren().FirstOrDefault(child => !string.IsNullOrWhiteSpace(child[leaf])) is { } named) {
        return $"{named.Path}:{leaf}";
      }
    }
    return null;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "{Key} names a storage account, but the package {Package} is not loaded, so the setting does nothing. Reference the package (it activates from this configuration), or remove the setting")]
  static partial void LogPackageNotLoaded(ILogger logger, string key, string package);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Archive store: {Store} ({Reason})")]
  static partial void LogArchive(ILogger logger, ArchiveLocation store, string reason);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Archive store: the database driver's built-in store ({Reason})")]
  static partial void LogArchiveBuiltIn(ILogger logger, string reason);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Body offload store: provider '{Provider}', {Store} ({Reason})")]
  static partial void LogOffload(ILogger logger, string provider, string store, string reason);
}
