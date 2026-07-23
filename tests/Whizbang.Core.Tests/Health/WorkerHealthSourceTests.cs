using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Covers <see cref="WorkerHealthSource"/>: it mirrors the worker pipeline's phase-driven run-state —
/// Operational while Running, Draining while Stopping, Faulted on the fault path, and
/// <see cref="ComponentState.PausedByDesign"/> otherwise (held during startup/migration/pause),
/// healthy by default rather than "not running = broken".
/// </summary>
public class WorkerHealthSourceTests {

  private sealed class FakeLifecycle(LifecyclePhase phase) : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; } = phase;
    public ValueTask AdvanceToAsync(LifecyclePhase p, CancellationToken cancellationToken) => default;
  }

  private static async Task<ComponentState> _reportAsync(LifecyclePhase phase) {
    var source = new WorkerHealthSource(new FakeLifecycle(phase));
    var health = await source.ReportAsync(CancellationToken.None);
    return health.State;
  }

  [Test]
  public async Task Running_ReportsOperationalAsync()
    => await Assert.That(await _reportAsync(LifecyclePhase.Running)).IsEqualTo(ComponentState.Operational);

  [Test]
  [Arguments(LifecyclePhase.Starting)]
  [Arguments(LifecyclePhase.Connecting)]
  [Arguments(LifecyclePhase.Migrating)]
  [Arguments(LifecyclePhase.Pausing)]
  [Arguments(LifecyclePhase.Paused)]
  [Arguments(LifecyclePhase.Resuming)]
  public async Task IntentionallyHeld_ReportsPausedByDesignAsync(LifecyclePhase phase)
    => await Assert.That(await _reportAsync(phase)).IsEqualTo(ComponentState.PausedByDesign);

  [Test]
  public async Task Stopping_ReportsDrainingAsync()
    => await Assert.That(await _reportAsync(LifecyclePhase.Stopping)).IsEqualTo(ComponentState.Draining);

  [Test]
  [Arguments(LifecyclePhase.Faulted)]
  [Arguments(LifecyclePhase.Halted)]
  public async Task FaultPath_ReportsFaultedAsync(LifecyclePhase phase)
    => await Assert.That(await _reportAsync(phase)).IsEqualTo(ComponentState.Faulted);

  [Test]
  public async Task Component_IsWorkersAsync() {
    var source = new WorkerHealthSource(new FakeLifecycle(LifecyclePhase.Running));
    await Assert.That(source.Component).IsEqualTo("workers");
  }
}
