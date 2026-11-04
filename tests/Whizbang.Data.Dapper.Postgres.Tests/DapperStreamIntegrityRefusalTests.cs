// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// A Dapper host never runs a stream-integrity feature that does nothing: every integrity switch defaults to off on this
/// driver, and a host whose configuration turns one on is refused at start, naming the feature, the configuration key
/// and the reason (#2051).
/// </summary>
/// <remarks>
/// <para>
/// Stream integrity runs on the EF Core driver only. On Dapper its coordinator members are the interface's inert
/// defaults, so before this the checkpoint, audit and repair workers ran on a Dapper host by default and did nothing,
/// and a host that asked for auto-repair got none. Silent no-op is the one outcome not allowed.
/// </para>
/// <para>
/// The refusal goes through the options pipeline (<c>IValidateOptions</c> with <c>ValidateOnStart</c>), as every other
/// option check does, so it fails the host at start. Design choice documented at data/drivers#what-dapper-refuses-at-startup.
/// </para>
/// </remarks>
/// <docs>data/drivers#what-dapper-refuses-at-startup</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperStreamIntegrityRefusal.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
[Category("Integration")]
public class DapperStreamIntegrityRefusalTests {
  private const string CONNECTION = "Host=localhost;Database=never_opened;Username=u;Password=p";

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AHostThatConfiguresNothing_StartsWithEveryIntegritySwitchOffAsync(bool workersFirst) {
    await using var provider = _build([], workersFirst);

    var options = provider.GetRequiredService<IOptions<StreamIntegrityOptions>>().Value;

    await Assert.That(options.CheckpointsEnabled || options.GapDetectionEnabled || options.AuditEnabled
                      || options.EpochClosureEnabled || options.RepairDrainEnabled || options.BackfillOnSubscriptionGrowth).IsFalse()
      .Because("on Dapper the integrity features would run and do nothing, so a host that asks for none gets none");
    await Assert.That(options.RepairMode).IsEqualTo(IntegrityRepairMode.ReportOnly);
  }

  [Test]
  [Arguments("CheckpointsEnabled", "stream integrity checkpoints")]
  [Arguments("GapDetectionEnabled", "stream integrity gap detection")]
  [Arguments("AuditEnabled", "the stream integrity audit")]
  [Arguments("EpochClosureEnabled", "integrity digest epoch closure")]
  [Arguments("RepairDrainEnabled", "the integrity repair drain")]
  [Arguments("BackfillOnSubscriptionGrowth", "subscription-growth backfill (redelivery of a new type's history)")]
  public async Task ASwitchConfiguredOn_RefusesTheHost_NamingTheFeatureTheKeyAndTheReasonAsync(string key, string feature) {
    foreach (var workersFirst in new[] { true, false }) {
      await using var provider = _build(new Dictionary<string, string?> { [$"Whizbang:StreamIntegrity:{key}"] = "true" }, workersFirst);

      var refusal = await Assert.That(() => provider.GetRequiredService<IOptions<StreamIntegrityOptions>>().Value)
        .Throws<OptionsValidationException>();

      await Assert.That(refusal!.Failures.Count()).IsEqualTo(1)
        .Because("only the switch configuration turned on is refused, whichever registration ran first");
      await Assert.That(refusal.Message).Contains($"Whizbang:StreamIntegrity:{key} is true, which turns on {feature}");
      await Assert.That(refusal.Message).Contains("stream integrity runs on the EF Core driver only");
    }
  }

  [Test]
  public async Task AutoRepair_RefusesTheHostAsync() {
    await using var provider = _build(new Dictionary<string, string?> { ["Whizbang:StreamIntegrity:RepairMode"] = "AutoRepairCapped" }, workersFirst: false);

    var refusal = await Assert.That(() => provider.GetRequiredService<IOptions<StreamIntegrityOptions>>().Value)
      .Throws<OptionsValidationException>();

    await Assert.That(refusal!.Message).Contains(
      "Whizbang:StreamIntegrity:RepairMode is AutoRepairCapped, which turns on integrity auto-repair, but the Dapper driver refuses it");
    await Assert.That(refusal.Message).Contains("Set it to ReportOnly, or use the EF Core driver.");
  }

  [Test]
  public async Task ASwitchConfiguredOff_IsKeptOff_AndAnUnreadableValueCountsAsOffAsync() {
    await using var provider = _build(new Dictionary<string, string?> {
      ["Whizbang:StreamIntegrity:CheckpointsEnabled"] = "false",
      ["Whizbang:StreamIntegrity:AuditEnabled"] = "not-a-bool",
    }, workersFirst: false);

    var options = provider.GetRequiredService<IOptions<StreamIntegrityOptions>>().Value;

    await Assert.That(options.CheckpointsEnabled).IsFalse();
    await Assert.That(options.AuditEnabled).IsFalse();
  }

  [Test]
  public async Task WithNoConfigurationRegistered_EverySwitchIsOffAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangPostgres(CONNECTION, new JsonSerializerOptions(), initializeSchema: false, perspectiveSchemaSql: null);
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<StreamIntegrityOptions>>().Value;

    await Assert.That(options.CheckpointsEnabled || options.AuditEnabled).IsFalse();
  }

  [Test]
  public async Task TheRefusal_IsCheckedAtHostStartAsync() {
    await using var provider = _build([], workersFirst: false);

    var startValidation = provider.GetServices<IStartupValidator>();

    await Assert.That(startValidation).IsNotEmpty()
      .Because("ValidateOnStart registers the startup validator the host runs before any hosted service starts");
  }

  private static ServiceProvider _build(Dictionary<string, string?> settings, bool workersFirst) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    if (workersFirst) {
      services.AddWhizbangWorkers();
    }
    services.AddWhizbangPostgres(CONNECTION, new JsonSerializerOptions(), initializeSchema: false, perspectiveSchemaSql: null);
    if (!workersFirst) {
      services.AddWhizbangWorkers();
    }
    return services.BuildServiceProvider();
  }
}
