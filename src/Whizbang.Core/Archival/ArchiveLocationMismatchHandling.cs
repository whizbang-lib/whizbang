// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// What an archive store does when a record's location is not the one its configuration names (#2141): the database
/// stores the location each record was written to (normalized, <see cref="ArchiveLocation.Normalize"/>), and
/// infrastructure controls where the objects actually are. A mismatch follows a restore, a storage failover, or a
/// database copied to another environment.
/// </summary>
/// <remarks>
/// Whatever the handling, a store reads, restores, deletes and writes in its configured location only: a record never
/// redirects an operation. The handling decides only whether a mismatch stops the operation and how it is reported.
/// </remarks>
/// <docs>fundamentals/events/archive-store#archive-locations</docs>
/// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreLocationMismatchTests.cs:Move_ARecordPinnedToTheOldLocation_IsHandledAsConfiguredAsync</tests>
public enum ArchiveLocationMismatchHandling {
  /// <summary>
  /// The default: the operation is refused with <see cref="ArchiveLocationMismatchException"/> before any request reaches
  /// storage, and the deletion queue deletes only objects queued in the configured location. Stops the archive until the
  /// location is repointed or the configuration corrected.
  /// </summary>
  Error = 0,

  /// <summary>The operation goes ahead in the configured location, and the mismatch is logged as a warning.</summary>
  Warning = 1,

  /// <summary>The operation goes ahead in the configured location, and the mismatch is logged as information.</summary>
  Info = 2,

  /// <summary>The operation goes ahead in the configured location; no location is compared and nothing is logged.</summary>
  Ignore = 3,
}
