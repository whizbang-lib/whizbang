using System.Threading;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The collective this process last published on each ordering key, so the dispatcher can stamp the next one on the
/// key with it (<see cref="ICollectiveEvent.PredecessorId"/>).
/// </summary>
/// <remarks>
/// <para>
/// A receiver uses the link to apply a key's collectives in the order they were sent even when they arrive in another
/// order. The link names the previous collective on the key, of any type, because the collectives a key orders are
/// usually different types (the two halves of a swap) and a receiver may handle only some of them.
/// </para>
/// <para>
/// Kept in memory: a link is a hint that lets a receiver wait for a collective it is about to receive, never a
/// requirement. The first collective on a key after a restart carries no link and applies as a collective always did,
/// and so does one published while the map was full: past <see cref="Capacity"/> keys the map starts over rather
/// than grow without bound.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events#ordering-across-services</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectivePredecessorTrackerTests.cs</tests>
public sealed class CollectivePredecessorTracker {
#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>The number of ordering keys remembered by default.</summary>
  public const int DEFAULT_CAPACITY = 10_000;
#pragma warning restore CA1707

  private readonly Lock _lock = new();
  private readonly Dictionary<Guid, (Guid EventId, string EventType)> _lastOnKey = [];

  /// <summary>Remembers up to <see cref="DEFAULT_CAPACITY"/> ordering keys.</summary>
  public CollectivePredecessorTracker() : this(DEFAULT_CAPACITY) {
  }

  /// <summary>Remembers up to <paramref name="capacity"/> ordering keys.</summary>
  /// <param name="capacity">How many keys to remember before starting over; at least one.</param>
  public CollectivePredecessorTracker(int capacity) {
    ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
    Capacity = capacity;
  }

  /// <summary>How many ordering keys are remembered before the map starts over.</summary>
  public int Capacity { get; }

  /// <summary>
  /// Stamps <paramref name="payload"/> with the collective published before it on its ordering key, and records it as
  /// the latest on that key. Does nothing for anything but a keyed <see cref="CollectiveEventBase"/>, or for one that
  /// already carries a link, as a collective published a second time does.
  /// </summary>
  /// <param name="payload">The message being published.</param>
  /// <param name="eventId">The id the message is published with.</param>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CollectivePredecessorTrackerTests.cs:Stamp_SecondCollectiveOnAKey_CarriesTheFirstAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CollectivePredecessorTrackerTests.cs:Stamp_TheSameCollectiveAgain_IsNotItsOwnPredecessorAsync</tests>
  public void Stamp(object? payload, Guid eventId) {
    if (payload is not CollectiveEventBase collective
        || string.IsNullOrWhiteSpace(collective.OrderingKey)
        || collective.PredecessorId is not null) {
      return;
    }
    var key = collective.StreamId;
    var type = TypeNameFormatter.Format(payload.GetType());
    (Guid EventId, string EventType) previous;
    bool hadPrevious;
    lock (_lock) {
      hadPrevious = _lastOnKey.TryGetValue(key, out previous);
      if (!hadPrevious && _lastOnKey.Count >= Capacity) {
        _lastOnKey.Clear();
      }
      _lastOnKey[key] = (eventId, type);
    }
    if (hadPrevious && previous.EventId != eventId) {
      collective.PredecessorId = previous.EventId;
      collective.PredecessorType = previous.EventType;
    }
  }
}
