using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <para>Locks in that the turnkey pipeline binds its own dead-letter options from configuration.
/// The gap this closes was found in production: <c>Whizbang__DeadLetterRecovery__Enabled=false</c>
/// sat on a pod for weeks while the worker ran at code defaults, because
/// <c>AddOptions&lt;T&gt;()</c> registers defaults and nothing anywhere read the section. A kill
/// switch that binds to nothing fails silently in the dangerous direction: the feature you
/// disabled keeps running and every dashboard says otherwise.</para>
/// <para>Binding must also degrade to code defaults when the host registers no
/// <see cref="IConfiguration"/> at all — bare unit-test hosts and minimal samples stay turnkey.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
[Category("Shard2")]
public sealed class WorkerOptionsBindingTests {

  private static ServiceProvider _hostWith(Dictionary<string, string?> settings) {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task DeadLetterRecovery_ReadsItsConfigurationSectionAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:DeadLetterRecovery:Enabled"] = "false",
      ["Whizbang:DeadLetterRecovery:WaitForIdle"] = "false",
      ["Whizbang:DeadLetterRecovery:ScanIntervalMinutes"] = "42",
    });

    var options = provider.GetRequiredService<IOptions<DeadLetterRecoveryOptions>>().Value;

    await Assert.That(options.Enabled).IsFalse()
      .Because("Enabled=false in configuration must actually stop the worker — this exact key "
             + "was set in production while recovery kept running on the code default");
    await Assert.That(options.WaitForIdle).IsFalse()
      .Because("the idle-arbitration opt-down is configuration, and unbound configuration is scenery");
    await Assert.That(options.ScanIntervalMinutes).IsEqualTo(42)
      .Because("numeric keys bind too, not just the booleans someone happened to test");
  }

  [Test]
  public async Task CanaryCampaignKeys_BindIncludingTheEnumAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:DeadLetterRecovery:RetryHeldOnStartup"] = "Canary",
      ["Whizbang:DeadLetterRecovery:CanaryProbeSize"] = "25",
      ["Whizbang:DeadLetterRecovery:ReleaseStaggerMinutes"] = "90",
    });
    var options = provider.GetRequiredService<IOptions<DeadLetterRecoveryOptions>>().Value;

    await Assert.That(options.RetryHeldOnStartup).IsEqualTo(RetryHeldOnStartupMode.Canary)
      .Because("the operator's set-and-restart lever is an enum, and enum binding must "
             + "survive the source-generated binder");
    await Assert.That(options.CanaryProbeSize).IsEqualTo(25);
    await Assert.That(options.ReleaseStaggerMinutes).IsEqualTo(90);
  }

  [Test]
  public async Task StreamIntegrity_DisableFlags_BindFromConfigurationAsync() {
    // #666: the integrity workers and the checkpoint receptor all honor their disable
    // flags — but the options class was never bound, so IOptions<StreamIntegrityOptions>
    // resolved default-constructed (everything enabled) and the flags in configuration
    // did nothing. Observed live: gap detection recounts at a double-digit share of DB
    // CPU on a fleet configured with GapDetectionEnabled=false.
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:StreamIntegrity:GapDetectionEnabled"] = "false",
      ["Whizbang:StreamIntegrity:AuditEnabled"] = "false",
      ["Whizbang:StreamIntegrity:CheckpointsEnabled"] = "false",
      ["Whizbang:StreamIntegrity:RepairMode"] = "ReportOnly",
    });
    var options = provider.GetRequiredService<IOptions<Whizbang.Core.Messaging.StreamIntegrityOptions>>().Value;
    await Assert.That(options.GapDetectionEnabled).IsFalse()
      .Because("an off switch that configuration cannot reach is not an off switch — the "
             + "flags must bind turnkey for the workers' checks to mean anything");
    await Assert.That(options.AuditEnabled).IsFalse();
    await Assert.That(options.CheckpointsEnabled).IsFalse();
    await Assert.That(options.RepairMode).IsEqualTo(Whizbang.Core.Messaging.IntegrityRepairMode.ReportOnly);
  }

  [Test]
  public async Task ClaimWorker_NotifyDrainLinger_BindsFromConfigurationAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:Claim:NotifyDrainLingerSeconds"] = "12",
    });
    var options = provider.GetRequiredService<IOptions<ClaimWorkerOptions>>().Value;
    await Assert.That(options.NotifyDrainLingerSeconds).IsEqualTo(12)
      .Because("the C# half of the doorbell debounce is an operator knob under the "
             + "turnkey-bound claim section, paired with the SQL notify_debounce_seconds "
             + "setting — both sides must be tunable, and the C# side must stay larger");
  }

  [Test]
  public async Task StackHistoryRetention_BindsFromConfigurationAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:DeadLetterRecovery:StackHistoryRetentionDays"] = "30",
    });
    var options = provider.GetRequiredService<IOptions<DeadLetterRecoveryOptions>>().Value;
    await Assert.That(options.StackHistoryRetentionDays).IsEqualTo(30)
      .Because("the rolling-history window is an operator knob under the turnkey-bound "
             + "dead-letter section");
  }

  [Test]
  public async Task TransportDrain_ReadsItsConfigurationSectionAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Workers:TransportDeadLetterDrain:Enabled"] = "false",
      ["Whizbang:Workers:TransportDeadLetterDrain:MaxPerTick"] = "77",
    });

    var options = provider.GetRequiredService<IOptions<TransportDeadLetterDrainWorkerOptions>>().Value;

    await Assert.That(options.Enabled).IsFalse()
      .Because("the drain kill switch is deployed under this section path in operations scripts");
    await Assert.That(options.MaxPerTick).IsEqualTo(77);
  }

  [Test]
  public async Task HousekeepingDeferralLimit_ReachesTheArbitrationMechanismAsync() {
    // Not just the options object: the value must reach the coordinator that actually
    // arbitrates. Two configured deferrals means the THIRD busy request forces through.
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Housekeeping:MaxConsecutiveDeferrals"] = "2",
    });
    var coordinator = provider.GetRequiredService<Whizbang.Core.Workers.HousekeepingCoordinator>();
    var busy = new ServiceBacklog { UnprocessedInboxRows = 500, ActiveLeasedRows = 1 };

    for (var i = 0; i < 2; i++) {
      var deferred = coordinator.TryBegin(
        Whizbang.Core.Workers.HousekeepingCoordinator.Activity.DeadLetterRecovery, busy);
      await Assert.That(deferred.Granted).IsFalse();
    }
    var forced = coordinator.TryBegin(
      Whizbang.Core.Workers.HousekeepingCoordinator.Activity.DeadLetterRecovery, busy);

    await Assert.That(forced.Reason)
      .IsEqualTo(Whizbang.Core.Workers.HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("an operator tuning the starvation floor from configuration must be tuning the "
             + "coordinator the workers consult, or the knob is scenery");
  }

  [Test]
  public async Task NoConfigurationRegistered_KeepsCodeDefaultsAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<DeadLetterRecoveryOptions>>().Value;

    await Assert.That(options.Enabled).IsTrue()
      .Because("a host with no IConfiguration must stay turnkey on code defaults, not throw");
  }
}
