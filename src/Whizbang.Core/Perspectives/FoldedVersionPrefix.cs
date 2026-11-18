// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Follows a replay's fold and says when what it has folded is every event of the stream up to some version, the set a
/// snapshot holds (#2379): the snapshot is then taken at the event with that version.
/// </summary>
/// <remarks>
/// A replay folds in replay order (<see cref="ReplayEventOrder"/>), which for a single-origin stream need not be version
/// order. Midway through, the fold can hold an event at version 5 and not yet the one at version 4. A snapshot of that row
/// recorded at either event would claim a set the row does not hold, so none is taken there; one is taken only where the
/// fold so far is exactly the events up to the highest version folded. The replay starts above a floor whose events it
/// does not read again, so only the events it reads are counted.
/// </remarks>
/// <docs>fundamentals/perspectives/rewind#snapshot-selection</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FoldedVersionPrefixTests.cs</tests>
public sealed class FoldedVersionPrefix {
  private readonly List<long> _readVersions = [];
  private bool _sorted = true;
  private bool _unversioned;
  private int _folded;
  private MessageEnvelope<IEvent>? _highest;

  /// <summary>Records the events a pass read, before any of them is folded.</summary>
  /// <param name="events">The stream's own events the pass read.</param>
  /// <docs>fundamentals/perspectives/rewind#snapshot-selection</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FoldedVersionPrefixTests.cs:Folded_InVersionOrder_IsAPrefixAtEveryEventAsync</tests>
  public void Read(IEnumerable<MessageEnvelope<IEvent>> events) {
    ArgumentNullException.ThrowIfNull(events);
    foreach (var envelope in events) {
      if (envelope.LocalStreamVersion is long version) {
        _readVersions.Add(version);
        _sorted = false;
      } else {
        _unversioned = true;
      }
    }
  }

  /// <summary>
  /// Records one folded event, and returns the event a snapshot of the fold so far is taken at: the one with the highest
  /// version folded, when the fold holds every event read up to it; otherwise null.
  /// </summary>
  /// <param name="envelope">The event just folded.</param>
  /// <returns>The snapshot's event, or null when the fold is not a version prefix.</returns>
  /// <docs>fundamentals/perspectives/rewind#snapshot-selection</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FoldedVersionPrefixTests.cs:Folded_AheadOfAnEarlierVersion_IsNoPrefixUntilItIsFoldedAsync</tests>
  public MessageEnvelope<IEvent>? Folded(MessageEnvelope<IEvent> envelope) {
    ArgumentNullException.ThrowIfNull(envelope);
    _folded++;
    if (_unversioned || envelope.LocalStreamVersion is not long version) {
      _unversioned = true;
      return null;
    }
    if (_highest is null || version > _highest.LocalStreamVersion) {
      _highest = envelope;
    }
    if (!_sorted) {
      _readVersions.Sort();
      _sorted = true;
    }
    return _countAtOrBelow(_highest.LocalStreamVersion!.Value) == _folded ? _highest : null;
  }

  // How many read versions are at or below the given one: the upper bound of a binary search.
  private int _countAtOrBelow(long version) {
    int low = 0, high = _readVersions.Count;
    while (low < high) {
      var mid = (low + high) / 2;
      if (_readVersions[mid] <= version) {
        low = mid + 1;
      } else {
        high = mid;
      }
    }
    return low;
  }
}
