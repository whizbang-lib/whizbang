namespace Whizbang.Core.Signals;

/// <summary>
/// Doorbell signal that outbox work is available for the receiving instance. Wire-name
/// <c>"outbox"</c> matches the SQL payload that <c>notify_instance_owners</c> and the store
/// procs (<c>store_outbox_messages</c>, <c>_emit_event_store_chain</c>) already emit today, so
/// signal-bus subscribers replace <see cref="Whizbang.Core.Notifications.WorkSignalCategory"/>
/// subscribers on the same wire. Doorbell-not-data — the subscriber checks the DB for actual work.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[WireName("outbox")]
public readonly record struct WorkOutboxAvailableSignal : ISignal {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Targeted;
}

/// <summary>
/// Doorbell signal that inbox work is available for the receiving instance. Wire-name
/// <c>"inbox"</c> matches today's SQL payload (<c>store_inbox_messages</c>, <c>notify_instance_owners</c>).
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[WireName("inbox")]
public readonly record struct WorkInboxAvailableSignal : ISignal {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Targeted;
}

/// <summary>
/// Doorbell signal that perspective work is available for the receiving instance. Wire-name
/// <c>"perspective"</c> matches today's SQL payload (<c>notify_instance_owners</c> emit sites
/// from event-store and cursor-tail paths).
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[WireName("perspective")]
public readonly record struct WorkPerspectiveAvailableSignal : ISignal {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Targeted;
}
