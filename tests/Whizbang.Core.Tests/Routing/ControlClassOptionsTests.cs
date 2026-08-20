using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for <see cref="ControlClassOptions"/> — the control class's delivery-semantics knobs
/// (topology arc phase 9) and, above all, the TTL DERIVATION.
/// <para>
/// The spec asks for <c>TimeToLive ≈ 2× cadence</c>. That is a rule, not a magic number, so it is
/// a pure static over (cadence, multiplier, floor) and is matrix-locked here rather than asserted
/// at one call site: a superseded control message must expire before its successor's successor
/// arrives, and a message that is merely slow must never expire mid-flight. The floor is what
/// keeps a very fast cadence from minting a TTL so short that a healthy broker round-trip loses it.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/ControlClassOptions.cs</code-under-test>
[Category("Core")]
[Category("Routing")]
public class ControlClassOptionsTests {
  [Test]
  public async Task Defaults_AreTheDocumentedPostureAsync() {
    var options = new ControlClassOptions();

    await Assert.That(options.Enabled).IsTrue()
      .Because("TTL minting is the safe half of the class — a supersedable message SHOULD expire");
    await Assert.That(options.CadenceMultiplier).IsEqualTo(2)
      .Because("the spec's rule is TTL ≈ 2× cadence");
    await Assert.That(options.TimeToLiveFloor).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(options.TimeToLive).IsNull()
      .Because("null = derive; an explicit value is the operator override");
    await Assert.That(options.SessionlessSubscriptions).IsFalse()
      .Because("changing entity session-ness re-provisions broker topology — opt-in, like every "
             + "other migration step in this arc");
    await Assert.That(options.NonDurableReceive).IsFalse()
      .Because("dropping the inbox row changes durability semantics — opt-in");
  }

  [Test]
  [Arguments(60, 2, 30, 120)]   // the shipped checkpoint cadence: 60s → 120s
  [Arguments(300, 2, 30, 600)]  // the shipped probe cadence: 5min → 10min
  [Arguments(1, 2, 30, 30)]     // 2s < floor → floor wins
  [Arguments(20, 2, 30, 40)]    // 40s > floor → derivation wins
  [Arguments(15, 2, 30, 30)]    // exactly the floor → floor (equal, either branch)
  [Arguments(60, 3, 30, 180)]   // a host that wants more headroom
  [Arguments(60, 1, 30, 60)]    // multiplier 1 = exactly one cadence
  public async Task DeriveTimeToLive_IsMaxOfFloorAndCadenceTimesMultiplierAsync(
      int cadenceSeconds, int multiplier, int floorSeconds, int expectedSeconds) {
    var derived = ControlClassOptions.DeriveTimeToLive(
      TimeSpan.FromSeconds(cadenceSeconds), multiplier, TimeSpan.FromSeconds(floorSeconds));

    await Assert.That(derived).IsEqualTo(TimeSpan.FromSeconds(expectedSeconds));
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  [Arguments(-3600)]
  public async Task DeriveTimeToLive_NonPositiveCadence_FallsBackToTheFloorAsync(int cadenceSeconds) {
    // A caller that cannot name a cadence (a disabled worker, an unconfigured interval) must not
    // mint a zero or negative TTL — that is an instantly-dead message, i.e. a silent broker drop.
    var derived = ControlClassOptions.DeriveTimeToLive(
      TimeSpan.FromSeconds(cadenceSeconds), 2, TimeSpan.FromSeconds(30));

    await Assert.That(derived).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  [Arguments(0)]
  [Arguments(-2)]
  public async Task DeriveTimeToLive_NonPositiveMultiplier_FallsBackToTheFloorAsync(int multiplier) {
    var derived = ControlClassOptions.DeriveTimeToLive(
      TimeSpan.FromSeconds(60), multiplier, TimeSpan.FromSeconds(30));

    await Assert.That(derived).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task DeriveTimeToLive_NonPositiveFloor_StillNeverReturnsNonPositiveAsync() {
    // Floor 0 with a usable cadence: the derivation carries it.
    var derived = ControlClassOptions.DeriveTimeToLive(TimeSpan.FromSeconds(60), 2, TimeSpan.Zero);
    await Assert.That(derived).IsEqualTo(TimeSpan.FromSeconds(120));

    // Floor 0 AND no usable cadence: fall back to the shipped floor rather than mint zero.
    var degenerate = ControlClassOptions.DeriveTimeToLive(TimeSpan.Zero, 2, TimeSpan.Zero);
    await Assert.That(degenerate).IsEqualTo(ControlClassOptions.DEFAULT_TIME_TO_LIVE_FLOOR);
  }

  [Test]
  public async Task DeriveTimeToLive_OverflowSaturates_NeverThrowsAsync() {
    var derived = ControlClassOptions.DeriveTimeToLive(
      TimeSpan.MaxValue, int.MaxValue, TimeSpan.FromSeconds(30));

    await Assert.That(derived).IsEqualTo(TimeSpan.MaxValue);
  }

  [Test]
  public async Task EffectiveTimeToLive_HonorsTheOverrideAsync() {
    var options = new ControlClassOptions { TimeToLive = TimeSpan.FromSeconds(7) };

    await Assert.That(options.EffectiveTimeToLive(TimeSpan.FromSeconds(60)))
      .IsEqualTo(TimeSpan.FromSeconds(7))
      .Because("an explicit override bypasses BOTH the derivation and the floor — the operator "
             + "who set it knows something the cadence does not say");
  }

  [Test]
  public async Task EffectiveTimeToLive_NoOverride_DerivesFromCadenceAsync() {
    var options = new ControlClassOptions();

    await Assert.That(options.EffectiveTimeToLive(TimeSpan.FromSeconds(60)))
      .IsEqualTo(TimeSpan.FromSeconds(120));
  }

  [Test]
  public async Task Options_BindFromConfigurationAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:ControlClass:Enabled"] = "false",
        ["Whizbang:Routing:ControlClass:CadenceMultiplier"] = "4",
        ["Whizbang:Routing:ControlClass:TimeToLiveFloor"] = "00:00:45",
        ["Whizbang:Routing:ControlClass:TimeToLive"] = "00:02:00",
        ["Whizbang:Routing:ControlClass:SessionlessSubscriptions"] = "true",
        ["Whizbang:Routing:ControlClass:NonDurableReceive"] = "true",
      })
      .Build();

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<ControlClassOptions>>().Value;
    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.CadenceMultiplier).IsEqualTo(4);
    await Assert.That(options.TimeToLiveFloor).IsEqualTo(TimeSpan.FromSeconds(45));
    await Assert.That(options.TimeToLive).IsEqualTo(TimeSpan.FromMinutes(2));
    await Assert.That(options.SessionlessSubscriptions).IsTrue();
    await Assert.That(options.NonDurableReceive).IsTrue();
  }

  [Test]
  public async Task Options_NoConfiguration_KeepDefaultsAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangWorkers();
    using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<ControlClassOptions>>().Value;
    await Assert.That(options.Enabled).IsTrue();
    await Assert.That(options.CadenceMultiplier).IsEqualTo(2);
    await Assert.That(options.SessionlessSubscriptions).IsFalse();
    await Assert.That(options.NonDurableReceive).IsFalse();
  }
}
