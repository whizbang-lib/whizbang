namespace Whizbang.Core.RunControl;

/// <summary>
/// A ready-made <see cref="IWhizbangRunControl"/> that enforces its run-state through a
/// <see cref="WhizbangRunPermit"/>. A subsystem awaits the permit in its loop; the run-controller
/// pauses/resumes/stops the component by driving this adapter, which flips the permit. Register one
/// per component (e.g. <c>"workers"</c>, <c>"transport-consume"</c>) sharing the permit the subsystem awaits.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public sealed class RunPermitControl : IWhizbangRunControl {
  private readonly WhizbangRunPermit _permit;

  /// <summary>Creates an adapter for <paramref name="component"/> backed by <paramref name="permit"/>.</summary>
  public RunPermitControl(string component, WhizbangRunPermit permit) {
    ArgumentNullException.ThrowIfNull(component);
    ArgumentNullException.ThrowIfNull(permit);
    Component = component;
    _permit = permit;
  }

  /// <inheritdoc />
  public string Component { get; }

  /// <inheritdoc />
  public RunState Current => _permit.State;

  /// <inheritdoc />
  public ValueTask ApplyAsync(RunState desired, CancellationToken cancellationToken) {
    _permit.Set(desired);
    return default;
  }
}
