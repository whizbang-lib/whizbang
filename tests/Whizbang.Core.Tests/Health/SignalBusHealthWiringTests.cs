using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.RunControl;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Locks the signal-bus health wiring (issue #505 layer 3): the <c>signal-bus</c> component must
/// report from the real <see cref="SignalBusLivenessState"/> — a failed wire-route probe or a
/// missed-doorbell streak degrades it — instead of the old assumed-healthy placeholder that could
/// never degrade no matter how broken the doorbell route was.
/// </summary>
public class SignalBusHealthWiringTests {
  private static async Task<(IWhizbangHealthSource Source, SignalBusLivenessState State, IServiceProvider Provider)> _buildAsync(LifecyclePhase phase) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    var provider = services.BuildServiceProvider();
    var lifecycle = provider.GetRequiredService<IWhizbangLifecycleState>();
    await lifecycle.AdvanceToAsync(phase, CancellationToken.None);
    var state = provider.GetRequiredService<SignalBusLivenessState>();
    var source = provider.GetServices<IWhizbangHealthSource>().Single(s => s.Component == "signal-bus");
    return (source, state, provider);
  }

  [Test]
  public async Task FailedWireRouteProbe_WhileRunning_ReportsDegradedAsync() {
    var (source, state, _) = await _buildAsync(LifecyclePhase.Running);

    state.MarkProbeResult(success: false, at: DateTimeOffset.UnixEpoch, failedTransport: "PostgresSignalTransport");
    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(health.Detail!).Contains("PostgresSignalTransport");
  }

  [Test]
  public async Task MissedDoorbellStreak_WhileRunning_ReportsDegradedAsync() {
    var (source, state, _) = await _buildAsync(LifecyclePhase.Running);

    state.MarkProbeResult(success: true, at: DateTimeOffset.UnixEpoch);
    for (var i = 0; i < 3; i++) {
      state.RecordMissedDoorbell();
    }
    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
  }

  [Test]
  public async Task VerifiedRoute_WhileRunning_ReportsOperationalAsync() {
    var (source, state, _) = await _buildAsync(LifecyclePhase.Running);

    state.MarkProbeResult(success: true, at: DateTimeOffset.UnixEpoch);
    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
  }

  [Test]
  public async Task DegradedState_OutsideRunning_StaysIntentionalAsync() {
    var (source, state, _) = await _buildAsync(LifecyclePhase.Migrating);

    state.MarkProbeResult(success: false, at: DateTimeOffset.UnixEpoch, failedTransport: "PostgresSignalTransport");
    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.PausedByDesign);
  }
}
