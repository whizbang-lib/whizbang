// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Archival;
using Whizbang.Data.Postgres;

namespace Whizbang.Storage.AzureBlob.Integration.Tests;

/// <summary>
/// The archive these tests exercise (#2503): the Postgres driver's coordinator writing through the Azure Blob object
/// store, built from the same arguments the Azure Blob archive store took before the two were split.
/// </summary>
internal static class AzureBlobArchive {
  public static PostgresObjectArchiveStore Create(
      PostgresArchiveConnection database,
      BlobContainerClient container,
      AzureBlobArchiveOptions options,
      ILogger<PostgresObjectArchiveStore>? logger = null,
      TimeProvider? timeProvider = null,
      ArchiveSweepMetrics? metrics = null) =>
    new(database, new AzureBlobArchiveObjectStore(container, options), logger, timeProvider, metrics);

  /// <summary>
  /// The locator a tombstone records, spelled out rather than asked of the code: <c>azure-blob://{stream}</c>, exactly
  /// what the Azure Blob archive store wrote, so archives written before the split stay readable.
  /// </summary>
  public static ArchiveLocator LocatorFor(Guid streamId) => ArchiveLocator.TryParse(
    $"azure-blob://{streamId:D}?codec=brotli&format=1", out var locator) ? locator : throw new InvalidOperationException("unreadable locator");
}
