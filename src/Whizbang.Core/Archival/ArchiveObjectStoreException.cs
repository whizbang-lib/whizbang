// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// A request to an <see cref="IArchiveObjectStore"/> failed in storage (#2503): refused, unreachable or timed out. The
/// provider's own error is the inner exception. The coordinator leaves the database as it was when a write fails this
/// way, keeps a deletion queued for its next attempt, and refuses an orphan sweep it cannot prove safe.
/// </summary>
/// <docs>fundamentals/events/archive-store#object-stores-and-the-coordinator</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs</tests>
public sealed class ArchiveObjectStoreException : Exception {
  /// <summary>Creates the exception with no message.</summary>
  public ArchiveObjectStoreException() { }

  /// <summary>Creates the exception with a message.</summary>
  /// <param name="message">What failed.</param>
  public ArchiveObjectStoreException(string message) : base(message) { }

  /// <summary>Creates the exception with a message and the provider's own error.</summary>
  /// <param name="message">What failed.</param>
  /// <param name="innerException">The provider's own error.</param>
  public ArchiveObjectStoreException(string message, Exception innerException) : base(message, innerException) { }

  /// <summary>Creates the exception for one request.</summary>
  /// <param name="operation">What was asked of storage: <c>put</c>, <c>get</c>, <c>delete</c>, <c>list</c> or <c>prepare</c>.</param>
  /// <param name="path">The object, or <see langword="null"/> for a request about the whole location.</param>
  /// <param name="location">The location the request went to.</param>
  /// <param name="innerException">The provider's own error.</param>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs:StoreException_NamesTheOperationTheObjectAndTheLocation_AndKeepsTheCauseAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs:StoreException_WithNoProviderError_StillNamesTheOperationAndTheLocationAsync</tests>
  public ArchiveObjectStoreException(string operation, string? path, ArchiveLocation location, Exception innerException)
    : base($"The archive {operation} of {(path is null ? "the location" : $"object {path}")} failed in storage ({location}): " +
           innerException?.Message, innerException) {
    Operation = operation;
    Path = path;
    Location = location;
  }

  /// <summary>What was asked of storage, when known.</summary>
  public string? Operation { get; }

  /// <summary>The object, when the request named one.</summary>
  public string? Path { get; }

  /// <summary>The location the request went to, when known.</summary>
  public ArchiveLocation? Location { get; }
}
