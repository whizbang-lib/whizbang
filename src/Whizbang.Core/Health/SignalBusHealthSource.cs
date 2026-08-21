using Whizbang.Core.RunControl;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Health;

/// <summary>
/// Real health source for the <c>signal-bus</c> component, replacing the assumed-healthy
/// placeholder that could never degrade. Phase-aware like <see cref="ConnectivityHealthSource"/>
/// (intentional states outside Running report as by-design), and while Running it reports the
/// <see cref="SignalBusLivenessState"/> verdict: a failed wire-route self-test or a streak of
/// work batches discovered by poll with no doorbell degrades the component — the system still
/// serves (polling fallback), but every hop pays the poll interval (issue #505).
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <tests>tests/Whizbang.Core.Tests/Health/SignalBusHealthWiringTests.cs:FailedWireRouteProbe_WhileRunning_ReportsDegradedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Health/SignalBusHealthWiringTests.cs:MissedDoorbellStreak_WhileRunning_ReportsDegradedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Health/SignalBusHealthWiringTests.cs:DegradedState_OutsideRunning_StaysIntentionalAsync</tests>
public sealed class SignalBusHealthSource(
  SignalBusLivenessState liveness,
  IWhizbangLifecycleState lifecycle
) : IWhizbangHealthSource {
  private readonly SignalBusLivenessState _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
  private readonly IWhizbangLifecycleState _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

  /// <inheritdoc />
  public string Component => "signal-bus";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    var phase = _lifecycle.Phase;
    if (phase is LifecyclePhase.Faulted or LifecyclePhase.Halted) {
      return new ValueTask<ComponentHealth>(new ComponentHealth(ComponentState.Faulted));
    }
    if (phase == LifecyclePhase.Stopping) {
      return new ValueTask<ComponentHealth>(new ComponentHealth(ComponentState.Draining));
    }
    switch (phase) {
      case LifecyclePhase.Starting:
      case LifecyclePhase.Connecting:
        return new ValueTask<ComponentHealth>(new ComponentHealth(ComponentState.Connecting));
      case LifecyclePhase.Running:
        return new ValueTask<ComponentHealth>(_liveness.Report());
      default: // Migrating / Pausing / Paused / Resuming — the bus is not relied on right now.
        return new ValueTask<ComponentHealth>(new ComponentHealth(ComponentState.PausedByDesign));
    }
  }
}
