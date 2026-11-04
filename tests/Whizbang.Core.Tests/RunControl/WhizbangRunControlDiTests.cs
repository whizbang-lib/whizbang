// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using Microsoft.Extensions.Configuration;
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
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetService<WhizbangLifecycleCoordinator>()).IsNotNull();
    await Assert.That(provider.GetService<IWhizbangLifecycleState>()).IsNotNull();
    await Assert.That(provider.GetService<WhizbangLifecycleOptions>()).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangRunControl_BroadcastsToRegisteredAdaptersAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangRunControl();
    services.AddWhizbangRunControlAdapter<FakeAdapter>();
    await using var provider = services.BuildServiceProvider();

    var lifecycle = provider.GetRequiredService<IWhizbangLifecycleState>();
    var adapter = provider.GetServices<IWhizbangRunControl>().OfType<FakeAdapter>().Single();

    await lifecycle.AdvanceToAsync(LifecyclePhase.Migrating, CancellationToken.None);

    await Assert.That(adapter.Last).IsEqualTo(LifecyclePhase.Migrating);
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Migrating);
  }

  [Test]
  public async Task AddWhizbangRunControl_RegistersOnePermitAndTheOnePathOverItAsync() {
    // #2045: every claiming worker, the transport consumer and the elector obey the same permit the path closes.
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangRunControl();
    await using var provider = services.BuildServiceProvider();

    var permit = provider.GetRequiredService<WhizbangRunPermit>();
    var controller = provider.GetRequiredService<IWhizbangRunController>();

    await Assert.That(provider.GetRequiredService<WhizbangRunPermit>()).IsSameReferenceAs(permit);
    await Assert.That(controller.Permit).IsSameReferenceAs(permit);
    await Assert.That(provider.GetRequiredService<IWhizbangRunController>()).IsSameReferenceAs(controller);

    await provider.GetRequiredService<IWhizbangKillswitch>().PauseAsync();
    await Assert.That(permit.State).IsEqualTo(RunState.Paused);
    await Assert.That(controller.HaltedFor).IsEqualTo(RunHaltReason.Pause);
  }

  [Test]
  public async Task AddWhizbangRunControl_TheHeldWorkBound_BindsFromConfigurationAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Whizbang:Lifecycle:HeldWorkTimeout"] = "00:00:07" })
      .Build());
    services.AddWhizbangRunControl();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<WhizbangLifecycleOptions>().HeldWorkTimeout).IsEqualTo(TimeSpan.FromSeconds(7));
    await Assert.That(new WhizbangLifecycleOptions().HeldWorkTimeout).IsEqualTo(TimeSpan.FromSeconds(20))
      .Because("the default leaves a host's 30-second shutdown budget time to stop its workers and deregister");
  }
}
