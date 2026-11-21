// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Archival;
using Whizbang.Core.Configuration;
using Whizbang.Core.Offloads;

namespace Whizbang.Storage.AzureBlob;

/// <summary>
/// Registers Azure Blob Storage for Whizbang (#2502): message body offload and event archive objects, each active when its
/// configuration names an account. <c>AddWhizbang()</c> calls it for a host that references the package, so a reference
/// and configuration are all a host needs; call it yourself after opting out
/// (<see cref="WhizbangCoreOptions.AutoRegisterStorageProviders"/>), or to configure in code.
/// </summary>
/// <docs>fundamentals/storage/azure-blob#turnkey-activation</docs>
/// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobStorageTurnkeyTests.cs</tests>
public static class AzureBlobStorageServiceCollectionExtensions {
  /// <summary>
  /// Registers the package, configured from <c>Whizbang:Storage:AzureBlob</c> (and the keys of the earlier packages).
  /// Safe to call more than once, before or after <c>AddWhizbang()</c>: the package registers once.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobStorageTurnkeyTests.cs:Turnkey_APackageThatIsReferencedAndConfigured_ArchivesAndOffloadsThroughBlob_WithoutACallAsync</tests>
  public static IServiceCollection AddWhizbangAzureBlobStorage(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    if (services.Any(d => d.ServiceType == typeof(Registered))) {
      return services;
    }
    services.AddSingleton<Registered>();

    services.AddOptions<AzureBlobStorageOptions>().ValidateOnStart();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<AzureBlobStorageOptions>, BindFromConfiguration>(
      sp => new BindFromConfiguration(sp.GetService<IConfiguration>())));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<AzureBlobStorageOptions>, DecideFeatures>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AzureBlobStorageOptions>, RefuseWhatTheStoresCannotRun>());

    // The archive: an object store the database driver's coordinator writes through, when an account is named for it.
    services.TryAddWhizbangArchiveObjectStore(_archive);

    // Body offload: the send-side hook (a pass-through until a store is chosen), every store this package configures,
    // the selector set to the store Whizbang:Storage:AzureBlob:Offloads names, and the cipher Whizbang:BodyOffload names.
    services.AddWhizbangBodyOffload();
    services.AddWhizbangBodyCipherFromConfiguration();
#pragma warning disable S8969 // The service key is object? on .NET 10 and object on .NET 11 (#2135).
    services.AddKeyedSingleton<IMessageBodyStore>(KeyedService.AnyKey, (sp, key) => _offloadStore(sp, key as string)!);
