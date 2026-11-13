// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Minting;

/// <summary>
/// The service-wide composite batch settings (#2285): bound from <c>Whizbang:CompositeBatches</c>, with a byte limit that,
/// when not set, is derived as three quarters of the body-offload threshold (which sits below the transport's largest
/// message), and reported with its inputs in the startup settings report.
/// </summary>
public class CompositeBatchDefaultsTests {
  private static ServiceProvider _provider(IReadOnlyDictionary<string, string?>? settings = null, bool offload = false) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());
    services.AddWhizbang();
    if (offload) {
      services.AddWhizbangBodyOffload();
    }
    return services.BuildServiceProvider();
  }

  private static IDerivedSetting[] _reported(IServiceProvider provider) =>
    [.. provider.GetServices<IDerivedSettingSource>().SelectMany(s => s.DerivedSettings)
      .Where(d => d.Name == "Whizbang:CompositeBatches:MaxBufferedBytes")];

  [Test]
  public async Task TheSection_BindsEverySetting_AndAConfiguredByteLimitIsNotDerivedAsync() {
    await using var provider = _provider(new Dictionary<string, string?> {
      ["Whizbang:CompositeBatches:MaxBufferedMessages"] = "250",
      ["Whizbang:CompositeBatches:MaxBufferedBytes"] = "20000",
      ["Whizbang:CompositeBatches:DebounceInterval"] = "00:00:00.250",
      ["Whizbang:CompositeBatches:MaxWait"] = "00:00:05",
    });

    var options = provider.GetRequiredService<IOptions<CompositeBatchOptions>>().Value;

    await Assert.That(options.MaxBufferedMessages).IsEqualTo(250);
    await Assert.That(options.MaxBufferedBytes).IsEqualTo(20000);
    await Assert.That(options.DebounceInterval).IsEqualTo(TimeSpan.FromMilliseconds(250));
    await Assert.That(options.MaxWait).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(_reported(provider)).IsEmpty();
  }

  [Test]
  public async Task AnUnsetByteLimit_IsThreeQuartersOfTheOffloadThreshold_AndIsReportedAsync() {
    await using var provider = _provider();

    var options = provider.GetRequiredService<IOptions<CompositeBatchOptions>>().Value;

    await Assert.That(options.MaxBufferedBytes).IsEqualTo(48L * 1024L);
    await Assert.That(_reported(provider).Single().ToString()).IsEqualTo("49152 (0.75 x SizeThresholdBytes; SizeThresholdBytes=65536)");
  }

  [Test]
  public async Task AConfiguredOffloadThreshold_SetsTheByteLimitAsync() {
    await using var provider = _provider(new Dictionary<string, string?> { ["Whizbang:BodyOffload:SizeThresholdBytes"] = "40960" }, offload: true);

    await Assert.That(provider.GetRequiredService<IOptions<CompositeBatchOptions>>().Value.MaxBufferedBytes).IsEqualTo(30720);
  }

  [Test]
  public async Task TheDerivation_IsThreeQuartersOfTheThreshold_WithItsInputAsync() {
    var derived = CompositeBatchDefaults.Derive(64L * 1024L);

    await Assert.That(derived.Value).IsEqualTo(49152);
    await Assert.That(derived.Inputs.Select(i => i.Name)).IsEquivalentTo(["SizeThresholdBytes"]);
  }

  [Test]
  public async Task WithoutConfiguration_TheCodeDefaultsApplyAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbang();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<CompositeBatchOptions>>().Value;

    await Assert.That(options.MaxBufferedMessages).IsNull();
    await Assert.That(options.DebounceInterval).IsEqualTo(TimeSpan.FromMilliseconds(100));
  }

  [Test]
  public async Task AHostWithNoConfiguration_KeepsTheCodeDefaultsAsync() {
    // A service built with no IConfiguration (a bare container) binds nothing: every value is the code default.
    var options = new CompositeBatchOptions { MaxBufferedMessages = 7 };

    new ConfigureCompositeBatchOptions(configuration: null).Configure(options);

    await Assert.That(options.MaxBufferedMessages).IsEqualTo(7);
    await Assert.That(options.DebounceInterval).IsEqualTo(new CompositeBatchOptions().DebounceInterval);
  }
}
