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
/// #1792: <see cref="StreamIntegrityOptions.MaxRepairDrainBacklog"/> is rejected below one where the
/// options are read, naming the configuration key.
/// </summary>
/// <remarks>
/// A bound of zero or less would admit no deficit to the paced repair drain at all, so with auto-repair
/// on nothing would ever be repaired and every comparison would log a deferral. That reads as a working
/// drain with an unlucky backlog; failing at host start says what is actually wrong.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamIntegrityOptionsValidator.cs</code-under-test>
public class StreamIntegrityOptionsValidatorTests {

  [Test]
  public async Task Validate_TheDefaults_SucceedAsync() {
    var result = new StreamIntegrityOptionsValidator().Validate(null, new StreamIntegrityOptions());

    await Assert.That(result.Succeeded).IsTrue();
  }

  [Test]
  public async Task Validate_ABacklogBoundOfOne_SucceedsAsync() {
    var result = new StreamIntegrityOptionsValidator().Validate(null, new StreamIntegrityOptions { MaxRepairDrainBacklog = 1 });

    await Assert.That(result.Succeeded).IsTrue()
      .Because("one is the smallest bound that still lets the drain repair anything");
  }

  [Test]
  [Arguments(0)]
  [Arguments(-5)]
  public async Task Validate_ABacklogBoundBelowOne_FailsNamingTheKeyAsync(int bound) {
    var result = new StreamIntegrityOptionsValidator().Validate(null, new StreamIntegrityOptions { MaxRepairDrainBacklog = bound });

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:MaxRepairDrainBacklog");
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task Validate_AnAnswerTimeoutBelowOneMinute_FailsNamingTheKeyAsync(int minutes) {
    var result = new StreamIntegrityOptionsValidator().Validate(null, new StreamIntegrityOptions { RepairAnswerTimeoutMinutes = minutes });

    await Assert.That(result.Failed).IsTrue()
      .Because("a timeout of zero would make every requested bucket claimable again at once, which is the storm (#2029)");
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:RepairAnswerTimeoutMinutes");
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task Validate_AControlMessageTimeToLiveBelowOneMinute_FailsNamingTheKeyAsync(int minutes) {
    var result = new StreamIntegrityOptionsValidator().Validate(
      null, new StreamIntegrityOptions { ControlMessageTimeToLiveMinutes = minutes });

    await Assert.That(result.Failed).IsTrue()
      .Because("#2078: a lifetime below a minute expires a directed request before its addressee can read it");
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:ControlMessageTimeToLiveMinutes");
  }

  [Test]
  public async Task Validate_BothSettingsWrong_NamesBothKeysAsync() {
    var result = new StreamIntegrityOptionsValidator().Validate(
      null, new StreamIntegrityOptions { MaxRepairDrainBacklog = 0, RepairAnswerTimeoutMinutes = 0 });

    await Assert.That(result.FailureMessage).Contains("MaxRepairDrainBacklog");
    await Assert.That(result.FailureMessage).Contains("RepairAnswerTimeoutMinutes");
  }

  [Test]
  public async Task RepairAnswerTimeout_DefaultsToThreeHoursAsync() =>
    await Assert.That(new StreamIntegrityOptions().RepairAnswerTimeoutMinutes).IsEqualTo(180)
      .Because("it covers the origin's maintainer deferral (one hour by default) with room for queue time, and stays well inside the daily audit");

  [Test]
  public async Task Validate_ANullOptionsInstance_ThrowsAsync() =>
    await Assert.That(() => new StreamIntegrityOptionsValidator().Validate(null, null!)).Throws<ArgumentNullException>();

  [Test]
  public async Task TheTurnkeyPipeline_RejectsABacklogBoundBelowOneAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:StreamIntegrity:MaxRepairDrainBacklog"] = "0",
    }).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(() => provider.GetRequiredService<IOptions<StreamIntegrityOptions>>().Value)
      .Throws<OptionsValidationException>()
      .Because("the validator is registered by the pipeline, so a bad bound fails where it is read");
  }

  [Test]
  [Arguments("ReattributionIntervalSeconds")]
  [Arguments("ReattributionBatchSize")]
  [Arguments("MaxReattributionBatchesPerPass")]
  public async Task Validate_AReattributionBoundBelowOne_FailsNamingTheKeyAsync(string key) {
    var options = key switch {
      "ReattributionIntervalSeconds" => new StreamIntegrityOptions { ReattributionIntervalSeconds = 0 },
      "ReattributionBatchSize" => new StreamIntegrityOptions { ReattributionBatchSize = 0 },
      _ => new StreamIntegrityOptions { MaxReattributionBatchesPerPass = 0 },
    };

    var result = new StreamIntegrityOptionsValidator().Validate(null, options);

    await Assert.That(result.Failed).IsTrue()
      .Because("a bound below one would spin the re-attribution loop or walk nothing, a broken setting that reads as a quiet store");
    await Assert.That(result.FailureMessage).Contains($"Whizbang:StreamIntegrity:{key}");
  }

  [Test]
  public async Task Validate_ReattributionBoundsOfOne_SucceedAsync() {
    var result = new StreamIntegrityOptionsValidator().Validate(null, new StreamIntegrityOptions {
      ReattributionIntervalSeconds = 1,
      ReattributionBatchSize = 1,
      MaxReattributionBatchesPerPass = 1,
    });

    await Assert.That(result.Succeeded).IsTrue();
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task Validate_AnAuditWindowBelowOneSequence_FailsNamingTheKeyAsync(int sequences) {
    var result = new StreamIntegrityOptionsValidator().Validate(
      null, new StreamIntegrityOptions { MaxAuditWindowSequences = sequences });

    await Assert.That(result.Failed).IsTrue()
      .Because("#2107: a window below one sequence covers nothing, so no audit could ever advance its seal");
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:MaxAuditWindowSequences");
  }

  [Test]
  public async Task Validate_AnAuditWindowOfOneSequence_SucceedsAsync() {
    var result = new StreamIntegrityOptionsValidator().Validate(
      null, new StreamIntegrityOptions { MaxAuditWindowSequences = 1 });

    await Assert.That(result.Succeeded).IsTrue();
  }

  [Test]
  public async Task MaxAuditWindowSequences_DefaultsToFiveEpochsAsync() =>
    await Assert.That(new StreamIntegrityOptions().MaxAuditWindowSequences).IsEqualTo(500_000)
      .Because("five of the default 100,000-sequence digest epochs: an aligned window answers from its seals");

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task Validate_AnAuditWindowBudgetBelowOne_FailsNamingTheKeyAsync(int windows) {
    var result = new StreamIntegrityOptionsValidator().Validate(
      null, new StreamIntegrityOptions { MaxAuditWindowsPerAudit = windows });

    await Assert.That(result.Failed).IsTrue()
      .Because("#2107: an audit is at least its first window; a budget below one is a broken setting");
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:MaxAuditWindowsPerAudit");
  }

  [Test]
  public async Task MaxAuditWindowsPerAudit_DefaultsToFiveAsync() =>
    await Assert.That(new StreamIntegrityOptions().MaxAuditWindowsPerAudit).IsEqualTo(5)
      .Because("five capped windows is 2.5 million sequences of history compared per audit at the default cap");

  [Test]
  [Arguments("LedgerRecheckIntervalSeconds")]
  [Arguments("LedgerRecheckRowsPerTick")]
  [Arguments("LedgerRecheckAfterMinutes")]
  public async Task Validate_ALedgerRecheckBoundBelowOne_FailsNamingTheKeyAsync(string key) {
    var options = key switch {
      "LedgerRecheckIntervalSeconds" => new StreamIntegrityOptions { LedgerRecheckIntervalSeconds = 0 },
      "LedgerRecheckRowsPerTick" => new StreamIntegrityOptions { LedgerRecheckRowsPerTick = 0 },
      _ => new StreamIntegrityOptions { LedgerRecheckAfterMinutes = 0 },
    };

    var result = new StreamIntegrityOptionsValidator().Validate(null, options);

    await Assert.That(result.Failed).IsTrue()
      .Because("#2115: a re-check bound below one spins the loop, claims nothing, or re-checks a row it just reported");
    await Assert.That(result.FailureMessage).Contains($"Whizbang:StreamIntegrity:{key}");
  }

  [Test]
  public async Task LedgerRecheck_DefaultsAreOn_EveryMinute_FiveHundredRows_TwoHoursAsync() {
    var options = new StreamIntegrityOptions();

    await Assert.That(options.LedgerRecheckEnabled).IsTrue();
    await Assert.That(options.LedgerRecheckIntervalSeconds).IsEqualTo(60);
    await Assert.That(options.LedgerRecheckRowsPerTick).IsEqualTo(500);
    await Assert.That(options.LedgerRecheckAfterMinutes).IsEqualTo(2 * options.AuditSettleWindowMinutes)
      .Because("twice the settle window: a row reported more recently than that was compared over settled history already");
  }

  // ── #2184: the outstanding repair-answer cap ──

  [Test]
  public async Task Validate_ANegativeOutstandingAnswerCap_FailsNamingTheKeyAsync() {
    var result = new StreamIntegrityOptionsValidator().Validate(null, new StreamIntegrityOptions { MaxOutstandingRepairAnswers = -1 });

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:MaxOutstandingRepairAnswers");
  }

  [Test]
  [Arguments(0)]
  [Arguments(1)]
  public async Task Validate_AnOutstandingAnswerCapOfZeroOrMore_SucceedsAsync(int cap) {
    var result = new StreamIntegrityOptionsValidator().Validate(null, new StreamIntegrityOptions { MaxOutstandingRepairAnswers = cap });

    await Assert.That(result.Succeeded).IsTrue().Because("0 turns the cap off; any positive bound is a cap");
  }

  // ── #2185: ledger rows for a type no service owns age out with their origin's silence ──

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task Validate_AnUnownedTypeSilenceBelowOneDay_FailsNamingTheKeyAsync(int days) {
    var result = new StreamIntegrityOptionsValidator().Validate(null, new StreamIntegrityOptions { UnownedTypeOriginSilenceDays = days });

    await Assert.That(result.Failed).IsTrue()
      .Because("below a day, an origin between two checkpoints would read as silent and lose rows it still answers for");
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:UnownedTypeOriginSilenceDays");
  }

  [Test]
  public async Task UnownedTypeOriginSilence_DefaultsToSevenDaysAsync() =>
    await Assert.That(new StreamIntegrityOptions().UnownedTypeOriginSilenceDays).IsEqualTo(7)
      .Because("a week outlasts a weekend outage or a paused deployment, and no live origin is silent that long");
}
