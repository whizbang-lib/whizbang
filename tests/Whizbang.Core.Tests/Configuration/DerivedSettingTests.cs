// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// A setting computed from something that varies from host to host (memory, processor count, pool size) carries the
/// inputs it was computed from, so the startup settings report can say why the value is what it is.
/// </summary>
public class DerivedSettingTests {
  [Test]
  public async Task FractionOf_MultipliesTheInput_AndRecordsItAsync() {
    var setting = DerivedSetting.FractionOf("Whizbang:BatchBuffer:MaxBufferedBytes", 0.05, "TotalAvailableMemoryBytes", 1_000_000_000);

    await Assert.That(setting.Value).IsEqualTo(50_000_000L);
    await Assert.That(setting.Name).IsEqualTo("Whizbang:BatchBuffer:MaxBufferedBytes");
    await Assert.That(setting.Rule).IsEqualTo("0.05 x TotalAvailableMemoryBytes");
    await Assert.That(setting.Inputs).IsEquivalentTo([new SettingInput("TotalAvailableMemoryBytes", "1000000000")]);
    await Assert.That(setting.DisplayValue).IsEqualTo("50000000");
  }

  [Test]
  [Arguments(8, 1, 50, 8, "8")]
  [Arguments(80, 1, 50, 50, "80 clamped to [1, 50]")]
  [Arguments(0, 1, 50, 1, "0 clamped to [1, 50]")]
  public async Task Clamp_KeepsTheValueInRange_AndSaysWhenItClampedAsync(int requested, int min, int max, int expected, string rule) {
    var setting = DerivedSetting.Clamp("Whizbang:Workers:Width", requested, min, max, SettingInput.Of("GateMaxConcurrent", max));

    await Assert.That(setting.Value).IsEqualTo(expected);
    await Assert.That(setting.Rule).IsEqualTo(rule);
    await Assert.That(setting.Inputs).IsEquivalentTo([new SettingInput("GateMaxConcurrent", max.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
  }

  [Test]
  public async Task Of_RecordsAnyRuleAndItsInputsAsync() {
    var setting = DerivedSetting.Of("ThreadPoolFloor", 12, "ProcessorCount", SettingInput.Of("ProcessorCount", 12));

    await Assert.That(setting.Value).IsEqualTo(12);
    await Assert.That(setting.Rule).IsEqualTo("ProcessorCount");
    await Assert.That(setting.ToString()).IsEqualTo("12 (ProcessorCount; ProcessorCount=12)");
  }

  [Test]
  public async Task Describe_WritesTheRuleAndInputs_OrTheRuleAloneAsync() {
    var withInputs = DerivedSetting.FractionOf("Whizbang:A:B", 0.5, "Pool", 10);
    var alone = DerivedSetting.Of("ThreadPoolFloor", 12, "ProcessorCount");

    await Assert.That(DerivedSetting.Describe(withInputs)).IsEqualTo("0.5 x Pool; Pool=10");
    await Assert.That(DerivedSetting.Describe(alone)).IsEqualTo("ProcessorCount");
    await Assert.That(alone.ToString()).IsEqualTo("12 (ProcessorCount)");
    await Assert.That(() => DerivedSetting.Describe(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task SettingInput_Of_FormatsWithTheInvariantCultureAsync() {
    await Assert.That(SettingInput.Of("Fraction", 0.5).Value).IsEqualTo("0.5");
  }

  [Test]
  public async Task Factories_RefuseAMissingNameAsync() {
    await Assert.That(() => DerivedSetting.Of<int>(null!, 1, "r")).Throws<ArgumentException>();
    await Assert.That(() => DerivedSetting.Of<int>("n", 1, " ")).Throws<ArgumentException>();
    await Assert.That(() => SettingInput.Of<int>("", 1)).Throws<ArgumentException>();
  }
}
