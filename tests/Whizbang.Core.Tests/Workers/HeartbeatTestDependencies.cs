using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The two required registry inputs of <see cref="Whizbang.Core.Workers.HeartbeatWorker"/> for tests
/// that build the worker by hand and do not care about them.
/// </summary>
internal static class HeartbeatTestDependencies {
  /// <summary>A lifecycle state pinned at Running.</summary>
  public static IWhizbangLifecycleState LifecycleState { get; } = new RunningLifecycleState();

  /// <summary>A fixed library version.</summary>
  public static ILibraryVersionProvider Version { get; } = new LibraryVersionProvider("0.0.0-test");

  private sealed class RunningLifecycleState : IWhizbangLifecycleState {
    public LifecyclePhase Phase => LifecyclePhase.Running;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }
}
