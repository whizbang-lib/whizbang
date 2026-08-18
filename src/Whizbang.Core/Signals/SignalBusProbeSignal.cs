namespace Whizbang.Core.Signals;

/// <summary>
/// Loopback doorbell used to verify the signal bus's wire route end to end: the host publishes it
/// targeted at its <em>own</em> instance through each transport and awaits its typed arrival. A
/// transport that cannot deliver it within the probe timeout marks the wire route failed and the
/// <c>signal-bus</c> health component <see cref="Health.ComponentState.Degraded"/> — a socket-level
/// self-test is not enough, because a healthy connection with a dead routing layer drops every
/// doorbell silently (issue #505). Doorbell-not-data, like every signal: no payload crosses the wire.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <tests>tests/Whizbang.Core.Tests/Signals/SignalBusProbeTests.cs:HostedStart_ProbeVerifiesWireRoute_ViaInMemoryAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Signals/SignalBusProbeTests.cs:HostedStart_DeadTransport_ProbeMarksWireRouteFailedAsync</tests>
[WireName("bus-probe")]
public readonly record struct SignalBusProbeSignal : ISignal {
  /// <inheritdoc />
  public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
  /// <inheritdoc />
  public static SignalTargeting Targeting => SignalTargeting.Targeted;
}
