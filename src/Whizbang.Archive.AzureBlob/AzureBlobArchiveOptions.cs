// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  /// <summary>The Brotli quality, 0 (fastest) to 11 (smallest). See <see cref="DEFAULT_COMPRESSION_LEVEL"/>.</summary>
  public int CompressionLevel { get; set; } = DEFAULT_COMPRESSION_LEVEL;

  /// <summary>
  /// The most events one archive object holds. A larger stream is written as several objects, so neither a write nor a
  /// read ever holds more than one object's events in memory.
  /// </summary>
  public int MaxEventsPerChunk { get; set; } = DEFAULT_MAX_EVENTS_PER_CHUNK;
}
