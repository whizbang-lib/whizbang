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
  /// in the host's secret store. Set this or <see cref="ServiceUri"/>, not both. Held as a
  /// <see cref="Whizbang.Core.Configuration.SensitiveString"/>, so writing the options out writes its redaction.
  /// </summary>
  public Whizbang.Core.Configuration.SensitiveString? ConnectionString { get; set; }

  /// <summary>The container that holds the archive objects; created on the first write when it does not exist.</summary>
  public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;

  /// <summary>
  /// The logical name of the storage connection, recorded with the account endpoint and the container as the archive's
  /// location (#2137). A name, never a connection string: a value with <c>;</c> or <c>=</c> is refused.
  /// </summary>
  /// <remarks>
  /// Every archive record names its location, and by default the store refuses to read, restore, delete or write through
  /// a record whose location is not the configured one (<see cref="LocationMismatch"/>). The name is compared without
  /// surrounding whitespace and without case. Give each
  /// environment that shares a storage account its own name or container, so a database copied between them is refused
  /// rather than reaching the other's objects.
  /// </remarks>
  /// <docs>fundamentals/events/archive-store#archive-locations</docs>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveLocationTests.cs:ConnectionName_DefaultsToDefault_AndIsBoundFromConfigurationAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveLocationTests.cs:ConnectionName_ThatLooksLikeAConnectionString_IsRefused_WhenBoundAndWhenBuiltAsync</tests>
  public string ConnectionName { get; set; } = DEFAULT_CONNECTION_NAME;

  /// <summary>
  /// What the store does when an archive record's location is not the configured one (#2141): <c>Error</c> (the
  /// default), <c>Warning</c>, <c>Info</c> or <c>Ignore</c>, bound from <c>Whizbang:Archive:AzureBlob:LocationMismatch</c>
  /// in any case.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The database stores the location each record was written to, normalized (account endpoint, connection name,
  /// container; see <see cref="Whizbang.Core.Archival.ArchiveLocation.Normalize"/>), and infrastructure controls where the
  /// objects actually are. After a restore, a storage failover or a copy of the database to another environment the
  /// records still name the old location while the configuration names the new one.
  /// </para>
  /// <para>
  /// <c>Error</c> refuses the read, restore, delete or write with
  /// <see cref="Whizbang.Core.Archival.ArchiveLocationMismatchException"/> before any request reaches storage, so the
  /// archive stops until the operator repoints the location (<see cref="AzureBlobArchiveStore.RepointLocationAsync"/>) or
  /// corrects the configuration, and the deletion queue deletes only objects queued in the configured location.
  /// <c>Warning</c> and <c>Info</c> go ahead in the configured location and log each mismatch at that level, and the
  /// deletion queue deletes every queued object from the configured container. <c>Ignore</c> does the same without
  /// comparing or logging. What every handling but <c>Error</c> gives up is the safety net against a configuration pointed
  /// at the wrong account or container reading objects that are not this database's, or deleting another environment's
  /// objects at the same paths. Location rows are written whatever the handling, so it can be set back to <c>Error</c>.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/events/archive-store#archive-locations</docs>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveLocationTests.cs:LocationMismatch_DefaultsToError_SoAMismatchStopsTheArchiveAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreLocationMismatchTests.cs:Move_ARecordPinnedToTheOldLocation_IsHandledAsConfiguredAsync</tests>
  public Whizbang.Core.Archival.ArchiveLocationMismatchHandling LocationMismatch { get; set; }

  /// <summary>Why the store cannot run with <paramref name="handling"/>, or <see langword="null"/> when it is a defined handling.</summary>
  internal static string? RefusalForLocationMismatch(Whizbang.Core.Archival.ArchiveLocationMismatchHandling handling) =>
    Enum.IsDefined(handling) ? null : LOCATION_MISMATCH_VALUES;

  /// <summary>The handlings a location mismatch can have.</summary>
  internal const string LOCATION_MISMATCH_VALUES = "LocationMismatch is Error, Warning, Info or Ignore.";

  /// <summary>The handling <paramref name="value"/> names, in any case and with surrounding whitespace, or <see langword="null"/>.</summary>
  internal static Whizbang.Core.Archival.ArchiveLocationMismatchHandling? LocationMismatchNamed(string value) =>
    value.Trim().ToLowerInvariant() switch {
      "error" => Whizbang.Core.Archival.ArchiveLocationMismatchHandling.Error,
      "warning" => Whizbang.Core.Archival.ArchiveLocationMismatchHandling.Warning,
      "info" => Whizbang.Core.Archival.ArchiveLocationMismatchHandling.Info,
      "ignore" => Whizbang.Core.Archival.ArchiveLocationMismatchHandling.Ignore,
      _ => null,
    };

  /// <summary>
  /// How long a queued object deletion waits before the object is deleted from storage, for storage that keeps nothing
  /// after a delete (no soft delete, no versioning). <see langword="null"/> (the default) deletes at once, as before.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A restore, a delete and a purge stop naming an object in the same database transaction and queue it for deletion
  /// (<c>wh_archive_object_deletions</c>). With a delay, the object is deleted no sooner than this long after it was
  /// queued, so a database restored to any point within the delay still finds every object it names. Set it to at least
  /// the database's point-in-time restore window. Azure Blob Storage with soft delete does not need it: the account keeps
  /// a deleted object for its retention period instead.
  /// </para>
  /// <para>
  /// With a delay, the maintenance step also sweeps the container once a day for objects no manifest row and no queued
  /// deletion names that are older than the delay (an object written after the point a database was restored to, or an
  /// upload whose transaction never committed), and deletes them. It is the only operation that lists the container, and
  /// it assumes the container holds this database's archive alone. Data a purge removed stays in storage for the delay,
  /// as it does in the database's own backups; crypto-shredding is the immediate erasure.
  /// </para>
  /// <para>
  /// The age is measured on the service's clock (<see cref="TimeProvider"/>) against the time the database queued the
  /// deletion and the time storage created the object; a delay of days makes any skew between them immaterial. Turning
  /// the delay on applies to deletions already queued; turning it off lets every queued deletion run at once.
  /// </para>
  /// </remarks>
  /// <docs>operations/deployment/restoring-an-environment-with-an-archive#a-deletion-delay</docs>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveRegistrationTests.cs:Configuration_DeleteAfter_IsOffByDefault_AndBindsATimeSpanAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreDeleteAfterTests.cs</tests>
  public TimeSpan? DeleteAfter { get; set; }

  /// <summary>
  /// Why the store cannot run with <paramref name="deleteAfter"/>, or <see langword="null"/> when it can: unset, or a time
  /// span above zero.
  /// </summary>
  internal static string? RefusalForDeleteAfter(TimeSpan? deleteAfter) =>
    deleteAfter is { } delay && delay <= TimeSpan.Zero ? DELETE_AFTER_REFUSAL : null;

  /// <summary>Why a deletion delay of zero or less, or one that is not a time span, is refused.</summary>
  internal const string DELETE_AFTER_REFUSAL =
    "DeleteAfter is a time span above zero, such as 35.00:00:00, or unset to delete at once: a zero delay would look like protection and give none.";

  /// <summary>
  /// Whether <paramref name="name"/> can be a logical connection name: not empty or whitespace (it would normalize to
  /// nothing), without <c>;</c> or <c>=</c>.
  /// </summary>
  internal static bool IsLogicalName(string? name) =>
    !string.IsNullOrWhiteSpace(name) && name.IndexOfAny([';', '=']) < 0;

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
