// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Whizbang.Core.Workers;

/// <summary>
/// When each collective started waiting for its predecessor on its ordering key (#1003), so a wait is bounded across
/// the sink runs that find it still missing.
/// </summary>
/// <remarks>
/// Kept per instance: a stream that moves to another instance starts its wait again there, which bounds a collective's
/// wait by the wait on each instance that holds it. A collective is forgotten when it applies; one applied elsewhere
/// is dropped once it has been held for twice the wait, which keeps the map to collectives waiting right now.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events#ordering-across-services</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/CollectivePredecessorHoldsTests.cs</tests>
internal sealed class CollectivePredecessorHolds(TimeProvider timeProvider) {
  private readonly ConcurrentDictionary<Guid, DateTimeOffset> _heldSince = new();

  /// <summary>How many collectives are waiting right now.</summary>
  internal int Count => _heldSince.Count;

  /// <summary>
  /// How much longer <paramref name="eventId"/> waits, starting its wait if it is not waiting yet, or
  /// <see cref="TimeSpan.Zero"/> when its wait has run out, which also forgets it.
  /// </summary>
  public TimeSpan HoldFor(Guid eventId, TimeSpan wait) {
    var now = timeProvider.GetUtcNow();
    foreach (var (heldId, since) in _heldSince) {
      if (heldId != eventId && since + wait + wait <= now) {
        _heldSince.TryRemove(heldId, out _);
      }
    }
    var remaining = _heldSince.GetOrAdd(eventId, now) + wait - now;
    if (remaining > TimeSpan.Zero) {
      return remaining;
    }
    _heldSince.TryRemove(eventId, out _);
    return TimeSpan.Zero;
  }

  /// <summary>Forgets <paramref name="eventId"/>, which has applied.</summary>
  public void Forget(Guid eventId) => _heldSince.TryRemove(eventId, out _);
}
