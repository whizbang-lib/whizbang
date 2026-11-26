// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>#2601: rows leased again and again with no failure recorded, which the dead-letter bound never sees.</summary>
public partial class PerspectiveWorker {
  /// <summary>The fetched rows leased <paramref name="bound"/> times or more with no failure.</summary>
  /// <param name="rows">The fetched rows.</param>
  /// <param name="bound">The warning bound; null turns it off.</param>
  /// <param name="startup">The startup set the barrier waits on, if it is waiting.</param>
  /// <returns>The rows to warn about.</returns>
  internal static List<StreamEventData> RowsLeasedWithoutFailure(List<StreamEventData> rows, int? bound, StartupRewindSet? startup) =>
    throw new NotSupportedException($"{rows.Count} {bound} {startup}");

  /// <summary>Whether this is the first warning about <paramref name="workId"/> in this process.</summary>
  /// <param name="warned">The rows warned about so far.</param>
  /// <param name="workId">The row.</param>
  /// <param name="cap">How many rows are remembered before the memory starts over.</param>
  /// <returns>True the first time.</returns>
  internal static bool FirstWarningOf(ConcurrentDictionary<Guid, byte> warned, Guid workId, int cap) =>
    throw new NotSupportedException($"{warned.Count} {workId} {cap}");
}
