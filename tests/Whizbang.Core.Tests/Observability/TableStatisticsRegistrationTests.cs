// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The registration both data drivers call: the provider they supply, the meter, the collector, and the
/// <c>Whizbang:TableStatistics</c> settings.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsOptions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsOptionsValidator.cs</code-under-test>
/// <docs>operations/observability/metrics#table-statistics</docs>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsRegistrationTests {
  [Test]
  public async Task MaxTrackedTables_BindsFromTheTableStatisticsSectionAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:TableStatistics:MaxTrackedTables"] = "42",
    }).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    TableStatisticsOptions? seen = null;
    services.AddWhizbangTableStatistics((_, options) => {
      seen = options;
      return new EmptyProvider();
    });
    await using var provider = services.BuildServiceProvider();

    _ = provider.GetRequiredService<ITableStatisticsProvider>();

    await Assert.That(provider.GetRequiredService<IOptions<TableStatisticsOptions>>().Value.MaxTrackedTables).IsEqualTo(42)
      .Because("a documented key that binds to nothing is a setting that silently does nothing (#646)");
    await Assert.That(seen?.MaxTrackedTables).IsEqualTo(42)
      .Because("the cap is applied by the driver's provider, so the provider has to be built with the bound value");
  }

  [Test]
  public async Task WithoutConfiguration_TheCapIsFiveHundredAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangTableStatistics((_, _) => new EmptyProvider());
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<TableStatisticsOptions>>().Value.MaxTrackedTables)
      .IsEqualTo(500)
      .Because("the documented default bounds the series at a size every metrics backend handles");
  }

  [Test]
  public async Task RegisteringTwice_RunsOneCollectorAsync() {
    // Two registrations (a host that names both drivers, or a driver registered twice) must not run two
    // collectors: each would query the catalog every cycle and write the same caches.
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangTableStatistics((_, _) => new EmptyProvider());
    services.AddWhizbangTableStatistics((_, _) => new EmptyProvider());
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetServices<IHostedService>().OfType<TableStatisticsCollector>().Count()).IsEqualTo(1);
    await Assert.That(provider.GetServices<IConfigureOptions<TableStatisticsOptions>>().Count()).IsEqualTo(1);
    await Assert.That(provider.GetService<TableStatisticsMetrics>()).IsNotNull()
      .Because("the collector writes to the meter, so registering the collector registers the meter");
  }

  [Test]
  public async Task AProviderTheHostRegistered_IsKeptAsync() {
    var mine = new EmptyProvider();
    var services = new ServiceCollection();
    services.AddSingleton<ITableStatisticsProvider>(mine);
    services.AddWhizbangTableStatistics((_, _) => throw new InvalidOperationException("the host's provider wins"));
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<ITableStatisticsProvider>()).IsSameReferenceAs(mine);
  }

  // ==== #1590: bytes per event type, sampled ====

  [Test]
  public async Task TheEventTypeSample_BindsFromTheTableStatisticsSectionAsync() {
    var options = _bound(new Dictionary<string, string?> {
      ["Whizbang:TableStatistics:EventTypeSampleInterval"] = "00:05:00",
      ["Whizbang:TableStatistics:EventTypeSamplePercent"] = "2.5",
      ["Whizbang:TableStatistics:MaxEventTypes"] = "7",
    });

    await Assert.That(options.EventTypeSampleInterval).IsEqualTo(TimeSpan.FromMinutes(5));
    await Assert.That(options.EventTypeSamplePercent).IsEqualTo(2.5);
    await Assert.That(options.MaxEventTypes).IsEqualTo(7)
      .Because("a documented key that binds to nothing is a setting that silently does nothing (#646)");
  }

  [Test]
  public async Task WithoutConfiguration_OnePercentEveryFifteenMinutes_FiftyTypesAsync() {
    var options = _bound([]);

    await Assert.That(options.EventTypeSampleInterval).IsEqualTo(TimeSpan.FromMinutes(15));
    await Assert.That(options.EventTypeSamplePercent).IsEqualTo(1d);
    await Assert.That(options.MaxEventTypes).IsEqualTo(50);
  }

  [Test]
  [Arguments("EventTypeSampleInterval", "-00:00:01")]
  [Arguments("EventTypeSamplePercent", "0")]
  [Arguments("EventTypeSamplePercent", "100.5")]
  [Arguments("MaxEventTypes", "0")]
  [Arguments("MaxTrackedTables", "0")]
  [Arguments("CollectionInterval", "00:00:00")]
  [Arguments("CollectionInterval", "-00:00:01")]
  public async Task AValueNoReadingCouldHonor_IsRefusedNamingItsKeyAsync(string setting, string value) {
    var exception = Assert.Throws<OptionsValidationException>(() => _bound(new Dictionary<string, string?> {
      ["Whizbang:TableStatistics:" + setting] = value,
    }));

    await Assert.That(exception.Message).Contains("Whizbang:TableStatistics:" + setting)
      .Because("the operator has to be told which key to fix, at start rather than on the first sample");
  }

  [Test]
  public async Task AZeroInterval_IsAcceptedAsTheWayToTurnTheSampleOffAsync() {
    var options = _bound(new Dictionary<string, string?> {
      ["Whizbang:TableStatistics:EventTypeSampleInterval"] = "00:00:00",
      ["Whizbang:TableStatistics:EventTypeSamplePercent"] = "100",
    });

    await Assert.That(options.EventTypeSampleInterval).IsEqualTo(TimeSpan.Zero);
    await Assert.That(options.EventTypeSamplePercent).IsEqualTo(100d)
      .Because("a full sample is the largest share there is, and still a share");
  }

  [Test]
  public async Task RegisteringTwice_RunsOneSamplerAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangTableStatistics((_, _) => new EmptyProvider());
    services.AddWhizbangTableStatistics((_, _) => new EmptyProvider());
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetServices<IHostedService>().OfType<EventTypeBytesSampler>().Count()).IsEqualTo(1)
      .Because("two samplers would each try to claim the same window, and one is all a fleet needs");
    await Assert.That(provider.GetServices<IValidateOptions<TableStatisticsOptions>>().Count()).IsEqualTo(1);
  }

  // ==== #1668: the collection interval is a setting ====

  [Test]
  public async Task TheCollectionInterval_BindsFromTheTableStatisticsSectionAsync() {
    var options = _bound(new Dictionary<string, string?> {
      ["Whizbang:TableStatistics:CollectionInterval"] = "00:02:00",
    });

    await Assert.That(options.CollectionInterval).IsEqualTo(TimeSpan.FromMinutes(2))
      .Because("a documented key that binds to nothing is a setting that silently does nothing (#646)");
  }

  [Test]
  public async Task WithoutConfiguration_TheTablesAreReadEveryThirtySecondsAsync() {
    var options = _bound([]);

    await Assert.That(options.CollectionInterval).IsEqualTo(TimeSpan.FromSeconds(30))
      .Because("the default is the interval the collector always used, so nothing changes for a host that sets nothing");
  }

  private static TableStatisticsOptions _bound(Dictionary<string, string?> settings) {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    services.AddWhizbangTableStatistics((_, _) => new EmptyProvider());
    using var provider = services.BuildServiceProvider();
    return provider.GetRequiredService<IOptions<TableStatisticsOptions>>().Value;
  }

  private sealed class EmptyProvider : ITableStatisticsProvider {
    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());

    public Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
  }
}
