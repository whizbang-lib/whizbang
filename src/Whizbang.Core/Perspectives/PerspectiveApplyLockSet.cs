// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Takes the apply locks of a set of (stream, perspective) pairs together, in one fixed order, and releases them
/// together: the lock seam of the set-based write mode.
/// </summary>
/// <remarks>
/// <para>
/// The per-pair path takes one pair's apply lock inside <see cref="IPerspectiveRunner.RunWithEventsAsync"/>. The
/// set-based path prepares every pair of a batch, writes them in one round and completes them, so the lock of every
/// pair has to be held from before its prepare until after its completion, across the batch write.
/// <see cref="IPerspectiveRunner.PrepareAsync"/> and <see cref="IPerspectiveRunner.CompleteAsync"/> therefore take no
/// lock: the caller takes the batch's locks here first.
/// </para>
/// <para>
/// The locks are taken in <see cref="Order"/>, stream id first and perspective name second (ordinal), with
/// duplicates taken once. Two batches that share pairs then wait on the first pair they share and never each hold a
/// lock the other needs, so they cannot deadlock. A worker that keeps further per-pair gates of its own takes them in
/// the same order.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#locks</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveApplyLockSetTests.cs</tests>
public static class PerspectiveApplyLockSet {
  /// <summary>The order pairs are locked in: stream id (<see cref="Guid.CompareTo(Guid)"/>), then perspective name (ordinal).</summary>
  public static IComparer<(Guid StreamId, string PerspectiveName)> Order { get; } = new PairOrder();

  /// <summary>
  /// Acquires the apply lock of every pair, in <see cref="Order"/>, each pair once. Disposing the result releases them
  /// in the reverse order. When acquiring one fails (or is canceled), the ones already held are released before the
  /// failure is thrown.
  /// </summary>
  /// <param name="coordinator">The apply coordinator; with none registered nothing is locked.</param>
  /// <param name="pairs">The pairs of the batch.</param>
  /// <param name="cancellationToken">Cancels the wait for a lock.</param>
  /// <returns>The held locks, released when disposed.</returns>
  public static async Task<IAsyncDisposable> AcquireAsync(
      IPerspectiveApplyCoordinator? coordinator,
      IEnumerable<(Guid StreamId, string PerspectiveName)> pairs,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(pairs);
    var held = new HeldLocks();
    if (coordinator is null) {
      return held;
    }
    var ordered = new SortedSet<(Guid StreamId, string PerspectiveName)>(pairs, Order);
    try {
      foreach (var (streamId, perspectiveName) in ordered) {
        held.Add(await coordinator.AcquireAsync(streamId, perspectiveName, cancellationToken).ConfigureAwait(false));
      }
    } catch {
      await held.DisposeAsync().ConfigureAwait(false);
      throw;
    }
    return held;
  }

  private sealed class PairOrder : IComparer<(Guid StreamId, string PerspectiveName)> {
    public int Compare((Guid StreamId, string PerspectiveName) x, (Guid StreamId, string PerspectiveName) y) {
      var byStream = x.StreamId.CompareTo(y.StreamId);
      return byStream != 0 ? byStream : string.CompareOrdinal(x.PerspectiveName, y.PerspectiveName);
    }
  }

  /// <summary>The locks held, released in reverse order once.</summary>
  private sealed class HeldLocks : IAsyncDisposable {
    private readonly List<IAsyncDisposable> _locks = [];
    private int _released;

    public void Add(IAsyncDisposable heldLock) => _locks.Add(heldLock);

    public async ValueTask DisposeAsync() {
      if (Interlocked.Exchange(ref _released, 1) == 1) {
        return;
      }
      for (var i = _locks.Count - 1; i >= 0; i--) {
        await _locks[i].DisposeAsync().ConfigureAwait(false);
      }
    }
  }
}
