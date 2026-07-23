using Whizbang.Core.RunControl;

namespace Whizbang.Core.Health;

/// <summary>
/// When a managed resource is depended on. Drives how a <see cref="ConnectivityHealthSource"/> reads a
/// failed connectivity probe against the lifecycle phase.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public enum ConnectivityRequirement {
  /// <summary>
  /// Needed from the moment the system connects (e.g. the event-store DB — the migration calls into
  /// it). A failed probe outside startup is a <b>real fault even during a migration</b>: the depended-on
  /// dependency is never masked.
  /// </summary>
  AlwaysRequired,

  /// <summary>
  /// Only needed while <see cref="LifecyclePhase.Running"/> (e.g. the transport broker, the offload
  /// store). Outside Running the resource reports its intentional state (connecting / paused / draining)
  /// and is not probed — a disconnected transport during a migration is by-design, not broken.
  /// </summary>
  RequiredWhenRunning
}

/// <summary>
/// A reusable, phase-aware <see cref="IWhizbangHealthSource"/> for any resource whose health is a
/// reachability probe — the event-store DB, the transport broker, the offload store, the signal bus.
/// A driver registers one with its component id, a probe delegate (is my dependency reachable?), and a
/// <see cref="ConnectivityRequirement"/>; the source combines the probe with the current lifecycle phase
/// so intentional states (connecting, paused, draining, migrating) are healthy-by-design while a genuine
/// outage — <b>including a DB outage mid-migration for an <see cref="ConnectivityRequirement.AlwaysRequired"/>
/// resource</b> — surfaces as <see cref="ComponentState.Faulted"/>. Drivers never hand-roll the phase logic.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public sealed class ConnectivityHealthSource : IWhizbangHealthSource {
  private readonly Func<CancellationToken, ValueTask<bool>> _probe;
  private readonly IWhizbangLifecycleState _lifecycle;
  private readonly ConnectivityRequirement _requirement;
  private readonly string? _faultDetail;

  /// <summary>Creates a connectivity source for <paramref name="component"/>.</summary>
  public ConnectivityHealthSource(
      string component,
      Func<CancellationToken, ValueTask<bool>> probe,
      IWhizbangLifecycleState lifecycle,
      ConnectivityRequirement requirement,
      string? faultDetail = null) {
    ArgumentNullException.ThrowIfNull(component);
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(lifecycle);
    Component = component;
    _probe = probe;
    _lifecycle = lifecycle;
    _requirement = requirement;
    _faultDetail = faultDetail;
  }

  /// <summary>Creates an always-required source (event-store/DB): a failed probe is a fault even while migrating.</summary>
  public static ConnectivityHealthSource AlwaysRequired(
      string component, Func<CancellationToken, ValueTask<bool>> probe, IWhizbangLifecycleState lifecycle, string? faultDetail = null)
    => new(component, probe, lifecycle, ConnectivityRequirement.AlwaysRequired, faultDetail);

  /// <summary>Creates a required-when-running source (transport/offload): only probed while Running.</summary>
  public static ConnectivityHealthSource RequiredWhenRunning(
      string component, Func<CancellationToken, ValueTask<bool>> probe, IWhizbangLifecycleState lifecycle, string? faultDetail = null)
    => new(component, probe, lifecycle, ConnectivityRequirement.RequiredWhenRunning, faultDetail);

  /// <inheritdoc />
  public string Component { get; }

  /// <inheritdoc />
  public async ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    var phase = _lifecycle.Phase;
    if (phase is LifecyclePhase.Faulted or LifecyclePhase.Halted) {
      return new ComponentHealth(ComponentState.Faulted, _faultDetail);
    }
    if (phase == LifecyclePhase.Stopping) {
      return new ComponentHealth(ComponentState.Draining);
    }

    if (_requirement == ConnectivityRequirement.RequiredWhenRunning) {
      switch (phase) {
        case LifecyclePhase.Starting:
        case LifecyclePhase.Connecting:
          return new ComponentHealth(ComponentState.Connecting);
        case LifecyclePhase.Running:
          break; // probe below
        default: // Migrating / Pausing / Paused / Resuming — not relied on right now
          return new ComponentHealth(ComponentState.PausedByDesign);
      }
    } else if (phase == LifecyclePhase.Starting) {
      // AlwaysRequired: nothing has connected yet at process boot.
      return new ComponentHealth(ComponentState.Starting);
    }

    var reachable = await _probeSafeAsync(cancellationToken).ConfigureAwait(false);
    if (reachable) {
      return new ComponentHealth(ComponentState.Operational);
    }
    // Unreachable: still warming during Connecting; a genuine fault anywhere it is required.
    return phase == LifecyclePhase.Connecting
      ? new ComponentHealth(ComponentState.Connecting)
      : new ComponentHealth(ComponentState.Faulted, _faultDetail);
  }

  private async ValueTask<bool> _probeSafeAsync(CancellationToken cancellationToken) {
    try {
      return await _probe(cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch {
      return false; // a throwing probe = unreachable
    }
  }
}
