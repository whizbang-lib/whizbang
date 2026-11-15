// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2354: what cleanup and integrity record about their own work rests for a configurable period and is then removed.
/// <c>Whizbang:OperationalRetention:Default</c> is 14 days, and each category can override it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/OperationalRetentionOptions.cs</code-under-test>
public class OperationalRetentionOptionsTests {

  private static OperationalRetentionOptions _bound(Dictionary<string, string?> settings) {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    using var provider = services.BuildServiceProvider();
    return provider.GetRequiredService<IOptions<OperationalRetentionOptions>>().Value;
  }

  [Test]
  public async Task TheDefaults_RestFourteenDays_InBoundedBatchesAsync() {
    var options = new OperationalRetentionOptions();

    await Assert.That(options.Enabled).IsTrue()
      .Because("bookkeeping that never rolls off grows with the system; the safe default removes it");
    await Assert.That(options.Default).IsEqualTo(TimeSpan.FromDays(14));
    await Assert.That(options.BatchSize).IsEqualTo(1000);
    await Assert.That(options.MaxBatchesPerCycle).IsEqualTo(10);
    foreach (var category in Enum.GetValues<OperationalCategory>()) {
      await Assert.That(options.RetentionFor(category)).IsEqualTo(TimeSpan.FromDays(14))
        .Because("a category with no override rests for the default");
    }
  }

  [Test]
  public async Task TheDefaultAndACategoryOverride_BindFromConfigurationAsync() {
    var options = _bound(new Dictionary<string, string?> {
      ["Whizbang:OperationalRetention:Default"] = "3.00:00:00",
      ["Whizbang:OperationalRetention:RewriteLog"] = "30.00:00:00",
      ["Whizbang:OperationalRetention:Queues"] = "06:00:00",
      ["Whizbang:OperationalRetention:BatchSize"] = "50",
      ["Whizbang:OperationalRetention:MaxBatchesPerCycle"] = "2",
      ["Whizbang:OperationalRetention:Enabled"] = "false",
    });

    await Assert.That(options.Enabled).IsFalse();
    await Assert.That(options.RetentionFor(OperationalCategory.RewriteLog)).IsEqualTo(TimeSpan.FromDays(30));
    await Assert.That(options.RetentionFor(OperationalCategory.Queues)).IsEqualTo(TimeSpan.FromHours(6));
    await Assert.That(options.RetentionFor(OperationalCategory.Reports)).IsEqualTo(TimeSpan.FromDays(3))
      .Because("an unset category follows the configured default, not the built-in one");
    await Assert.That(options.BatchSize).IsEqualTo(50);
    await Assert.That(options.MaxBatchesPerCycle).IsEqualTo(2);
  }

  [Test]
  public async Task EveryCategory_HasItsOwnOverrideKeyAsync() {
    foreach (var category in Enum.GetValues<OperationalCategory>()) {
      var options = _bound(new Dictionary<string, string?> {
        [$"Whizbang:OperationalRetention:{category}"] = "1.00:00:00",
      });

      await Assert.That(options.RetentionFor(category)).IsEqualTo(TimeSpan.FromDays(1))
        .Because($"{category} is a documented category, so Whizbang:OperationalRetention:{category} must reach it");
    }
  }

  [Test]
  public async Task Validate_TheDefaults_SucceedAsync() {
    var result = new OperationalRetentionOptionsValidator().Validate(null, new OperationalRetentionOptions());

    await Assert.That(result.Succeeded).IsTrue();
  }

  [Test]
  public async Task Validate_APositiveCategoryOverride_SucceedsAsync() {
    var result = new OperationalRetentionOptionsValidator().Validate(null, new OperationalRetentionOptions { Queues = TimeSpan.FromHours(1) });

    await Assert.That(result.Succeeded).IsTrue();
  }

  [Test]
  [Arguments("Default")]
  [Arguments("RewriteLog")]
  [Arguments("BatchSize")]
  [Arguments("MaxBatchesPerCycle")]
  public async Task Validate_AValueBelowItsFloor_FailsNamingTheKeyAsync(string key) {
    var options = new OperationalRetentionOptions();
    switch (key) {
      case "Default": options.Default = TimeSpan.Zero; break;
      case "RewriteLog": options.RewriteLog = TimeSpan.FromSeconds(-1); break;
      case "BatchSize": options.BatchSize = 0; break;
      default: options.MaxBatchesPerCycle = 0; break;
    }

    var result = new OperationalRetentionOptionsValidator().Validate(null, options);

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains("Whizbang:OperationalRetention:" + key);
  }

  [Test]
  public async Task TheValidator_IsCheckedAtHostStartAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:OperationalRetention:BatchSize"] = "0",
    }).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(() => provider.GetRequiredService<IOptions<OperationalRetentionOptions>>().Value)
      .Throws<OptionsValidationException>();
  }
}
