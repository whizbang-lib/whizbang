namespace Whizbang.Core.RunControl;

/// <summary>
/// Broadcasts each lifecycle transition to every <see cref="IWhizbangRunControl"/> and coordinates it:
/// <list type="bullet">
///   <item><b>Barrier</b> — invokes all resources and awaits <em>all</em> their acks before returning.</item>
///   <item><b>Timeout</b> — bounds each resource's ack by <see cref="WhizbangLifecycleOptions.TransitionAckTimeout"/>; a timeout raises <see cref="LifecycleAckTimeoutException"/>.</item>
///   <item><b>Queue</b> — serializes transitions so only one is in flight at a time; concurrent calls queue.</item>
/// </list>
/// A resource that throws or times out surfaces the exception to the caller (<see cref="WhizbangLifecycleState"/>),
/// which turns it into a fault. This is the generalized, coordinated form of "never run Whizbang against
/// an unmigrated schema": the whole managed set moves together, and Whizbang knows the change reached
/// every resource before it proceeds.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public sealed class WhizbangLifecycleCoordinator : IDisposable {
  private readonly IEnumerable<IWhizbangRunControl> _participants;
  private readonly WhizbangLifecycleOptions _options;
  private readonly TimeProvider _timeProvider;
  private readonly SemaphoreSlim _serialize = new(1, 1);
  private readonly Dictionary<string, LifecyclePhase> _overrides = new(StringComparer.Ordinal);

  /// <summary>Creates a coordinator over the registered resources, options, and a clock (defaults to the system clock).</summary>
  public WhizbangLifecycleCoordinator(
      IEnumerable<IWhizbangRunControl> participants,
      WhizbangLifecycleOptions options,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(participants);
    ArgumentNullException.ThrowIfNull(options);
    _participants = participants;
    _options = options;
    _timeProvider = timeProvider ?? TimeProvider.System;
  }

  /// <summary>
  /// Broadcasts <paramref name="phase"/> to every resource and awaits all acknowledgements (the barrier),
  /// each bounded by the ack timeout. Serialized against other transitions (the queue). Throws
  /// <see cref="LifecycleAckTimeoutException"/> on a timeout or the resource's own exception on a throw.
  /// </summary>
  public async ValueTask TransitionAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
    await _serialize.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      var acks = new List<Task>();
      foreach (var participant in _participants) {
        // A per-component operator override pins that component to its own phase, independent of the
        // system phase (e.g. drain one transport for maintenance while the rest keep Running).
        var effective = _overrides.TryGetValue(participant.Component, out var pinned) ? pinned : phase;
        acks.Add(_ackAsync(participant, effective, cancellationToken));
      }
      await Task.WhenAll(acks).ConfigureAwait(false);
    } finally {
      _serialize.Release();
    }
  }

  /// <summary>
  /// Operator override: pin <paramref name="component"/> to <paramref name="overridePhase"/> (or clear
  /// it with <see langword="null"/>) regardless of the system phase, then re-apply the effective phase
  /// to that component. While pinned it ignores system transitions; clearing returns it to the system
  /// phase (<paramref name="systemPhase"/>).
  /// </summary>
  public async ValueTask SetComponentOverrideAsync(
      string component, LifecyclePhase? overridePhase, LifecyclePhase systemPhase, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(component);
    await _serialize.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      if (overridePhase is null) {
        _overrides.Remove(component);
      } else {
        _overrides[component] = overridePhase.Value;
      }
      var effective = overridePhase ?? systemPhase;
      var acks = new List<Task>();
      foreach (var participant in _participants) {
        if (string.Equals(participant.Component, component, StringComparison.Ordinal)) {
          acks.Add(_ackAsync(participant, effective, cancellationToken));
        }
      }
      await Task.WhenAll(acks).ConfigureAwait(false);
    } finally {
      _serialize.Release();
    }
  }

  private async Task _ackAsync(IWhizbangRunControl participant, LifecyclePhase phase, CancellationToken cancellationToken) {
    try {
      await participant.OnPhaseAsync(phase, cancellationToken)
        .AsTask()
        .WaitAsync(_options.TransitionAckTimeout, _timeProvider, cancellationToken)
        .ConfigureAwait(false);
    } catch (TimeoutException) {
      throw new LifecycleAckTimeoutException(participant.Component, phase);
    }
  }

  /// <summary>Disposes the serialization semaphore.</summary>
  public void Dispose() => _serialize.Dispose();
}
