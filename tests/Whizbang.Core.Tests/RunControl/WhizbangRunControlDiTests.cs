using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers the run-control DI wiring: <c>AddWhizbangRunControl</c> registers a controller that drives
/// every adapter added via <c>AddWhizbangRunControlAdapter</c>, under the default phase policy.
/// </summary>
public class WhizbangRunControlDiTests {

  private sealed class FakeAdapter : IWhizbangRunControl {
    public string Component => "workers";
    public RunState Current { get; private set; } = RunState.Running;
    public ValueTask ApplyAsync(RunState desired, CancellationToken cancellationToken) {
      Current = desired;
      return default;
    }
  }

  [Test]
  public async Task AddWhizbangRunControl_DrivesRegisteredAdaptersAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangRunControl();
    services.AddWhizbangRunControlAdapter<FakeAdapter>();
    using var provider = services.BuildServiceProvider();

    var controller = provider.GetRequiredService<WhizbangRunController>();
    var adapter = provider.GetServices<IWhizbangRunControl>().OfType<FakeAdapter>().Single();

    await controller.TransitionAsync(LifecyclePhase.Migrating, CancellationToken.None);

    // "workers" is paused during a migration by WhizbangRunControlOptions.Default().
    await Assert.That(adapter.Current).IsEqualTo(RunState.Paused);
  }
}
