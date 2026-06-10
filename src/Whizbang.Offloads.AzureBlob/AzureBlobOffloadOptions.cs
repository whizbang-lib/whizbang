using Azure.Storage.Blobs.Models;

namespace Whizbang.Offloads.AzureBlob;

/// <summary>
/// Per-provider configuration for the Azure Blob Storage body-store. One
/// instance is bound per registered provider name (via
/// <see cref="AzureBlobOffloadServiceCollectionExtensions.AddWhizbangAzureBlobOffload"/>).
/// </summary>
/// <docs>fundamentals/offloads/providers/azure-blob</docs>
public sealed class AzureBlobOffloadOptions {
  /// <summary>
  /// Azure Storage connection string. Distinguishes emulator from live by
  /// the standard SDK convention — <c>UseDevelopmentStorage=true</c> or an
  /// explicit emulator endpoint (Azurite default
  /// <c>http://127.0.0.1:10000/devstoreaccount1</c>) targets the emulator;
  /// a real <c>DefaultEndpointsProtocol=https;AccountName=...</c> targets
  /// the live service. The provider behaves identically against either.
  /// </summary>
  public string? ConnectionString { get; set; }

  /// <summary>
  /// Container that holds the offloaded bodies. The provider lazily
  /// creates the container on first upload if it doesn't exist (the
  /// emulator and a fresh live account both start without it).
  /// </summary>
  public string ContainerName { get; set; } = "whizbang-offload-bodies";

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
