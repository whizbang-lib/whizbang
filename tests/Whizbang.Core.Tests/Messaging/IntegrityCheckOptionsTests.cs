// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2353: sealing, the sealed-history re-check and the full-history compare each have their own configuration section
/// (<c>Whizbang:StreamIntegrity:Sealing</c>, <c>:SealRecheck</c>, <c>:FullHistoryCompare</c>). Sealing is on by default;
/// both re-checks are off by default. The keys these sections replace keep working as aliases for one release, and a new
/// key wins over its alias when both are set.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityCheckOptions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
public class IntegrityCheckOptionsTests {

  private static ServiceProvider _hostWith(Dictionary<string, string?> settings) {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  private static StreamIntegrityOptions _bound(Dictionary<string, string?> settings) {
    using var provider = _hostWith(settings);
    return provider.GetRequiredService<IOptions<StreamIntegrityOptions>>().Value;
  }

  // ── defaults ─────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Sealing_IsOnByDefault_WithTheEpochWidthAndCapItHadAsync() {
    var sealing = new StreamIntegrityOptions().Sealing;

    await Assert.That(sealing.Enabled).IsTrue()
      .Because("sealing is what lets an answer read immutable folds; turning re-checks off must never stop it");
    await Assert.That(sealing.EpochWidth).IsEqualTo(100_000L);
    await Assert.That(sealing.MaxClosuresPerCycle).IsEqualTo(64);
  }

  [Test]
  public async Task SealRecheck_IsOffByDefault_WithTheDocumentedLimitsAsync() {
    var recheck = new StreamIntegrityOptions().SealRecheck;

    await Assert.That(recheck.Enabled).IsFalse()
      .Because("the re-check grows with sealed history, so it is an opt-in sized by the operator (decision 4)");
    await Assert.That(recheck.Schedule).IsEqualTo("0 3 * * *");
    await Assert.That(recheck.Lookback).IsEqualTo(TimeSpan.FromDays(30));
    await Assert.That(recheck.RecheckEvery).IsEqualTo(TimeSpan.FromDays(7));
    await Assert.That(recheck.MaxEpochsPerRun).IsEqualTo(10_000);
    await Assert.That(recheck.OnDrift).IsEqualTo(SealDriftAction.Refold);
  }

  [Test]
  public async Task FullHistoryCompare_IsOffByDefault_OnTheNightlyScheduleAsync() {
    var compare = new StreamIntegrityOptions().FullHistoryCompare;

    await Assert.That(compare.Enabled).IsFalse()
      .Because("the whole-store heal and the full-history ask grow with history; the routine windowed audit does not");
    await Assert.That(compare.Schedule).IsEqualTo("0 3 * * *");
  }

  // ── binding ──────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task TheThreeSections_BindFromConfigurationAsync() {
    var options = _bound(new Dictionary<string, string?> {
      ["Whizbang:StreamIntegrity:Sealing:Enabled"] = "false",
      ["Whizbang:StreamIntegrity:Sealing:EpochWidth"] = "5000",
      ["Whizbang:StreamIntegrity:Sealing:MaxClosuresPerCycle"] = "9",
      ["Whizbang:StreamIntegrity:SealRecheck:Enabled"] = "true",
      ["Whizbang:StreamIntegrity:SealRecheck:Schedule"] = "15 4 * * *",
      ["Whizbang:StreamIntegrity:SealRecheck:Lookback"] = "10.00:00:00",
      ["Whizbang:StreamIntegrity:SealRecheck:RecheckEvery"] = "2.00:00:00",
      ["Whizbang:StreamIntegrity:SealRecheck:MaxEpochsPerRun"] = "250",
      ["Whizbang:StreamIntegrity:SealRecheck:OnDrift"] = "ReportOnly",
      ["Whizbang:StreamIntegrity:FullHistoryCompare:Enabled"] = "true",
      ["Whizbang:StreamIntegrity:FullHistoryCompare:Schedule"] = "30 2 * * 0",
    });

