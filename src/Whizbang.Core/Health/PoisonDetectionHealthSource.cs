using Whizbang.Core.Routing;

namespace Whizbang.Core.Health;

/// <summary>
/// Managed-health surface of the poison detector's capability honesty (topology arc phase 8.5).
/// Reports the <c>"poison-detection"</c> component as <see cref="ComponentState.Degraded"/> —
/// never faulted, messages still flow — while any receive surface cannot supply a trustworthy
/// broker first-enqueue timestamp, which makes age-based quarantine (layer 1) inert there and
/// leaves durable observation counting (layer 2) as the only bound.
/// <para>
/// The valve this phase replaces failed silently: on session-enabled entities the broker's
/// delivery counter never rises under connection-death lock loss, so MaxDeliveryCount could not
/// fire and nobody could see that it could not. A degraded-but-invisible layer 1 would repeat the
/// mistake, so the fallback is health-visible after the startup log scrolls away.
/// </para>
/// </summary>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/PoisonMessageDetectorTests.cs</tests>
public sealed class PoisonDetectionHealthSource : IWhizbangHealthSource {
  private readonly PoisonDetectionCapabilityState _state;

  /// <summary>Creates the source over the shared capability state the transports report into.</summary>
  /// <param name="state">The capability-state record.</param>
  /// <exception cref="ArgumentNullException">Thrown when state is null.</exception>
  public PoisonDetectionHealthSource(PoisonDetectionCapabilityState state) {
    ArgumentNullException.ThrowIfNull(state);
    _state = state;
  }

  /// <inheritdoc />
  public string Component => "poison-detection";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    if (!_state.HasDegradedSurface) {
      return ValueTask.FromResult(new ComponentHealth(ComponentState.Operational));
    }

    var detail = "age-based poison detection is inert on: "
      + string.Join("; ", _state.DegradedSurfaces.Select(static finding =>
          $"{finding.Transport}/{finding.Entity}"))
      + " — no trustworthy broker first-enqueue timestamp; quarantine falls back to durable"
      + " observation counting on these surfaces";
    return ValueTask.FromResult(new ComponentHealth(ComponentState.Degraded, detail));
  }
}
