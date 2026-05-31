using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Whizbang.Core.Notifications;

/// <summary>
/// Thread-safe channel → subscribers map. Tells the surrounding shared-connection owner
/// whether a given subscribe/unsubscribe transition needs to fire <c>LISTEN</c> /
/// <c>UNLISTEN</c> on the live connection — only the first subscribe-for-a-channel and the
/// last unsubscribe-for-a-channel are connection events; everything else is a pure in-memory
/// mutation. Extracted as a separate class so the registration semantics (the part that
/// doesn't need a real Postgres connection) is unit-testable in isolation.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public sealed class NotifySubscriptionRegistry {
  private readonly ConcurrentDictionary<string, ImmutableArray<INotifySubscription>> _byChannel
    = new(StringComparer.Ordinal);

  /// <summary>
  /// Add <paramref name="subscription"/> to its channel. Returns <c>true</c> when this is the
  /// FIRST subscription on that channel (caller must issue <c>LISTEN</c> if the conn is open),
  /// <c>false</c> when other subscribers already cover the channel.
  /// </summary>
  public bool Add(INotifySubscription subscription) {
    ArgumentNullException.ThrowIfNull(subscription);
    // CAS-loop, NOT AddOrUpdate. ConcurrentDictionary.AddOrUpdate may invoke
    // addValueFactory multiple times under contention — see
    // https://learn.microsoft.com/dotnet/api/system.collections.concurrent.concurrentdictionary-2.addorupdate
    // — which would cause multiple concurrent Adds for the same channel to all
    // see wasFirst=true, breaking the "first subscribe issues LISTEN exactly once"
    // contract this method exists to provide.
    var channel = subscription.ChannelName;
    while (true) {
      if (_byChannel.TryGetValue(channel, out var existing)) {
        var updated = existing.Add(subscription);
        if (_byChannel.TryUpdate(channel, updated, existing)) {
          return false;  // Channel already had subscribers — not a transition.
        }
        // Lost the CAS — another writer mutated the slot; retry.
      } else {
        if (_byChannel.TryAdd(channel, [subscription])) {
          return true;  // We installed the slot — this caller MUST issue LISTEN.
        }
        // Lost the race — someone else installed the slot first; retry as an update.
      }
    }
  }

  /// <summary>
  /// Remove <paramref name="subscription"/> from its channel. Returns <c>true</c> when this
  /// was the LAST subscription on that channel (caller must issue <c>UNLISTEN</c> if the conn
  /// is open), <c>false</c> when other subscribers remain.
  /// </summary>
  public bool Remove(INotifySubscription subscription) {
    ArgumentNullException.ThrowIfNull(subscription);
    var wasLast = false;
    while (_byChannel.TryGetValue(subscription.ChannelName, out var existing)) {
      var idx = existing.IndexOf(subscription);
      if (idx < 0) {
        return false;
      }
      var updated = existing.RemoveAt(idx);
      if (updated.IsEmpty) {
        // Compare-and-swap the empty entry out so concurrent Adds don't observe a phantom
        // "first subscribe" against a stale empty array.
        if (((ICollection<KeyValuePair<string, ImmutableArray<INotifySubscription>>>)_byChannel)
            .Remove(new KeyValuePair<string, ImmutableArray<INotifySubscription>>(subscription.ChannelName, existing))) {
          wasLast = true;
          return wasLast;
        }
        // Lost the race against a concurrent mutation — retry.
        continue;
      }
      if (_byChannel.TryUpdate(subscription.ChannelName, updated, existing)) {
        return false;
      }
      // Lost the CAS — retry.
    }
    return wasLast;
  }

  /// <summary>Snapshot of every channel currently being LISTENed.</summary>
  public IReadOnlyCollection<string> AllChannels() => [.. _byChannel.Keys];

  /// <summary>
  /// Subscribers for a channel (or empty when nothing's registered). Returned as an
  /// <see cref="ImmutableArray{T}"/> so dispatch loops iterate without locks and concurrent
  /// Add/Remove never invalidate the snapshot.
  /// </summary>
  public ImmutableArray<INotifySubscription> Get(string channelName) =>
    _byChannel.TryGetValue(channelName, out var subs) ? subs : [];

  /// <summary>Total subscriber count across every channel — diagnostic only.</summary>
  public int TotalSubscriberCount() {
    var total = 0;
    foreach (var pair in _byChannel) {
      total += pair.Value.Length;
    }
    return total;
  }
}
