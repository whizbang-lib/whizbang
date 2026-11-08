// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// Thrown when an archive record's location is not the location the store is configured for (#2137): another storage
/// endpoint, another logical connection name, another container, or no location at all. The store throws before it
/// sends a single request to storage, so nothing is read, restored, deleted or written, nothing is ever deleted outside the
/// configured location, and a stream's archive never spans two locations.
/// </summary>
/// <remarks>
/// <para>
/// The usual cause is a database that moved without its configuration: a copy to another environment, a restore from a
/// backup, or disaster recovery to another storage account. Configuration drives where a store reads, restores, deletes
/// and writes; a record never redirects an operation.
/// </para>
/// <para>
/// When the objects are expected in the configured location (they were copied there, or the account was replaced),
/// acknowledge it by repointing the record's location: update its row in <c>wh_archive_locations</c>, or call the
/// store's repoint operation with <see cref="RecordedLocationId"/>. The check passes afterwards. When they are not, fix
/// the configuration instead.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/archive-store#archive-locations</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/ArchiveLocationMismatchExceptionTests.cs</tests>
/// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Read_Restore_Delete_InAnotherContainer_ThrowTheMismatch_AndTouchNoObjectAsync</tests>
public sealed class ArchiveLocationMismatchException : Exception {
  /// <summary>Creates the exception with no message.</summary>
  public ArchiveLocationMismatchException() { }

  /// <summary>Creates the exception with a message.</summary>
  /// <param name="message">What failed.</param>
  public ArchiveLocationMismatchException(string message) : base(message) { }

  /// <summary>Creates the exception with a message and its cause.</summary>
  /// <param name="message">What failed.</param>
  /// <param name="innerException">The cause.</param>
  public ArchiveLocationMismatchException(string message, Exception innerException) : base(message, innerException) { }

  /// <summary>Creates the exception for one stream whose archive is recorded somewhere the store is not configured for.</summary>
  /// <param name="streamId">The archived stream.</param>
  /// <param name="operation">What was refused: <c>read</c>, <c>restore</c>, <c>delete</c> or <c>write</c>.</param>
  /// <param name="recordedLocationId">The id of the record's location row, or <see langword="null"/> when it names none.</param>
  /// <param name="recorded">The record's location, or <see langword="null"/> when it names none.</param>
  /// <param name="configured">The location the store is configured for.</param>
  public ArchiveLocationMismatchException(
      Guid streamId, string operation, long? recordedLocationId, ArchiveLocation? recorded, ArchiveLocation configured)
    : base(_message(streamId, operation, recordedLocationId, recorded, configured)) {
    StreamId = streamId;
    Operation = operation;
    RecordedLocationId = recordedLocationId;
    Recorded = recorded;
    Configured = configured;
  }

  /// <summary>The archived stream, when known.</summary>
  public Guid? StreamId { get; }

  /// <summary>What was refused (<c>read</c>, <c>restore</c>, <c>delete</c> or <c>write</c>), when known.</summary>
  public string? Operation { get; }

  /// <summary>The id of the record's location row, to repoint; <see langword="null"/> when the record names none.</summary>
  public long? RecordedLocationId { get; }

  /// <summary>The record's location, or <see langword="null"/> when it names none or is not known.</summary>
  public ArchiveLocation? Recorded { get; }

  /// <summary>The location the store is configured for, when known.</summary>
  public ArchiveLocation? Configured { get; }

  private static string _message(
      Guid streamId, string operation, long? recordedLocationId, ArchiveLocation? recorded, ArchiveLocation configured) {
    var where = recorded is null
      ? "an archive record of it records no location"
      : $"its objects are recorded at location {recordedLocationId} ({recorded})";
    return $"The {operation} of the archive of stream {streamId} was refused: {where}, and this service is configured for " +
      $"{configured}. Nothing was read, restored, written or deleted. If the objects are expected in the configured location (the " +
      "database was copied or restored, or the storage moved), repoint the record's location row in wh_archive_locations " +
      "(or call the store's repoint operation); otherwise correct the configuration.";
  }
}
