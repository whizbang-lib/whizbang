// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Storage.Blobs.Models;

namespace Whizbang.Storage.AzureBlob;

/// <summary>
/// Configuration for the Azure Blob Storage body store: bound from <c>Whizbang:Storage:AzureBlob:Offloads</c> with the
/// account from <c>Whizbang:Storage:AzureBlob</c> (#2502), or, from the earlier package's keys, one instance per provider
/// name under <c>Whizbang:Offloads:AzureBlob:&lt;providerName&gt;</c>.
/// </summary>
/// <docs>fundamentals/offloads/providers/azure-blob</docs>
/// <docs>fundamentals/storage/azure-blob#configuration</docs>
/// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobStorageConfigurationTests.cs</tests>
public sealed class AzureBlobOffloadOptions {
  /// <summary>The provider name the body store registers under when none is configured.</summary>
  public const string DEFAULT_PROVIDER_NAME = "azure-blob";

  /// <summary>The container used when none is configured.</summary>
  public const string DEFAULT_CONTAINER_NAME = "whizbang-offload-bodies";

  /// <summary>
  /// The provider name the store registers under and every claim it writes records (<c>MessageBodyClaim.ProviderName</c>),
  /// for the store <c>Whizbang:Storage:AzureBlob:Offloads</c> configures. Keep it stable: a receiver finds the store by the
  /// name the sender recorded. The stores of the earlier package's keys are named by their section instead.
  /// </summary>
  public string ProviderName { get; set; } = DEFAULT_PROVIDER_NAME;

  /// <summary>
  /// The Blob service endpoint (for example <c>https://account.blob.core.windows.net</c>), authenticated with the
  /// <c>Azure.Core.TokenCredential</c> registered in the container. Set this or <see cref="ConnectionString"/>. Read for
  /// the store <c>Whizbang:Storage:AzureBlob</c> configures.
  /// </summary>
  public Uri? ServiceUri { get; set; }

  /// <summary>The key that named this feature's own account, or <see langword="null"/> when none did.</summary>
  internal string? AccountSource { get; set; }

  /// <summary>
  /// Azure Storage connection string. Distinguishes emulator from live by
  /// the standard SDK convention — <c>UseDevelopmentStorage=true</c> or an
  /// explicit emulator endpoint (Azurite default
  /// <c>http://127.0.0.1:10000/devstoreaccount1</c>) targets the emulator;
  /// a real <c>DefaultEndpointsProtocol=https;AccountName=...</c> targets
  /// the live service. The provider behaves identically against either. Held as a
  /// <see cref="Whizbang.Core.Configuration.SensitiveString"/>, so writing the options out writes its redaction.
  /// </summary>
  public Whizbang.Core.Configuration.SensitiveString? ConnectionString { get; set; }

  /// <summary>
  /// Container that holds the offloaded bodies. The provider lazily
  /// creates the container on first upload if it doesn't exist (the
  /// emulator and a fresh live account both start without it).
  /// </summary>
  public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;

  /// <summary>
  /// Optional Azure Blob access tier for uploads — Hot, Cool, Cold, or
  /// Archive. Null leaves the storage account's default. Archive bodies
  /// are NOT downloadable without a rehydration step, so only set this
  /// for long-cold-storage use cases that don't expect receive-time
  /// rehydrate; the offload pipeline assumes Hot/Cool by default.
  /// </summary>
  public AccessTier? DefaultAccessTier { get; set; }

  /// <summary>
  /// Defensive cap on download size, in bytes. When non-null and a claim
  /// reports a larger body, the provider refuses to download — protects
  /// receivers from a tampered claim ticket that would otherwise pull a
  /// multi-GB blob. Null = no cap (trust claim.Size).
  /// </summary>
  public long? MaxDownloadBytes { get; set; }
}
