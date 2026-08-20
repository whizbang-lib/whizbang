using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Topology arc phase 8.5 — the poison detector must be WIRED, not merely written. The valve it
/// replaces was unreachable in production for its entire life; a policy that exists in the
/// assembly but never reaches a transport would repeat that exactly. These tests lock the turnkey
/// registration and the operator-reachable configuration surface (killswitch and both bounds,
/// bindable without a redeploy).
/// </summary>
public class PoisonMessageWiringTests {

  [Test]
  public async Task AddWhizbangWorkers_RegistersTheDetectorCapabilityStateAndHealthSourceAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetService<IPoisonMessageDetector>()).IsNotNull();
    await Assert.That(provider.GetService<PoisonDetectionCapabilityState>()).IsNotNull();
    await Assert.That(provider.GetServices<IWhizbangHealthSource>()
      .Any(static source => source is PoisonDetectionHealthSource)).IsTrue()
      .Because("a silently-inert layer 1 must be visible on the health endpoint");
  }

  [Test]
  public async Task AddWhizbangWorkers_DetectorAndHealthSourceShareOneCapabilityStateAsync() {
    // A health source reading a DIFFERENT state instance would report Operational forever —
    // the same invisible-degradation failure in a new place.
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var detector = provider.GetRequiredService<IPoisonMessageDetector>();
    var state = provider.GetRequiredService<PoisonDetectionCapabilityState>();
    detector.ReportAgeCapability("rabbitmq", "inbox.orders", canSupplyTrustworthyAge: false);

    var source = provider.GetServices<IWhizbangHealthSource>()
      .OfType<PoisonDetectionHealthSource>().Single();
    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(state.HasDegradedSurface).IsTrue();
    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
  }

  [Test]
  public async Task Configuration_BindsEveryOperatorReachableKnobAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Routing:PoisonMessages:Enabled"] = "false",
      ["Whizbang:Routing:PoisonMessages:AgeThreshold"] = "01:15:00",
      ["Whizbang:Routing:PoisonMessages:AgeThresholdFloor"] = "00:20:00",
      ["Whizbang:Routing:PoisonMessages:MaxDurableObservations"] = "42",
      ["Whizbang:Routing:PoisonMessages:LockRenewalDuration"] = "00:07:00",
      ["Whizbang:Routing:PoisonMessages:MaxDeliveryAttempts"] = "3",
    }).Build();

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddLogging();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<PoisonMessageOptions>>().Value;

    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.AgeThreshold).IsEqualTo(TimeSpan.FromMinutes(75));
    await Assert.That(options.AgeThresholdFloor).IsEqualTo(TimeSpan.FromMinutes(20));
    await Assert.That(options.MaxDurableObservations).IsEqualTo(42);
    await Assert.That(options.LockRenewalDuration).IsEqualTo(TimeSpan.FromMinutes(7));
    await Assert.That(options.MaxDeliveryAttempts).IsEqualTo(3);
  }

  [Test]
  public async Task Configuration_AbsentSection_LeavesDefaultsLockedAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddLogging();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<PoisonMessageOptions>>().Value;

    await Assert.That(options.Enabled).IsTrue()
      .Because("an unreachable dead-letter valve is the defect this phase closes — on by default");
    await Assert.That(options.AgeThreshold).IsNull();
    await Assert.That(options.MaxDurableObservations)
      .IsEqualTo(PoisonMessageOptions.DEFAULT_MAX_DURABLE_OBSERVATIONS);
    await Assert.That(options.EffectiveAgeThreshold).IsEqualTo(TimeSpan.FromMinutes(50))
      .Because("5-minute renewal x 10 attempts, above the 30-minute floor");
  }
}
