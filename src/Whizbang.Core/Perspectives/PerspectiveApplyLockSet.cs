// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Takes the apply locks of a set of (stream, perspective) pairs together, in one fixed order, and releases them
/// together: the lock seam of the set-based write mode.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#locks</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveApplyLockSetTests.cs</tests>
public static class PerspectiveApplyLockSet {
  /// <summary>The order pairs are locked in: stream id, then perspective name (ordinal).</summary>
  public static IComparer<(Guid StreamId, string PerspectiveName)> Order => throw new NotImplementedException();

  /// <summary>Acquires the apply lock of every pair, in <see cref="Order"/>.</summary>
  /// <param name="coordinator">The apply coordinator.</param>
  /// <param name="pairs">The pairs of the batch.</param>
  /// <param name="cancellationToken">Cancels the wait for a lock.</param>
  /// <returns>The held locks, released when disposed.</returns>
  public static Task<IAsyncDisposable> AcquireAsync(
      IPerspectiveApplyCoordinator? coordinator,
      IEnumerable<(Guid StreamId, string PerspectiveName)> pairs,
      CancellationToken cancellationToken = default) =>
    throw new NotImplementedException();
}
