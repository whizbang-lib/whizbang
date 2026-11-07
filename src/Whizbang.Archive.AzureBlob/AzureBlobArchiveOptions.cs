// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Storage.Blobs.Models;

namespace Whizbang.Archive.AzureBlob;

/// <summary>
/// Settings for the Azure Blob archive store, bound from <c>Whizbang:Archive:AzureBlob</c>
/// (<see cref="AzureBlobArchiveServiceCollectionExtensions.CONFIGURATION_SECTION"/>).
/// </summary>
/// <remarks>
/// Name the account with exactly one of <see cref="ServiceUri"/> (production: the identity comes from the
/// <c>Azure.Core.TokenCredential</c> the host registers, so no secret is in configuration) or
/// <see cref="ConnectionString"/> (the Azurite emulator, or an account key the host keeps in its own secret store).
/// </remarks>
/// <docs>fundamentals/events/archive-store#azure-blob-configuration</docs>
/// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs</tests>
public sealed class AzureBlobArchiveOptions {
  /// <summary>The logical connection name used when none is configured.</summary>
  public const string DEFAULT_CONNECTION_NAME = "default";

  /// <summary>The container used when none is configured.</summary>
  public const string DEFAULT_CONTAINER_NAME = "whizbang-archive";

  /// <summary>
  /// The Brotli quality used when none is configured. Measured on 10,000 event rows (13.3 MB of JSON lines): quality 4
  /// wrote 1.90 MB in about 0.25 s, qualities 5 to 9 saved under 1% more for two to seven times the time, and 11 saved
  /// 15% more for 38 s per chunk.
  /// </summary>
  public const int DEFAULT_COMPRESSION_LEVEL = 4;

  /// <summary>The most events one archive object holds when none is configured.</summary>
  public const int DEFAULT_MAX_EVENTS_PER_CHUNK = 10_000;

  /// <summary>The lowest Brotli quality.</summary>
  public const int MIN_COMPRESSION_LEVEL = 0;

  /// <summary>The highest Brotli quality.</summary>
  public const int MAX_COMPRESSION_LEVEL = 11;

  /// <summary>
  /// The Blob service endpoint (for example <c>https://account.blob.core.windows.net</c>). The store authenticates with
  /// the <c>Azure.Core.TokenCredential</c> registered in the container (managed identity, workload identity, or any
  /// credential the host chooses).
  /// </summary>
  public Uri? ServiceUri { get; set; }

  /// <summary>
  /// A storage connection string, for the Azurite emulator (<c>UseDevelopmentStorage=true</c>) or an account key kept
  /// in the host's secret store. Set this or <see cref="ServiceUri"/>, not both.
  /// </summary>
  public string? ConnectionString { get; set; }

  /// <summary>The container that holds the archive objects; created on the first write when it does not exist.</summary>
  public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;

  /// <summary>
  /// The logical name of the storage connection, recorded with the account endpoint and the container as the archive's
  /// location (#2137). A name, never a connection string: a value with <c>;</c> or <c>=</c> is refused.
  /// </summary>
  /// <remarks>
  /// Every archive record names its location, and the store refuses to read, restore or delete a record whose location
  /// is not the configured one (<see cref="Whizbang.Core.Archival.ArchiveLocationMismatchException"/>). Give each
  /// environment that shares a storage account its own name or container, so a database copied between them is refused
  /// rather than reaching the other's objects.
  /// </remarks>
  /// <docs>fundamentals/events/archive-store#archive-locations</docs>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveLocationTests.cs:ConnectionName_DefaultsToDefault_AndIsBoundFromConfigurationAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveLocationTests.cs:ConnectionName_ThatLooksLikeAConnectionString_IsRefused_WhenBoundAndWhenBuiltAsync</tests>
  public string ConnectionName { get; set; } = DEFAULT_CONNECTION_NAME;

  /// <summary>Whether <paramref name="name"/> can be a logical connection name: not empty, without <c>;</c> or <c>=</c>.</summary>
  internal static bool IsLogicalName(string? name) =>
    !string.IsNullOrEmpty(name) && name.IndexOfAny([';', '=']) < 0;

  /// <summary>The Brotli quality, 0 (fastest) to 11 (smallest). See <see cref="DEFAULT_COMPRESSION_LEVEL"/>.</summary>
  public int CompressionLevel { get; set; } = DEFAULT_COMPRESSION_LEVEL;

  /// <summary>
  /// The most events one archive object holds. A larger stream is written as several objects, so neither a write nor a
  /// read ever holds more than one object's events in memory.
  /// </summary>
  public int MaxEventsPerChunk { get; set; } = DEFAULT_MAX_EVENTS_PER_CHUNK;

  /// <summary>
  /// The access tier every archive object is written at: <see cref="Azure.Storage.Blobs.Models.AccessTier.Hot"/>,
  /// <see cref="Azure.Storage.Blobs.Models.AccessTier.Cool"/> or <see cref="Azure.Storage.Blobs.Models.AccessTier.Cold"/>.
  /// <see langword="null"/> (the default) leaves the storage account's default tier.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Archives are written once and rarely read, so a colder tier stores them for less: Cool and Cold cost less per
  /// gigabyte than Hot and more per read, and charge for an object deleted before 30 (Cool) or 90 (Cold) days. A restore
  /// and a purge delete objects, so a stream restored soon after it was archived pays that early-deletion charge.
  /// </para>
  /// <para>
  /// Archive is refused, at startup, when the setting is bound and when the store is built: an object in the Archive
  /// tier cannot be read until it is rehydrated, which takes hours, so a read-through restore or a rebuild that reads the
  /// archive would fail. Move objects to Archive with a storage lifecycle rule only where those reads may wait.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/events/archive-store#access-tier</docs>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Configuration_AccessTier_BindsHotCoolOrCold_InAnyCaseAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Write_WithAnAccessTier_StoresEveryObjectAtIt_WithoutOne_TheAccountDefaultAsync</tests>
  public AccessTier? AccessTier { get; set; }

  /// <summary>
  /// Why the store cannot write at <paramref name="tier"/>, or <see langword="null"/> when it can: no tier (the account
  /// default), Hot, Cool or Cold.
  /// </summary>
  /// <param name="tier">The configured tier.</param>
  /// <returns>The refusal, or <see langword="null"/>.</returns>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Store_RefusesATierItCannotReadBackFromAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Store_AcceptsHotCoolColdOrTheAccountDefaultAsync</tests>
  internal static string? RefusalFor(AccessTier? tier) {
    if (tier is not { } value || Online(value) is not null) {
      return null;
    }
    return value == Azure.Storage.Blobs.Models.AccessTier.Archive
      ? "An object in the Archive tier cannot be read until it is rehydrated, which takes hours, so a read-through " +
        "restore or a rebuild that reads the archive would fail. Write at Hot, Cool or Cold, and move objects to Archive " +
        "with a storage lifecycle rule only where those reads may wait."
      : "Archive objects are written at Hot, Cool or Cold.";
  }

  /// <summary>The SDK's own Hot, Cool or Cold value equal to <paramref name="tier"/> (in any case), or <see langword="null"/>.</summary>
  internal static AccessTier? Online(AccessTier tier) {
    foreach (var online in _onlineTiers) {
      if (tier == online) {
        return online;
      }
    }
    return null;
  }

  // The tiers an object can be read from as soon as it is written.
  private static readonly AccessTier[] _onlineTiers = [
    Azure.Storage.Blobs.Models.AccessTier.Hot, Azure.Storage.Blobs.Models.AccessTier.Cool, Azure.Storage.Blobs.Models.AccessTier.Cold,
  ];
}
