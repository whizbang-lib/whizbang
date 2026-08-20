using Whizbang.Core.Routing;

namespace Whizbang.Core.Health;

/// <summary>
/// Managed-health surface of the startup topology-drift check (topology arc phase 5): reports
/// the <c>"topology"</c> component as <see cref="ComponentState.Degraded"/> — never faulted,
/// the service still serves — while any ownership violation stands (a second service's
/// subscription observed on a command inbox entity this service owns). Cross-service command
/// ownership cannot be enforced at build time (the WHIZ151 analyzer covers only the
/// intra-compilation invariant), so the provisioning path's observation is the census-mandated
/// runtime check — and a health Degraded is how it stays visible after the startup log scrolls
/// away.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#ownership-drift</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/TopologyDriftStateTests.cs</tests>
public sealed class TopologyDriftHealthSource : IWhizbangHealthSource {
  private readonly TopologyDriftState _state;

  /// <summary>Creates the source over the shared drift state the provisioners record into.</summary>
  /// <param name="state">The drift-state accumulator.</param>
  /// <exception cref="ArgumentNullException">Thrown when state is null.</exception>
  public TopologyDriftHealthSource(TopologyDriftState state) {
    ArgumentNullException.ThrowIfNull(state);
    _state = state;
  }

  /// <inheritdoc />
  public string Component => "topology";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    if (!_state.HasDrift) {
      return ValueTask.FromResult(new ComponentHealth(ComponentState.Operational));
    }

    var findings = _state.Findings;
    var detail = "topology ownership drift: "
      + string.Join("; ", findings.Select(f =>
          $"command inbox '{f.Entity}' carries foreign subscription '{f.ObservedSubscription}'"))
      + " — one service owns a command namespace; a second subscriber duplicates every command";
    return ValueTask.FromResult(new ComponentHealth(ComponentState.Degraded, detail));
  }
}