    await Assert.That(options.Sealing.Enabled).IsFalse();
    await Assert.That(options.Sealing.EpochWidth).IsEqualTo(5000L);
    await Assert.That(options.Sealing.MaxClosuresPerCycle).IsEqualTo(9);
    await Assert.That(options.SealRecheck.Enabled).IsTrue();
    await Assert.That(options.SealRecheck.Schedule).IsEqualTo("15 4 * * *");
    await Assert.That(options.SealRecheck.Lookback).IsEqualTo(TimeSpan.FromDays(10));
    await Assert.That(options.SealRecheck.RecheckEvery).IsEqualTo(TimeSpan.FromDays(2));
    await Assert.That(options.SealRecheck.MaxEpochsPerRun).IsEqualTo(250);
    await Assert.That(options.SealRecheck.OnDrift).IsEqualTo(SealDriftAction.ReportOnly);
    await Assert.That(options.FullHistoryCompare.Enabled).IsTrue();
    await Assert.That(options.FullHistoryCompare.Schedule).IsEqualTo("30 2 * * 0");
  }

  // ── the old keys are aliases for one release ─────────────────────────────────────────────

  [Test]
  public async Task TheOldKeys_StillReachTheNewSettingsAsync() {
    var options = _bound(new Dictionary<string, string?> {
      ["Whizbang:StreamIntegrity:EpochClosureEnabled"] = "false",
      ["Whizbang:StreamIntegrity:MaxEpochClosuresPerMaintenanceCycle"] = "7",
      ["Whizbang:StreamIntegrity:MaxEpochVerificationsPerSweep"] = "123",
      ["Whizbang:StreamIntegrity:FullSweepCron"] = "45 1 * * *",
    });

    await Assert.That(options.Sealing.Enabled).IsFalse()
      .Because("a host that turned closure off with the old key must not find it back on after the upgrade");
    await Assert.That(options.Sealing.MaxClosuresPerCycle).IsEqualTo(7);
    await Assert.That(options.SealRecheck.MaxEpochsPerRun).IsEqualTo(123);
    await Assert.That(options.SealRecheck.Schedule).IsEqualTo("45 1 * * *")
      .Because("the old sweep cron scheduled both checks, so it still schedules both");
    await Assert.That(options.FullHistoryCompare.Schedule).IsEqualTo("45 1 * * *");
  }

  [Test]
  public async Task ANewKey_WinsOverItsAlias_WhenBothAreSetAsync() {
    var options = _bound(new Dictionary<string, string?> {
      ["Whizbang:StreamIntegrity:EpochClosureEnabled"] = "false",
      ["Whizbang:StreamIntegrity:Sealing:Enabled"] = "true",
      ["Whizbang:StreamIntegrity:MaxEpochClosuresPerMaintenanceCycle"] = "7",
      ["Whizbang:StreamIntegrity:Sealing:MaxClosuresPerCycle"] = "11",
      ["Whizbang:StreamIntegrity:MaxEpochVerificationsPerSweep"] = "123",
      ["Whizbang:StreamIntegrity:SealRecheck:MaxEpochsPerRun"] = "456",
      ["Whizbang:StreamIntegrity:FullSweepCron"] = "45 1 * * *",
      ["Whizbang:StreamIntegrity:SealRecheck:Schedule"] = "5 5 * * *",
      ["Whizbang:StreamIntegrity:FullHistoryCompare:Schedule"] = "6 6 * * *",
    });

    await Assert.That(options.Sealing.Enabled).IsTrue()
      .Because("the section is the documented home; an alias left behind in an old file must not override it");
    await Assert.That(options.Sealing.MaxClosuresPerCycle).IsEqualTo(11);
    await Assert.That(options.SealRecheck.MaxEpochsPerRun).IsEqualTo(456);
    await Assert.That(options.SealRecheck.Schedule).IsEqualTo("5 5 * * *");
    await Assert.That(options.FullHistoryCompare.Schedule).IsEqualTo("6 6 * * *");
  }

  [Test]
  public async Task TheAliases_ReadAndWriteTheSectionsInCodeAsync() {
    var options = new StreamIntegrityOptions {
      EpochClosureEnabled = false,
      MaxEpochClosuresPerMaintenanceCycle = 3,
      MaxEpochVerificationsPerSweep = 4,
      FullSweepCron = "1 2 * * *",
    };

    await Assert.That(options.Sealing.Enabled).IsFalse();
    await Assert.That(options.EpochClosureEnabled).IsFalse();
    await Assert.That(options.MaxEpochClosuresPerMaintenanceCycle).IsEqualTo(3);
    await Assert.That(options.MaxEpochVerificationsPerSweep).IsEqualTo(4);
    await Assert.That(options.FullSweepCron).IsEqualTo("1 2 * * *");
  }

  // ── validation ───────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Validate_TheDefaults_SucceedAsync() {
    var result = new StreamIntegrityOptionsValidator().Validate(null, new StreamIntegrityOptions());

    await Assert.That(result.Succeeded).IsTrue();
  }

  [Test]
  [Arguments("Sealing:EpochWidth")]
  [Arguments("Sealing:MaxClosuresPerCycle")]
  [Arguments("SealRecheck:MaxEpochsPerRun")]
  [Arguments("SealRecheck:Lookback")]
  [Arguments("SealRecheck:RecheckEvery")]
  public async Task Validate_ALimitBelowItsFloor_FailsNamingTheKeyAsync(string key) {
    var options = new StreamIntegrityOptions();
    switch (key) {
      case "Sealing:EpochWidth": options.Sealing.EpochWidth = 0; break;
      case "Sealing:MaxClosuresPerCycle": options.Sealing.MaxClosuresPerCycle = 0; break;
      case "SealRecheck:MaxEpochsPerRun": options.SealRecheck.MaxEpochsPerRun = 0; break;
      case "SealRecheck:Lookback": options.SealRecheck.Lookback = TimeSpan.Zero; break;
      default: options.SealRecheck.RecheckEvery = TimeSpan.Zero; break;
    }

    var result = new StreamIntegrityOptionsValidator().Validate(null, options);

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:" + key);
  }

  [Test]
  public async Task SealRecheckRequest_From_CarriesTheConfiguredLimitsAsync() {
    var options = new SealRecheckOptions {
      MaxEpochsPerRun = 12,
      Lookback = TimeSpan.FromDays(3),
      RecheckEvery = TimeSpan.FromDays(1),
      OnDrift = SealDriftAction.ReportOnly,
    };

    var request = SealRecheckRequest.From(options, TimeSpan.FromMinutes(60));

    await Assert.That(request).IsEqualTo(new SealRecheckRequest(
      TimeSpan.FromMinutes(60), 12, TimeSpan.FromDays(3), TimeSpan.FromDays(1), Refold: false));
    await Assert.That(SealRecheckRequest.From(new SealRecheckOptions(), TimeSpan.Zero).Refold).IsTrue()
      .Because("the default drift action refolds, as the backstop always did");
  }
}
