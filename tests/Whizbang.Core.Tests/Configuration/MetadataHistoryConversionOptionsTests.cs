// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// #2322: the historical metadata conversion converted a fixed 20,000 rows a cycle, so a database with millions of
/// full-form rows took days. Its budget is configurable under <c>Whizbang:Workers:Maintenance:MetadataHistoryConversion</c>,
/// larger by default, and bounded by a time budget so no cycle holds maintenance for long.
/// </summary>
/// <docs>data/event-store#history-conversion</docs>
/// <tests>src/Whizbang.Core/Configuration/MetadataHistoryConversionOptions.cs</tests>
[Category("Core")]
public class MetadataHistoryConversionOptionsTests {
  [Test]
  public async Task Defaults_AreALargerBoundedBudgetAsync() {
    var options = new MetadataHistoryConversionOptions();

    await Assert.That(options.BatchSize).IsEqualTo(1000);
    await Assert.That(options.MaxBatchesPerCycle).IsEqualTo(200);
    await Assert.That(options.TimeBudget).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task Options_BindFromConfiguration_AndAreValidatedAtStartAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Workers:Maintenance:MetadataHistoryConversion:BatchSize"] = "500",
      ["Whizbang:Workers:Maintenance:MetadataHistoryConversion:MaxBatchesPerCycle"] = "40",
      ["Whizbang:Workers:Maintenance:MetadataHistoryConversion:TimeBudget"] = "00:01:00",
    }).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var bound = provider.GetRequiredService<IOptions<MetadataHistoryConversionOptions>>().Value;

    await Assert.That(bound.BatchSize).IsEqualTo(500);
    await Assert.That(bound.MaxBatchesPerCycle).IsEqualTo(40);
    await Assert.That(bound.TimeBudget).IsEqualTo(TimeSpan.FromMinutes(1));
    await Assert.That(provider.GetServices<IValidateOptions<MetadataHistoryConversionOptions>>()
      .OfType<MetadataHistoryConversionOptionsValidator>().Count()).IsEqualTo(1);
  }

  [Test]
  public async Task Validator_RejectsABudgetThatConvertsNothing_NamingTheKeyAsync() {
    var validator = new MetadataHistoryConversionOptionsValidator();

    var bad = validator.Validate(null, new MetadataHistoryConversionOptions { BatchSize = 0, MaxBatchesPerCycle = 0, TimeBudget = TimeSpan.Zero });
    var good = validator.Validate(null, new MetadataHistoryConversionOptions());

    await Assert.That(bad.Failed).IsTrue();
    await Assert.That(bad.Failures!.Count()).IsEqualTo(3);
    await Assert.That(bad.FailureMessage).Contains("Whizbang:Workers:Maintenance:MetadataHistoryConversion:BatchSize");
    await Assert.That(bad.FailureMessage).Contains("Whizbang:Workers:Maintenance:MetadataHistoryConversion:MaxBatchesPerCycle");
    await Assert.That(bad.FailureMessage).Contains("Whizbang:Workers:Maintenance:MetadataHistoryConversion:TimeBudget");
    await Assert.That(good.Succeeded).IsTrue();
    await Assert.That(() => validator.Validate(null, null!)).Throws<ArgumentNullException>();
  }
}
