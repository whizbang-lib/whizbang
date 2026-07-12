namespace Whizbang.Core.Signals;

/// <summary>
/// Metadata for one concrete <see cref="ISignal"/> type, collected at compile time by the signal
/// registry generator. The <see cref="WireName"/> is the stable identifier a transport uses to route
/// a received signal back to its type (so wire delivery needs no reflection), and <see cref="Dispatch"/>
/// reconstructs the doorbell signal from the wire and delivers it to a sink — the subscriber then
/// fetches authoritative state from the database (doorbell-not-data), so a default instance suffices.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public sealed record SignalTypeEntry(
  Type SignalType,
  string WireName,
  SignalDeliveryClass DeliveryClass,
  SignalTargeting Targeting,
  Func<ISignalSink, CancellationToken, ValueTask> Dispatch);
