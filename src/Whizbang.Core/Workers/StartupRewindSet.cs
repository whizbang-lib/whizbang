// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// The (stream, perspective) cursors the Blocking startup rewind scan found flagged RewindRequired, which the read-model
/// barrier waits on (#2214). Fixed at scan time: a key leaves the set when this worker completes a rewind of it
/// (<see cref="MarkRewound"/>) or when a keyed re-read finds it no longer flagged or gone (<see cref="ClearUnflagged"/>),
/// and nothing flagged after the scan ever joins it, so later flags cannot extend the wait.
/// </summary>
/// <remarks>
/// Written by the consumer loops (a completed rewind) and by the barrier's poll (a re-read) at once, so membership is a
/// concurrent dictionary and <see cref="AllCleared"/> completes, once, whichever of them removes the last key.
/// </remarks>
/// <docs>fundamentals/perspectives/rewind#startup-scan</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/StartupRewindSetTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerStartupRewindBarrierTests.cs</tests>
internal sealed class StartupRewindSet {
  private readonly ConcurrentDictionary<(Guid StreamId, string PerspectiveName), byte> _pending = new();
  private readonly TaskCompletionSource _allCleared = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Fixes the set to the cursors the scan found flagged.</summary>
  /// <param name="flagged">The cursors the scan returned; a key reported twice is one cursor.</param>
  public StartupRewindSet(IReadOnlyList<RewindCursorInfo> flagged) {
    ArgumentNullException.ThrowIfNull(flagged);
    foreach (var cursor in flagged) {
      _pending.TryAdd((cursor.StreamId, cursor.PerspectiveName), 0);
    }
    ScanTimeCount = _pending.Count;
    StreamCount = flagged.Select(c => c.StreamId).Distinct().Count();
    _completeIfEmpty();
  }

  /// <summary>How many distinct cursors the scan found flagged.</summary>
  public int ScanTimeCount { get; }

  /// <summary>How many distinct streams those cursors are on.</summary>
  public int StreamCount { get; }

  /// <summary>How many scan-time cursors are still flagged.</summary>
  public int RemainingCount => _pending.Count;

  /// <summary>How many scan-time cursors have cleared.</summary>
  public int ClearedCount => ScanTimeCount - RemainingCount;

  /// <summary>Completes when no scan-time cursor is left.</summary>
  public Task AllCleared => _allCleared.Task;

  /// <summary>
  /// Records a rewind this worker completed. Clears the key only when it is a scan-time key and the rewind left the
  /// cursor completed and no longer flagged.
  /// </summary>
  /// <param name="streamId">The rewound stream.</param>
  /// <param name="perspectiveName">The rewound perspective.</param>
  /// <param name="status">The status the rewind left the cursor in.</param>
  /// <returns>True when this call cleared a scan-time key.</returns>
  public bool MarkRewound(Guid streamId, string perspectiveName, PerspectiveProcessingStatus status) {
    var repaired = status.HasFlag(PerspectiveProcessingStatus.Completed)
      && !status.HasFlag(PerspectiveProcessingStatus.RewindRequired);
    if (!repaired || !_pending.TryRemove((streamId, perspectiveName), out _)) {
      return false;
    }
    _completeIfEmpty();
    return true;
  }

  /// <summary>The streams that still hold a scan-time cursor, each once: what the keyed re-read asks for.</summary>
  public Guid[] RemainingStreamIds() => [.. _pending.Keys.Select(k => k.StreamId).Distinct()];

  /// <summary>
  /// Applies a keyed re-read of <see cref="RemainingStreamIds"/>: every scan-time key whose cursor is no longer flagged,
  /// or is gone (its stream purged, so nothing is left to repair), leaves the set. Cursors the scan did not flag are
  /// ignored.
  /// </summary>
  /// <param name="cursors">The cursors the re-read returned.</param>
  public void ClearUnflagged(IReadOnlyList<PerspectiveCursorInfo> cursors) {
    var stillFlagged = cursors
      .Where(c => c.Status.HasFlag(PerspectiveProcessingStatus.RewindRequired))
      .Select(c => (c.StreamId, c.PerspectiveName))
      .ToHashSet();
    foreach (var key in _pending.Keys.Where(k => !stillFlagged.Contains(k))) {
      _pending.TryRemove(key, out _);
    }
    _completeIfEmpty();
  }

  /// <summary>Names the cursors still flagged as <c>stream/perspective</c>, at most <paramref name="max"/> of them.</summary>
  /// <param name="max">How many to name before summarizing the rest as a count.</param>
  /// <returns>A comma-separated list, ending in "and N more" when some were left out.</returns>
  public string DescribeRemaining(int max) {
    var keys = _pending.Keys.ToArray();
    var named = string.Join(", ", keys.Take(max).Select(k => $"{k.StreamId}/{k.PerspectiveName}"));
    return keys.Length > max
      ? string.Create(CultureInfo.InvariantCulture, $"{named}, and {keys.Length - max} more")
      : named;
  }

  private void _completeIfEmpty() {
    if (_pending.IsEmpty) {
      _allCleared.TrySetResult();
    }
  }
}
