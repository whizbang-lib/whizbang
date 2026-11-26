// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// How the archive coordinator writes through an <see cref="IArchiveObjectStore"/> (#2503): the Brotli quality, the most
/// events one object holds, what a record in another location does, and the deletion delay. A provider builds it from
/// its own configuration section, so the archive is configured in one place.
/// </summary>
/// <docs>fundamentals/events/archive-store#object-stores-and-the-coordinator</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs</tests>
public sealed record ArchiveObjectStoreSettings {
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

  /// <summary>Why a deletion delay of zero or less is refused.</summary>
  public const string DELETE_AFTER_REFUSAL =
    "DeleteAfter is a time span above zero, such as 35.00:00:00, or unset to delete at once: a zero delay would look like protection and give none.";

  /// <summary>The Brotli quality, 0 (fastest) to 11 (smallest). See <see cref="DEFAULT_COMPRESSION_LEVEL"/>.</summary>
  public int CompressionLevel { get; init; } = DEFAULT_COMPRESSION_LEVEL;

  /// <summary>
  /// The most events one archive object holds. A larger stream is written as several objects, so neither a write nor a
  /// read ever holds more than one object's events in memory.
  /// </summary>
  public int MaxEventsPerChunk { get; init; } = DEFAULT_MAX_EVENTS_PER_CHUNK;

  /// <summary>
  /// What the coordinator does when an archive record's location is not the store's location (#2141): refuse with
  /// <see cref="ArchiveLocationMismatchException"/> (<see cref="ArchiveLocationMismatchHandling.Error"/>, the default),
  /// go ahead in the store's location and log at Warning or Info, or go ahead without comparing (Ignore).
  /// </summary>
  /// <docs>fundamentals/events/archive-store#location-mismatch</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs:Settings_RefuseWhatTheCoordinatorCannotRunWith_AndAcceptTheBoundariesAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreLocationMismatchTests.cs:Move_ARecordPinnedToTheOldLocation_IsHandledAsConfiguredAsync</tests>
  public ArchiveLocationMismatchHandling LocationMismatch { get; init; }

  /// <summary>Why an undefined location-mismatch handling is refused.</summary>
  public const string LOCATION_MISMATCH_VALUES = "LocationMismatch is Error, Warning, Info or Ignore.";

  /// <summary>
  /// How long a queued object deletion waits before the object is deleted, for storage that keeps nothing after a delete.
  /// <see langword="null"/> (the default) deletes at once. With a delay, the orphan sweep runs too (#2367).
  /// </summary>
  /// <docs>operations/deployment/restoring-an-environment-with-an-archive#a-deletion-delay</docs>
  public TimeSpan? DeleteAfter { get; init; }

  /// <summary>
  /// Why a coordinator cannot run with these settings, or <see langword="null"/> when it can.
  /// </summary>
  /// <returns>The first refusal, naming the setting.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs:Settings_RefuseWhatTheCoordinatorCannotRunWith_AndAcceptTheBoundariesAsync</tests>
  public string? Refusal() {
    if (CompressionLevel is < MIN_COMPRESSION_LEVEL or > MAX_COMPRESSION_LEVEL) {
      return $"CompressionLevel is {CompressionLevel}; it is a Brotli quality, {MIN_COMPRESSION_LEVEL} to {MAX_COMPRESSION_LEVEL}.";
    }
    if (MaxEventsPerChunk < 1) {
      return $"MaxEventsPerChunk is {MaxEventsPerChunk}; it is 1 or more.";
    }
    return RefusalForLocationMismatch(LocationMismatch) ?? RefusalForDeleteAfter(DeleteAfter);
  }

  /// <summary>Why <paramref name="handling"/> is refused, or <see langword="null"/> when it is a defined handling.</summary>
  /// <param name="handling">The location-mismatch handling.</param>
  /// <returns>The refusal, or <see langword="null"/>.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs:Settings_RefuseWhatTheCoordinatorCannotRunWith_AndAcceptTheBoundariesAsync</tests>
  public static string? RefusalForLocationMismatch(ArchiveLocationMismatchHandling handling) =>
    Enum.IsDefined(handling) ? null : LOCATION_MISMATCH_VALUES;

  /// <summary>Why <paramref name="deleteAfter"/> is refused, or <see langword="null"/>: unset, or a time span above zero.</summary>
  /// <param name="deleteAfter">The deletion delay.</param>
  /// <returns>The refusal, or <see langword="null"/>.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs:Settings_RefuseWhatTheCoordinatorCannotRunWith_AndAcceptTheBoundariesAsync</tests>
  public static string? RefusalForDeleteAfter(TimeSpan? deleteAfter) =>
    deleteAfter is { } delay && delay <= TimeSpan.Zero ? DELETE_AFTER_REFUSAL : null;
}
