namespace Whizbang.Core.Notifications;

/// <summary>
/// One LISTEN channel subscription against the shared notify connection. Implementations are
/// thin and stateless — they own only the channel name and a callback. The actual connection,
/// the LISTEN/UNLISTEN SQL, reconnect handling, and the WaitAsync dispatch loop all live in
/// <see cref="ISharedNotifyConnection"/>'s implementation.
/// </summary>
/// <remarks>
/// <para>
/// Pre-slice-33 design had each consumer (work signals, commit-order stamping, app signals)
/// inherit <c>BackgroundService</c> and own its own <c>NpgsqlConnection</c>. With horizontal
/// scaling that's 3 × N pods of direct connections — a real load on the DB connection budget
/// on top of the pgbouncer-pooled traffic. Slice 33 collapses that to one direct conn per pod
/// with channels multiplexed via this interface.
/// </para>
/// <para>
/// <strong>Callback discipline.</strong> <see cref="OnNotification"/> is invoked synchronously
/// on the shared connection's WaitAsync dispatch loop. A slow callback blocks subsequent
/// notifications for ALL channels on that pod. Implementations must do only the minimum work
/// (parse the payload, enqueue to a local channel) and return immediately; the real work runs
/// in a separate background loop reading from that channel.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public interface INotifySubscription {
  /// <summary>
  /// The Postgres LISTEN channel name. Must match the corresponding producer-side
  /// <c>pg_notify</c> channel exactly. Will be wrapped in double-quotes when issued
  /// as <c>LISTEN "{name}"</c> so characters like hyphens and UUIDs are safe.
  /// </summary>
  string ChannelName { get; }

  /// <summary>
  /// Invoked when a notification arrives on <see cref="ChannelName"/>. Payload is the
  /// raw string passed to <c>pg_notify(channel, payload)</c> on the producer side.
  /// </summary>
  /// <remarks>
  /// Must be fast and non-blocking. See the type-level remarks for why.
  /// </remarks>
  void OnNotification(string payload);
}

/// <summary>
/// Registers <see cref="INotifySubscription"/> instances against the per-pod shared direct
/// connection. Multiplexes many channels onto one <c>NpgsqlConnection</c>.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public interface ISharedNotifyConnection {
  /// <summary>
  /// Register a subscription. If the shared connection is currently open, issues
  /// <c>LISTEN "{channel}"</c> immediately; if not, the subscription is queued and the
  /// LISTEN is issued when the connection next becomes available. Returns an
  /// <see cref="IDisposable"/> handle — disposing it removes this subscription and, when
  /// the channel has no remaining subscribers, issues <c>UNLISTEN "{channel}"</c>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Multiple subscribers on the same channel are supported — all callbacks fire on each
  /// notification. Useful when two unrelated components both want signals on the same channel
  /// (e.g., two flavors of work-signal consumer scaling separately).
  /// </para>
  /// <para>
  /// Subscriptions are persistent across the shared connection's reconnect cycles: when the
  /// gate's reprobe succeeds and the connection re-opens, every registered subscription's
  /// channel is re-LISTENed automatically. Callers don't need to re-subscribe.
  /// </para>
  /// </remarks>
  IDisposable Subscribe(INotifySubscription subscription);
}
