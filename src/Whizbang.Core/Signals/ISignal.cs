namespace Whizbang.Core.Signals;

/// <summary>
/// Reliability class for a signal type. Most control-plane signals are best-effort
/// (a dropped notification only costs latency, because a signal is a doorbell and the
/// database is the source of truth). A small set must never be missed and are additionally
/// persisted to a durable log and tailed with a cursor.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public enum SignalDeliveryClass {
  /// <summary>Fire over the transport; correctness comes from the per-type pull-source reconciliation.</summary>
  BestEffort = 0,

  /// <summary>Also persisted to the durable signal log and tailed with a cursor — guaranteed delivery.</summary>
  Durable = 1,
}

/// <summary>
/// Reach of a signal type. Targeted signals route to the owning instance; broadcast signals
/// reach every instance.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public enum SignalTargeting {
  /// <summary>Routed to the instance that owns the affected partition/stream.</summary>
  Targeted = 0,

  /// <summary>Delivered to every instance in the cluster.</summary>
  Broadcast = 1,
}

/// <summary>
/// Marker for a control-plane signal carried by the <see cref="ISignalBus"/>. Signals follow
/// <em>doorbell-not-data</em> semantics: they carry only enough to identify what to look at —
/// the subscriber fetches authoritative state from the database. Each signal type declares its
/// reliability class and targeting.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface ISignal {
  /// <summary>Reliability class for this signal type.</summary>
  static abstract SignalDeliveryClass DeliveryClass { get; }

  /// <summary>Reach for this signal type.</summary>
  static abstract SignalTargeting Targeting { get; }
}
