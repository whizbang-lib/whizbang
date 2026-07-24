using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers the run-control DI wiring: <c>AddWhizbangRunControl</c> registers the lifecycle coordinator +
/// options + shared state, and <c>AddWhizbangRunControlAdapter</c> registers a participant the
/// coordinator broadcasts to.
/// </summary>
public class WhizbangRunControlDiTests {

  private sealed class FakeAdapter : IWhizbangRunControl {
    public string Component => "workers";
    public LifecyclePhase? Last { get; private set; }
    public ValueTask OnPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Last = phase;
      return default;
    }
  }

  [Test]
  public async Task AddWhizbangRunControl_RegistersCoordinatorAndStateAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangRunControl();
    using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetService<WhizbangLifecycleCoordinator>()).IsNotNull();
    await Assert.That(provider.GetService<IWhizbangLifecycleState>()).IsNotNull();
    await Assert.That(provider.GetService<WhizbangLifecycleOptions>()).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangRunControl_BroadcastsToRegisteredAdaptersAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangRunControl();
    services.AddWhizbangRunControlAdapter<FakeAdapter>();
    using var provider = services.BuildServiceProvider();

    var lifecycle = provider.GetRequiredService<IWhizbangLifecycleState>();
    var adapter = provider.GetServices<IWhizbangRunControl>().OfType<FakeAdapter>().Single();

    await lifecycle.AdvanceToAsync(LifecyclePhase.Migrating, CancellationToken.None);

    await Assert.That(adapter.Last).IsEqualTo(LifecyclePhase.Migrating);
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Migrating);
  }
}
