namespace Whizbang.Core.RunControl;

/// <summary>
/// Raised when a managed resource fails to acknowledge a lifecycle transition within
/// <see cref="WhizbangLifecycleOptions.TransitionAckTimeout"/>. The coordinator surfaces it so the
/// lifecycle state can drive the system to <see cref="LifecyclePhase.Faulted"/> — a resource that
/// hangs on a transition is a fault, not something to wait on forever.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangLifecycleCoordinatorTests.cs:Transition_Timeout_RaisesAckTimeoutAsync</tests>
public sealed class LifecycleAckTimeoutException : Exception {
  /// <summary>Creates the exception for the component that timed out on the given phase.</summary>
  public LifecycleAckTimeoutException(string component, LifecyclePhase phase)
      : base($"Managed resource '{component}' did not acknowledge lifecycle phase '{phase}' within the ack timeout.") {
    Component = component;
    Phase = phase;
  }

  /// <summary>Standard constructor (no component/phase context).</summary>
  public LifecycleAckTimeoutException() { }

  /// <summary>Standard constructor with a message.</summary>
  public LifecycleAckTimeoutException(string? message) : base(message) { }

  /// <summary>Standard constructor with a message and inner exception.</summary>
  public LifecycleAckTimeoutException(string? message, Exception? innerException) : base(message, innerException) { }

  /// <summary>The component that failed to acknowledge (null for the standard constructors).</summary>
  public string? Component { get; }

  /// <summary>The phase it was asked to enter.</summary>
  public LifecyclePhase Phase { get; }
}
