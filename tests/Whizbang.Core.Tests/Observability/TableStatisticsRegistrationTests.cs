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

  private sealed class EmptyProvider : ITableStatisticsProvider {
    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());

    public Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
  }
}
