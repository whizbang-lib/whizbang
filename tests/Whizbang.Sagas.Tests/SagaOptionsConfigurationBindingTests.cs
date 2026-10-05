// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Sagas;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// #1014: <see cref="SagaOptions"/> binds from <c>Whizbang:Sagas</c>, so the watchdog and the
/// stranded-saga sweep can be tuned at deploy time. The per-item stream namespace is the exception:
/// it is stream identity, fixed when the sagas are registered, and never read from configuration.
/// </summary>
/// <code-under-test>src/Whizbang.Sagas/SagaServiceCollectionExtensions.cs</code-under-test>
/// <docs>operations/configuration/configuration-reference</docs>
[Category("Unit")]
[Category("Saga")]
[NotInParallel("AppDefaultNamespace")]
public class SagaOptionsConfigurationBindingTests {

  private static ServiceProvider _build(Dictionary<string, string?> settings, Action<SagaOptions>? configure = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    services.AddSingleton(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    services.AddWhizbangSagas(configure);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task EveryKeyBindsAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      await using var provider = _build(new() {
        ["Whizbang:Sagas:MinWatchdogDelay"] = "00:00:10",
        ["Whizbang:Sagas:MaxWatchdogDelay"] = "01:00:00",
        ["Whizbang:Sagas:WatchdogSafetyMargin"] = "00:00:05",
        ["Whizbang:Sagas:MaxConsecutiveStalls"] = "9",
        ["Whizbang:Sagas:StallBackoffMultiplier"] = "1.5",
        ["Whizbang:Sagas:StrandedSagaIdleGuard"] = "00:10:00",
        ["Whizbang:Sagas:StrandedSagaRearmInterval"] = "00:20:00",
        ["Whizbang:Sagas:ClaimRetention"] = "3.00:00:00",
      });

      var options = provider.GetRequiredService<SagaOptions>();

      await Assert.That(options.MinWatchdogDelay).IsEqualTo(TimeSpan.FromSeconds(10));
      await Assert.That(options.MaxWatchdogDelay).IsEqualTo(TimeSpan.FromHours(1));
      await Assert.That(options.WatchdogSafetyMargin).IsEqualTo(TimeSpan.FromSeconds(5));
      await Assert.That(options.MaxConsecutiveStalls).IsEqualTo(9);
      await Assert.That(options.StallBackoffMultiplier).IsEqualTo(1.5);
      await Assert.That(options.StrandedSagaIdleGuard).IsEqualTo(TimeSpan.FromMinutes(10));
      await Assert.That(options.StrandedSagaRearmInterval).IsEqualTo(TimeSpan.FromMinutes(20));
      await Assert.That(options.ClaimRetention).IsEqualTo(TimeSpan.FromDays(3));
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  [Test]
  public async Task ConfigurationOverridesCode_AndCodeSurvivesAbsentKeysAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      await using var provider = _build(new() {
        ["Whizbang:Sagas:MaxConsecutiveStalls"] = "2",
      }, o => {
        o.MaxConsecutiveStalls = 7;
        o.MinWatchdogDelay = TimeSpan.FromSeconds(3);
      });

      var options = provider.GetRequiredService<SagaOptions>();

      await Assert.That(options.MaxConsecutiveStalls).IsEqualTo(2);
      await Assert.That(options.MinWatchdogDelay).IsEqualTo(TimeSpan.FromSeconds(3));
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  [Test]
  public async Task PerItemStreamNamespace_IsNeverReadFromConfigurationAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      var code = Guid.Parse("0b36f8d4-3884-4c3c-b92b-fc6ec74775ea");
      await using var provider = _build(new() {
        ["Whizbang:Sagas:PerItemStreamNamespace"] = "11111111-1111-1111-1111-111111111111",
      }, o => o.PerItemStreamNamespace = code);

      var options = provider.GetRequiredService<SagaOptions>();

      await Assert.That(options.PerItemStreamNamespace).IsEqualTo(code)
        .Because("the namespace is stream identity; a configuration key changing it would re-route every per-item stream");
      await Assert.That(SagaItemStreams.AppDefaultNamespace).IsEqualTo(code);
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  [Test]
  public async Task NoConfigurationRegistered_ResolvesDefaultsAsync() {
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      var services = new ServiceCollection();
      services.AddSingleton(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
      services.AddWhizbangSagas();
      await using var provider = services.BuildServiceProvider();

      await Assert.That(provider.GetRequiredService<SagaOptions>().MaxConsecutiveStalls).IsEqualTo(4);
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }
}
