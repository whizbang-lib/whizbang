// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// A perspective write was fenced (#2556): the stream's partition is no longer owned at the epoch its work was claimed
/// under, so nothing was written and the work stays for the partition's current owner. Thrown by a per-pair write, and by
/// the completion of a set-based one, so the apply ends without finishing; its failure report is fenced too, and the
/// rows are replayed by the current owner.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWritesAreFencedTests.cs</tests>
public sealed class PartitionOwnershipLostException : Exception {
  /// <summary>Creates the exception for a stream.</summary>
  /// <param name="streamId">The stream whose write was fenced.</param>
  public PartitionOwnershipLostException(Guid streamId)
    : base($"The perspective write for stream {streamId} was fenced: its partition's ownership moved on since its work was claimed, so the current owner applies it.") {
    StreamId = streamId;
  }

  /// <summary>Creates the exception.</summary>
  public PartitionOwnershipLostException() : this(Guid.Empty) { }

  /// <summary>Creates the exception with a message.</summary>
  /// <param name="message">The message.</param>
  public PartitionOwnershipLostException(string message) : base(message) { }

  /// <summary>Creates the exception with a message and an inner exception.</summary>
  /// <param name="message">The message.</param>
  /// <param name="innerException">The inner exception.</param>
  public PartitionOwnershipLostException(string message, Exception innerException) : base(message, innerException) { }

  /// <summary>The stream whose write was fenced.</summary>
  public Guid StreamId { get; }
}
