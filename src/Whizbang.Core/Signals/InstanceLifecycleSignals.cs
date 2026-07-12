namespace Whizbang.Core.Signals;

/// <summary>
/// A new instance has joined the cluster (its first heartbeat registered a row in
/// <c>wh_service_instances</c>). Broadcast + best-effort — every pod receives it; the pull-source
/// / heartbeat scan reconciles missed notifications. Consumers use this to warm caches, rebalance
/// partition ownership, log topology changes, etc.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[WireName("instance-joined")]
public readonly record struct InstanceJoinedSignal : ISignal {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Broadcast;
}

/// <summary>
/// An instance is about to leave gracefully (shutdown path calling <c>deregister_instance</c>).
/// Broadcast + best-effort — missing this signal only costs a bit of latency until the heartbeat
/// scan detects the expired lease.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[WireName("instance-leaving")]
public readonly record struct InstanceLeavingSignal : ISignal {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Broadcast;
}

/// <summary>
/// An instance has died (ungraceful loss detected by lease/heartbeat expiry — e.g., pod OOM, node
/// crash, network partition). Broadcast + <em>durable</em> — this signal drives orphan takeover,
/// so it must never be missed. Persisted to <c>wh_signals</c> on emit and delivered by every pod's
/// tail cursor even if the fast-path NOTIFY was dropped.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[WireName("instance-died")]
public readonly record struct InstanceDiedSignal : ISignal {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Broadcast;
}
