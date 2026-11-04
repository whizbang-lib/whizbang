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
}
