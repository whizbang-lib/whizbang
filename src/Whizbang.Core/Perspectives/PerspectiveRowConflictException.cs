// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Thrown by a perspective store when a per-stream apply's write is refused because the row changed after
/// the apply read it: another writer (a collective apply, a second apply of the same stream) committed in
/// between. Nothing was written. Writing would have put the stale values the apply computed from its old
/// read back over the other writer's, silently.
/// </summary>
/// <remarks>
/// The generated perspective runner catches this, re-reads the row, re-applies the batch's events onto it
/// and writes again, a bounded number of times. When the row keeps moving it rethrows, the batch fails
/// through the ordinary failure path, and its events are redelivered.
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowVersionTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowConflictRetryTests.cs</tests>
public sealed class PerspectiveRowConflictException : Exception {
  /// <summary>Initializes a new instance with a default message.</summary>
  public PerspectiveRowConflictException()
      : base("The perspective row changed after it was read for apply; the write was refused.") {
  }

  /// <summary>Initializes a new instance with <paramref name="message"/>.</summary>
  /// <param name="message">The exception message.</param>
  public PerspectiveRowConflictException(string message)
      : base(message) {
  }

  /// <summary>Initializes a new instance with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
  /// <param name="message">The exception message.</param>
  /// <param name="innerException">The exception that caused this one.</param>
  public PerspectiveRowConflictException(string message, Exception innerException)
      : base(message, innerException) {
  }

  /// <summary>Initializes a new instance describing one refused write.</summary>
  /// <param name="modelType">The perspective model whose row was written.</param>
  /// <param name="streamId">The row's stream id.</param>
  /// <param name="expectedVersion">The version the apply read.</param>
  /// <param name="actualVersion">The version the row carried when the write was refused.</param>
  public PerspectiveRowConflictException(
      Type modelType, Guid streamId, PerspectiveRowVersion expectedVersion, PerspectiveRowVersion actualVersion)
      : base(_describe(modelType, streamId, expectedVersion, actualVersion)) {
    ModelType = modelType;
    StreamId = streamId;
    ExpectedVersion = expectedVersion;
    ActualVersion = actualVersion;
  }

  private static string _describe(
      Type modelType, Guid streamId, PerspectiveRowVersion expectedVersion, PerspectiveRowVersion actualVersion) {
    ArgumentNullException.ThrowIfNull(modelType);
    return $"The {modelType.Name} perspective row for stream {streamId} changed after it was read for apply "
      + $"(version read: {expectedVersion}; version now: {actualVersion}). The write was refused so it cannot "
      + "overwrite the newer row; the apply re-reads the row and re-applies its events.";
  }

  /// <summary>The perspective model whose row was written, when known.</summary>
  public Type? ModelType { get; }

  /// <summary>The row's stream id.</summary>
  public Guid StreamId { get; }

  /// <summary>The version the apply read.</summary>
  public PerspectiveRowVersion ExpectedVersion { get; }

  /// <summary>The version the row carried when the write was refused.</summary>
  public PerspectiveRowVersion ActualVersion { get; }
}
