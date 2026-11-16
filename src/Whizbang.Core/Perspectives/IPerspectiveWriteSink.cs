// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Writes a batch of prepared perspective rows with as few statements as their tables and shapes allow, and
/// reports one outcome per write.
/// </summary>
/// <remarks>
/// <para>
/// The write-sink seam of the set-based write mode: the runner prepares every (stream, perspective) pair of a batch
/// in memory and hands the results here, instead of saving each pair with its own round trip.
/// </para>
/// <para>
/// A sink keeps the per-row guarantees of the per-stream write. A write the ordering guard turns away is
/// <see cref="PerspectiveWriteStatus.Refused"/>, a row that moved since the apply read it is
/// <see cref="PerspectiveWriteStatus.Conflict"/>, and a row the database rejects (a constraint, a value it cannot
/// take) is <see cref="PerspectiveWriteStatus.Failed"/> without failing the other rows of the batch. A sink joins
/// the ambient transaction of the context it writes through, the same way the per-stream write does.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SetBasedPerspectiveWriteSinkTests.cs</tests>
public interface IPerspectiveWriteSink {
  /// <summary>Writes the batch.</summary>
  /// <param name="writes">The prepared writes, in batch order.</param>
  /// <param name="cancellationToken">Cancels the write.</param>
  /// <returns>One outcome per write, at the same index as the write it answers.</returns>
  Task<IReadOnlyList<PerspectiveWriteOutcome>> WriteAsync(
      IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default);
}

/// <summary>How a pending perspective write ended.</summary>
/// <docs>fundamentals/perspectives/write-modes</docs>
public enum PerspectiveWriteStatus {
  /// <summary>The row was written (or, for a purge, is gone).</summary>
  Written = 0,

  /// <summary>The ordering guard refused the write: the row already holds a later position. Nothing was written.</summary>
  Refused = 1,

  /// <summary>The row is not at the version the apply read. Nothing was written.</summary>
  Conflict = 2,

  /// <summary>The database rejected this row. Nothing was written for it; the batch's other rows were.</summary>
  Failed = 3,
}

/// <summary>The outcome of one pending perspective write.</summary>
/// <param name="Status">How the write ended.</param>
/// <param name="ActualVersion">For a conflict, the version the row is actually at (absent when there is no row).</param>
/// <param name="Error">For a failure, what the database reported.</param>
/// <docs>fundamentals/perspectives/write-modes</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PendingPerspectiveWriteTests.cs</tests>
public readonly record struct PerspectiveWriteOutcome(
    PerspectiveWriteStatus Status, PerspectiveRowVersion ActualVersion, Exception? Error) {
  /// <summary>The row was written.</summary>
  public static PerspectiveWriteOutcome Written => new(PerspectiveWriteStatus.Written, PerspectiveRowVersion.Unchecked, null);

  /// <summary>The ordering guard refused the write.</summary>
  public static PerspectiveWriteOutcome Refused => new(PerspectiveWriteStatus.Refused, PerspectiveRowVersion.Unchecked, null);

  /// <summary>The row moved since the apply read it.</summary>
  /// <param name="actual">The version the row is at, or absent.</param>
  /// <returns>The outcome.</returns>
  public static PerspectiveWriteOutcome Conflict(PerspectiveRowVersion actual) =>
    new(PerspectiveWriteStatus.Conflict, actual, null);

  /// <summary>The database rejected the row.</summary>
  /// <param name="error">What it reported.</param>
  /// <returns>The outcome.</returns>
  public static PerspectiveWriteOutcome Failed(Exception error) {
    ArgumentNullException.ThrowIfNull(error);
    return new(PerspectiveWriteStatus.Failed, PerspectiveRowVersion.Unchecked, error);
  }
}
