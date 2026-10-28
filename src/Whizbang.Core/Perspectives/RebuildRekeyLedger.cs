// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The re-keyed events one rebuild run has read, by the row they fold onto, so each such row is folded once from
/// everything the run found for it (#1820).
/// </summary>
/// <remarks>
/// <para>
/// A rebuild reads one stored stream at a time, and a re-keyed event is found on the stream it is stored on, not on
/// the row's. Folding the row while reading each source stream left it holding whichever source was read last. While
/// a run is open (<see cref="Begin"/>), a generated runner hands each re-keyed partition here instead of folding it,
/// and the rebuilder folds every row that received one after the run has read its streams, from the row's own stream
/// and every source (<see cref="IPerspectiveRunner.RunRekeyedRowAsync"/>).
/// </para>
/// <para>
/// A rebuild that does not read a source stream (a selected-streams rebuild of the target alone) cannot find the
/// events re-keyed from it: the event store has no index from a re-keyed row to its sources.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/RebuildRowRoutingTests.cs:Ledger_CollectsEachSourceOnceAndHandsOverOnlyTouchedRowsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/RebuildRowRoutingTests.cs:Rebuild_FoldsEachRekeyedRowOnceFromEverySourceAfterReadingTheStreamsAsync</tests>
public sealed class RebuildRekeyLedger {
  private static readonly AsyncLocal<RebuildRekeyLedger?> _current = new();

  // Row -> (source stream -> its events). Keyed by source, so a stream read twice in a run contributes once.
  private readonly Dictionary<Guid, Dictionary<Guid, IReadOnlyList<MessageEnvelope<IEvent>>>> _rows = [];
  private readonly List<Guid> _order = [];
  private readonly HashSet<Guid> _touched = [];

  /// <summary>The run open on this flow, or null when none is.</summary>
  public static RebuildRekeyLedger? Current => _current.Value;

  /// <summary>Makes <paramref name="ledger"/> the open run on this flow until the returned scope is disposed.</summary>
  /// <param name="ledger">The run's ledger.</param>
  /// <returns>The scope.</returns>
  public static IDisposable Begin(RebuildRekeyLedger ledger) {
    ArgumentNullException.ThrowIfNull(ledger);
    var previous = _current.Value;
    _current.Value = ledger;
    return new Scope(previous);
  }

  /// <summary>Records a re-keyed partition, replacing what an earlier read of the same source recorded for its row.</summary>
  /// <param name="partition">The partition.</param>
  public void Add(RebuildPartition partition) {
    ArgumentNullException.ThrowIfNull(partition);
    if (!_rows.TryGetValue(partition.Row, out var sources)) {
      sources = [];
      _rows[partition.Row] = sources;
      _order.Add(partition.Row);
    }
    sources[partition.StoredOn] = partition.Events;
    _touched.Add(partition.Row);
  }

  /// <summary>
  /// The rows that received re-keyed events since the last call, each with every source the run has recorded for it,
  /// in the order the rows and their sources were first recorded.
  /// </summary>
  /// <returns>The rows to fold.</returns>
  public IReadOnlyList<(Guid Row, IReadOnlyList<IReadOnlyList<MessageEnvelope<IEvent>>> Sources)> TakeTouched() {
    var touched = _order.Where(_touched.Contains)
      .Select(row => (row, (IReadOnlyList<IReadOnlyList<MessageEnvelope<IEvent>>>)[.. _rows[row].Values]))
      .ToList();
    _touched.Clear();
    return touched;
  }

  private sealed class Scope(RebuildRekeyLedger? previous) : IDisposable {
    public void Dispose() => _current.Value = previous;
  }
}
