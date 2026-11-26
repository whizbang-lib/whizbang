// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// #2601: rows leased again and again with no failure recorded. A lease ends in an apply, which completes the row, or a
/// failure, which the dead-letter bound counts; a row with neither is one nothing runs, and the bound never sees it.
/// </summary>
public partial class PerspectiveWorker {
  /// <summary>How many warned rows the worker remembers before the memory starts over.</summary>
  private const int MAX_LEASED_WITHOUT_FAILURE_REMEMBERED = 10_000;

  // The rows warned about in this process, so each is warned about once.
  private readonly ConcurrentDictionary<Guid, byte> _leasedWithoutFailureWarned = new();

  /// <summary>
  /// Warns about each fetched row leased <see cref="PerspectiveWorkerOptions.LeasedWithoutFailureWarningAttempts"/> times
  /// or more with no failure, once per row and process, and counts it; and while the startup barrier waits, records every
  /// row's leases on its set, so the cap's Warning says how often the queued row of a cursor still rewinding was leased.
  /// </summary>
  /// <docs>operations/workers/perspective-worker#rows-leased-without-a-failure</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerDeepPathDrainTests.LeasedWithoutFailure.cs</tests>
  private void _warnRowsLeasedWithoutFailure(List<StreamEventData> rows) {
    foreach (var row in RowsLeasedWithoutFailure(rows, _options.LeasedWithoutFailureWarningAttempts, _startupRewinds)) {
      if (!FirstWarningOf(_leasedWithoutFailureWarned, row.EventWorkId, MAX_LEASED_WITHOUT_FAILURE_REMEMBERED)) {
        continue;
      }
      _metrics?.RowsLeasedWithoutFailure.Add(1, new KeyValuePair<string, object?>(METRIC_TAG_PERSPECTIVE_NAME, row.PerspectiveName));
      LogRowLeasedWithoutFailure(_logger, row.PerspectiveName, row.StreamId, row.EventId, row.Attempts);
    }
  }

  /// <summary>The fetched rows leased <paramref name="bound"/> times or more with no failure.</summary>
  /// <param name="rows">The fetched rows.</param>
  /// <param name="bound">The warning bound; null turns it off.</param>
  /// <param name="startup">The startup set the barrier waits on, if it is waiting: every row's leases are recorded on it.</param>
  /// <returns>The rows to warn about, in fetch order.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/PerspectiveWorkerLeasedWithoutFailureTests.cs</tests>
  internal static List<StreamEventData> RowsLeasedWithoutFailure(List<StreamEventData> rows, int? bound, StartupRewindSet? startup) {
    var stuck = new List<StreamEventData>();
    foreach (var row in rows) {
      startup?.RecordLeases(row.StreamId, row.PerspectiveName!, row.Attempts, row.Failures);
      if (row.Failures == 0 && row.Attempts >= bound) {
        stuck.Add(row);
      }
    }
    return stuck;
  }

  /// <summary>Whether this is the first warning about <paramref name="workId"/> in this process.</summary>
  /// <param name="warned">The rows warned about so far.</param>
  /// <param name="workId">The row.</param>
  /// <param name="cap">How many rows are remembered before the memory starts over, so it stays bounded.</param>
  /// <returns>True the first time.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/PerspectiveWorkerLeasedWithoutFailureTests.cs:FirstWarningOf_IsTrueOncePerRow_AndTheMemoryStartsOverAtItsCapAsync</tests>
  internal static bool FirstWarningOf(ConcurrentDictionary<Guid, byte> warned, Guid workId, int cap) {
    if (warned.Count >= cap) {
      warned.Clear();
    }
    return warned.TryAdd(workId, 0);
  }
}
