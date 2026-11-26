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
/// #2317: the gauge once named <c>repair_progress_percent</c> is <c>100 * closed / opened</c> over the window, a ratio of
/// two rates that says whether the backlog shrinks, not how much of the repair is done. Its switch is
/// <c>RepairCloseRatioGaugeEnabled</c>, and the window it and the ETA share is <c>CompletionWindowMinutes</c>. The old keys
/// (<c>RepairProgressGaugeEnabled</c>, <c>RepairProgressWindowMinutes</c>) keep binding for one release, and a new key
/// wins over its alias when both are set.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityCheckpoint.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamIntegrityOptionsValidator.cs</code-under-test>
/// <docs>resilience/stream-integrity#derived-completion-meters</docs>
public class RepairCloseRatioOptionsTests {

  private static StreamIntegrityOptions _bound(Dictionary<string, string?> settings) {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    using var provider = services.BuildServiceProvider();
    return provider.GetRequiredService<IOptions<StreamIntegrityOptions>>().Value;
  }

  [Test]
  public async Task Defaults_TheCloseRatioIsOn_OverAnHourAsync() {
    var options = new StreamIntegrityOptions();

    await Assert.That(options.RepairCloseRatioGaugeEnabled).IsTrue();
    await Assert.That(options.CompletionWindowMinutes).IsEqualTo(60);
  }

  [Test]
  public async Task TheNewKeys_BindAsync() {
    var options = _bound(new Dictionary<string, string?> {
      ["Whizbang:StreamIntegrity:RepairCloseRatioGaugeEnabled"] = "false",
      ["Whizbang:StreamIntegrity:CompletionWindowMinutes"] = "15",
    });

    await Assert.That(options.RepairCloseRatioGaugeEnabled).IsFalse();
    await Assert.That(options.CompletionWindowMinutes).IsEqualTo(15);
  }

  [Test]
  public async Task TheOldKeys_StillTurnTheGaugeOff_AndStillSetTheWindowAsync() {
    var options = _bound(new Dictionary<string, string?> {
      ["Whizbang:StreamIntegrity:RepairProgressGaugeEnabled"] = "false",
      ["Whizbang:StreamIntegrity:RepairProgressWindowMinutes"] = "30",
    });

    await Assert.That(options.RepairCloseRatioGaugeEnabled).IsFalse()
      .Because("a host that switched the gauge off with the old key must not find it back on after the upgrade");
    await Assert.That(options.CompletionWindowMinutes).IsEqualTo(30)
      .Because("the old window key still sets the window the ratio and the ETA are computed over");
  }

  [Test]
  public async Task ANewKey_WinsOverItsAlias_WhenBothAreSetAsync() {
    var options = _bound(new Dictionary<string, string?> {
      ["Whizbang:StreamIntegrity:RepairProgressGaugeEnabled"] = "false",
      ["Whizbang:StreamIntegrity:RepairCloseRatioGaugeEnabled"] = "true",
      ["Whizbang:StreamIntegrity:RepairProgressWindowMinutes"] = "30",
      ["Whizbang:StreamIntegrity:CompletionWindowMinutes"] = "15",
    });

    await Assert.That(options.RepairCloseRatioGaugeEnabled).IsTrue()
      .Because("the new key is the documented one; an alias left behind in an old file must not override it");
    await Assert.That(options.CompletionWindowMinutes).IsEqualTo(15);
  }

  [Test]
  public async Task TheAliases_ReadAndWriteTheNewSettingsInCodeAsync() {
#pragma warning disable CS0618 // the aliases under test
    var options = new StreamIntegrityOptions { RepairProgressGaugeEnabled = false, RepairProgressWindowMinutes = 5 };

    await Assert.That(options.RepairCloseRatioGaugeEnabled).IsFalse();
    await Assert.That(options.CompletionWindowMinutes).IsEqualTo(5);
    await Assert.That(options.RepairProgressGaugeEnabled).IsFalse();
    await Assert.That(options.RepairProgressWindowMinutes).IsEqualTo(5);
#pragma warning restore CS0618
  }

  [Test]
  public async Task Validate_AWindowBelowOneMinute_FailsNamingTheNewKeyAsync() {
    var result = new StreamIntegrityOptionsValidator().Validate(null, new StreamIntegrityOptions { CompletionWindowMinutes = 0 });

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:CompletionWindowMinutes");
  }
}
