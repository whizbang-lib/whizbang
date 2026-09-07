using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.RunControl;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Coverage for three <see cref="SignalBusHealthSource.ReportAsync"/> phase branches
/// <see cref="SignalBusHealthWiringTests"/> never drives the lifecycle to: <see cref="LifecyclePhase.Faulted"/>,
/// <see cref="LifecyclePhase.Stopping"/>, and <see cref="LifecyclePhase.Starting"/>. This component
/// is phase-aware specifically so an intentional state (draining, still connecting, faulted) reads
/// as by-design instead of triggering the same alert as a genuine signal-bus degradation — get any
/// of these three wrong and an operator either pages on a routine shutdown or misses a real fault
/// because it read as "still connecting."
/// </summary>
public class SignalBusHealthSourceCoverageTests {

  private sealed class _fakeLifecycle : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; set; } = LifecyclePhase.Starting;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Phase = phase;
      return ValueTask.CompletedTask;
    }
    public ValueTask FaultAsync(CancellationToken cancellationToken) {
      Phase = LifecyclePhase.Faulted;
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  public async Task ReportAsync_Faulted_ReportsFaultedAsync() {
    var lifecycle = new _fakeLifecycle { Phase = LifecyclePhase.Faulted };
    var source = new SignalBusHealthSource(new SignalBusLivenessState(), lifecycle);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Faulted)
      .Because("a faulted process must surface the failure through every component's health, not read as merrily operational while the host is dying");
  }

  [Test]
  public async Task ReportAsync_Stopping_ReportsDrainingAsync() {
    var lifecycle = new _fakeLifecycle { Phase = LifecyclePhase.Stopping };
    var source = new SignalBusHealthSource(new SignalBusLivenessState(), lifecycle);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Draining)
      .Because("a coordinated shutdown is an intentional drain, not a degradation — reporting Degraded here would page an operator for a normal stop");
  }

  [Test]
  public async Task ReportAsync_Starting_ReportsConnectingAsync() {
    var lifecycle = new _fakeLifecycle { Phase = LifecyclePhase.Starting };
    var source = new SignalBusHealthSource(new SignalBusLivenessState(), lifecycle);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Connecting)
      .Because("before the doorbell route has been probed even once, health must read as still-connecting rather than falsely operational or falsely degraded");
  }
}
