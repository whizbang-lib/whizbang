namespace Whizbang.Core.Signals;

/// <summary>
/// Metadata for one concrete <see cref="ISignal"/> type, collected at compile time by the signal
/// registry generator. The <see cref="WireName"/> is the stable identifier a transport uses to
/// route a received signal back to its type (so wire delivery needs no reflection).
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public sealed record SignalTypeEntry(
  Type SignalType,
  string WireName,
  SignalDeliveryClass DeliveryClass,
  SignalTargeting Targeting);
