// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>RED stub (#2137).</summary>
public sealed class ArchiveLocationMismatchException : Exception {
  /// <summary>RED stub.</summary>
  public ArchiveLocationMismatchException() { }

  /// <summary>RED stub.</summary>
  public ArchiveLocationMismatchException(string message) : base(message) { }

  /// <summary>RED stub.</summary>
  public ArchiveLocationMismatchException(string message, Exception innerException) : base(message, innerException) { }

  /// <summary>RED stub.</summary>
  public ArchiveLocationMismatchException(
      Guid streamId, string operation, long? recordedLocationId, ArchiveLocation? recorded, ArchiveLocation configured) {
    StreamId = streamId;
    Operation = operation;
    RecordedLocationId = recordedLocationId;
    Recorded = recorded;
    Configured = configured;
  }

  /// <summary>RED stub.</summary>
  public Guid? StreamId { get; }

  /// <summary>RED stub.</summary>
  public string? Operation { get; }

  /// <summary>RED stub.</summary>
  public long? RecordedLocationId { get; }

  /// <summary>RED stub.</summary>
  public ArchiveLocation? Recorded { get; }

  /// <summary>RED stub.</summary>
  public ArchiveLocation? Configured { get; }
}