#pragma warning restore S8969
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<MessageBodyOffloadOptions>, SelectTheOffloadStore>());
    services.AddSingleton(sp => {
      var options = sp.GetRequiredService<IOptions<AzureBlobStorageOptions>>().Value;
      return new StorageFeatureActivation(StorageFeatureActivation.OFFLOAD, options.Offloads?.ProviderName ?? string.Empty, options.OffloadReason);
    });

    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AzureBlobStorageStartupNotices>());
    return services;
  }

  /// <summary>
  /// Registers the package configured from <c>Whizbang:Storage:AzureBlob</c>, then by <paramref name="configure"/>,
  /// whose values win over configuration's. Set <see cref="AzureBlobStorageOptions.Archive"/> or
  /// <see cref="AzureBlobStorageOptions.Offloads"/> to turn a feature on in code.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configure">Sets the account and the features.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobStorageTurnkeyTests.cs:ExplicitRegistration_WinsOverConfiguration_BeforeOrAfterAddWhizbangAsync</tests>
  public static IServiceCollection AddWhizbangAzureBlobStorage(this IServiceCollection services, Action<AzureBlobStorageOptions> configure) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configure);
    services.AddWhizbangAzureBlobStorage();
    services.Configure(configure);
    return services;
  }

  /// <summary>
  /// Registers the package configured from <paramref name="configuration"/>, read over the host's own configuration: for a
  /// host that binds a configuration it builds itself rather than the one in its container.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configuration">The configuration to read <c>Whizbang:Storage:AzureBlob</c> (and the earlier keys) from.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobStorageConfigurationTests.cs:AGivenConfiguration_IsReadOverTheHostsOwnAsync</tests>
  public static IServiceCollection AddWhizbangAzureBlobStorage(this IServiceCollection services, IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);
    services.AddWhizbangAzureBlobStorage();
    services.AddSingleton<IConfigureOptions<AzureBlobStorageOptions>>(new BindFromGivenConfiguration(configuration));
    return services;
  }

  private static ArchiveObjectStoreSelection _archive(IServiceProvider services) {
    var options = services.GetRequiredService<IOptions<AzureBlobStorageOptions>>().Value;
    if (options.Archive is not { } archive) {
      return new ArchiveObjectStoreSelection(null, options.ArchiveReason);
    }
    var container = AzureBlobStorageConfiguration.CreateContainerClient(
      archive.ServiceUri, archive.ConnectionString, archive.ContainerName, services.GetService<TokenCredential>(), "archive");
    return new ArchiveObjectStoreSelection(new AzureBlobArchiveObjectStore(container, archive), options.ArchiveReason);
  }

  // The store a claim's provider name resolves to: the one Whizbang:Storage:AzureBlob:Offloads configures, or one of the
  // earlier package's named stores; any other name resolves to nothing, so stores registered by name in code are found
  // first and a name nobody registered still reads as missing.
  private static AzureBlobMessageBodyStore? _offloadStore(IServiceProvider services, string? providerName) {
    if (providerName is null) {
      return null;
    }
    var options = services.GetRequiredService<IOptions<AzureBlobStorageOptions>>().Value;
    if (options.Offloads is { } offloads && string.Equals(offloads.ProviderName, providerName, StringComparison.Ordinal)) {
      return new AzureBlobMessageBodyStore(providerName, offloads, AzureBlobStorageConfiguration.CreateContainerClient(
        offloads.ServiceUri, offloads.ConnectionString, offloads.ContainerName, services.GetService<TokenCredential>(), "body store"));
    }
    if (!options.NamedOffloads.TryGetValue(providerName, out var named)) {
      return null;
    }
    if (string.IsNullOrWhiteSpace(named.ConnectionString?.Reveal())) {
      throw new InvalidOperationException(
        $"{AzureBlobStorageOptions.LEGACY_OFFLOADS_SECTION}:{providerName}:ConnectionString is required for body store '{providerName}'.");
    }
    return new AzureBlobMessageBodyStore(providerName, named,
      AzureBlobStorageConfiguration.CreateContainerClient(null, named.ConnectionString, named.ContainerName, credential: null, "body store"));
  }

  /// <summary>Marks the package registered, so a second registration adds nothing.</summary>
  private sealed class Registered {
    /// <summary>The provider the marker stands for, as the turnkey registers it.</summary>
    public string Provider { get; } = AzureBlobArchiveObjectStore.PROVIDER_NAME;
  }

  private sealed class BindFromConfiguration(IConfiguration? configuration) : IConfigureOptions<AzureBlobStorageOptions> {
    public void Configure(AzureBlobStorageOptions options) {
      if (configuration is not null) {
        AzureBlobStorageConfiguration.Bind(configuration, options);
      }
    }
  }

  private sealed class BindFromGivenConfiguration(IConfiguration configuration) : IConfigureOptions<AzureBlobStorageOptions> {
    public void Configure(AzureBlobStorageOptions options) => AzureBlobStorageConfiguration.Bind(configuration, options);
  }

  private sealed class DecideFeatures : IPostConfigureOptions<AzureBlobStorageOptions> {
    public void PostConfigure(string? name, AzureBlobStorageOptions options) => AzureBlobStorageConfiguration.Complete(options);
  }

  // Refuses, when the host starts, settings the stores cannot run with, set in configuration or in code.
  private sealed class RefuseWhatTheStoresCannotRun : IValidateOptions<AzureBlobStorageOptions> {
    public ValidateOptionsResult Validate(string? name, AzureBlobStorageOptions options) {
      if (options.Archive is not { } archive) {
        return ValidateOptionsResult.Success;
      }
      if (AzureBlobArchiveOptions.RefusalFor(archive.AccessTier) is { } refusal) {
        return ValidateOptionsResult.Fail(AzureBlobStorageConfiguration.TierRefused(AzureBlobStorageOptions.ARCHIVE_SECTION, $"{archive.AccessTier}", refusal).Message);
      }
      return archive.ToSettings().Refusal() is { } settings
        ? ValidateOptionsResult.Fail($"{AzureBlobStorageOptions.ARCHIVE_SECTION}: {settings}")
        : ValidateOptionsResult.Success;
    }
  }

  // The store Whizbang:Storage:AzureBlob:Offloads configures is the one bodies are offloaded to: it wins over
  // Whizbang:BodyOffload:ProviderName, which still chooses among the earlier package's named stores.
  private sealed class SelectTheOffloadStore(IOptions<AzureBlobStorageOptions> storage) : IPostConfigureOptions<MessageBodyOffloadOptions> {
    public void PostConfigure(string? name, MessageBodyOffloadOptions options) {
      if (storage.Value.Offloads is { } offloads) {
        options.ProviderName = offloads.ProviderName;
      }
    }
  }
}

/// <summary>
/// Logs, once at start, every warning binding the package's configuration gave (#2502): a key of an earlier package that
/// is still read (naming its new key), or a feature configured with no account.
/// </summary>
/// <param name="options">The package's options; reading them binds and validates them.</param>
/// <param name="logger">The logger.</param>
/// <docs>fundamentals/storage/azure-blob#migrating-from-the-earlier-packages</docs>
/// <tests>tests/Whizbang.Storage.AzureBlob.Integration.Tests/AzureBlobStorageStartupNoticesTests.cs:TheStartupNotices_AreLoggedOnce_EachAsAWarningAsync</tests>
internal sealed partial class AzureBlobStorageStartupNotices(IOptions<AzureBlobStorageOptions> options, ILogger<AzureBlobStorageStartupNotices> logger)
    : IHostedService {
  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    foreach (var notice in options.Value.Notices.Distinct(StringComparer.Ordinal)) {
      LogNotice(logger, notice);
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Azure Blob storage: {Notice}")]
  static partial void LogNotice(ILogger logger, string notice);
}
