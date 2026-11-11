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
/// #2250: the blocker report is on by default, the release list is empty by default, and both bind from the documented
/// <c>Whizbang:Ephemeral</c> keys through the turnkey pipeline.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
/// <code-under-test>src/Whizbang.Core/Configuration/EphemeralOptions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Configuration/EphemeralBlockerReportOptions.cs</code-under-test>
[Category("Configuration")]
public class EphemeralBlockerOptionsTests {
  private static ServiceProvider _hostWith(Dictionary<string, string?> settings) {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task Defaults_TheReportIsOn_AndNothingIsReleasedAsync() {
    var options = new EphemeralOptions();

    await Assert.That(options.BlockerReport.Enabled).IsTrue()
      .Because("the report only reads and counts inside the walk; an operator who expected a reclaim needs it without asking");
    await Assert.That(options.ReleaseBlockers).IsEmpty()
      .Because("releasing turns durable events ephemeral; nothing durable changes without an explicit setting");
  }

  [Test]
  public async Task BlockerReport_AndReleaseBlockers_BindFromConfigurationAsync() {
    await using var provider = _hostWith(new Dictionary<string, string?> {
      ["Whizbang:Ephemeral:BlockerReport:Enabled"] = "false",
      ["Whizbang:Ephemeral:ReleaseBlockers:Contracts.Sessions.SessionPinged:0"] = "Contracts.Sessions.SessionOpened, Contracts",
      ["Whizbang:Ephemeral:ReleaseBlockers:Contracts.Sessions.SessionPinged:1"] = "Contracts.Sessions.SessionClosed",
    });

    var options = provider.GetRequiredService<IOptions<EphemeralOptions>>().Value;

    await Assert.That(options.BlockerReport.Enabled).IsFalse()
      .Because("a documented key that nothing reads looks authoritative and fails silently");
    await Assert.That(options.ReleaseBlockers.Count).IsEqualTo(1);
    await Assert.That(options.ReleaseBlockers["Contracts.Sessions.SessionPinged"])
      .IsEquivalentTo(["Contracts.Sessions.SessionOpened, Contracts", "Contracts.Sessions.SessionClosed"]);
  }
}
