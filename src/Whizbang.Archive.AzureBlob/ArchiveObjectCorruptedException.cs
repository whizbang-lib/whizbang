// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Archive.AzureBlob;

/// <summary>
/// Thrown when an archive object's bytes do not match what the manifest recorded when it was written: a different length
/// or a different SHA-256. The object is never decoded, so a corrupted or truncated archive is reported rather than
/// read as events. A read stops at the corrupted object; a restore changes nothing and the stream stays archived.
/// </summary>
/// <docs>fundamentals/events/archive-store#azure-blob-integrity</docs>
/// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Read_ACorruptedObject_IsDetectedByItsHash_AndNothingIsDecodedAsync</tests>
/// <tests>tests/Whizbang.Archive.AzureBlob.Tests/ArchiveObjectCorruptedExceptionTests.cs</tests>
public sealed class ArchiveObjectCorruptedException : InvalidDataException {
  /// <summary>Creates the exception with no message.</summary>
  public ArchiveObjectCorruptedException() { }

  /// <summary>Creates the exception with a message.</summary>
  /// <param name="message">What failed.</param>
  public ArchiveObjectCorruptedException(string message) : base(message) { }

  /// <summary>Creates the exception with a message and its cause.</summary>
  /// <param name="message">What failed.</param>
  /// <param name="innerException">The cause.</param>
  public ArchiveObjectCorruptedException(string message, Exception innerException) : base(message, innerException) { }

  /// <summary>Creates the exception for one object whose bytes do not match its manifest row.</summary>
  /// <param name="streamId">The archived stream.</param>
  /// <param name="locator">The object's locator, as the manifest records it.</param>
  /// <param name="expectedLength">The byte length the manifest recorded.</param>
  /// <param name="actualLength">The byte length the store returned.</param>
  /// <param name="expectedSha256">The SHA-256 the manifest recorded.</param>
  /// <param name="actualSha256">The SHA-256 of the bytes the store returned.</param>
  public ArchiveObjectCorruptedException(
      Guid streamId, string locator, long expectedLength, long actualLength, string expectedSha256, string actualSha256)
    : base($"The archive object {locator} of stream {streamId} is corrupted: the manifest recorded {expectedLength} bytes " +
           $"with SHA-256 {expectedSha256}, the store returned {actualLength} bytes with SHA-256 {actualSha256}.") {
    StreamId = streamId;
    Locator = locator;
    ExpectedSha256 = expectedSha256;
    ActualSha256 = actualSha256;
  }

  /// <summary>The archived stream, when known.</summary>
  public Guid? StreamId { get; }

  /// <summary>The object's locator, when known.</summary>
  public string? Locator { get; }

  /// <summary>The SHA-256 the manifest recorded, when known.</summary>
  public string? ExpectedSha256 { get; }

  /// <summary>The SHA-256 of the bytes the store returned, when known.</summary>
  public string? ActualSha256 { get; }
}
