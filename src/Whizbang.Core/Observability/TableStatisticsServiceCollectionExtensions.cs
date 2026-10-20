// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Observability;

/// <summary>
/// Registers the table statistics a data driver reports: the provider it supplies, the meter, the
/// collector that refreshes the meter, and the <c>Whizbang:TableStatistics</c> settings.
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsRegistrationTests.cs</tests>
public static class TableStatisticsServiceCollectionExtensions {
  /// <summary>
  /// Registers the table statistics meter and its collector over the provider
  /// <paramref name="createProvider"/> builds.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="createProvider">
  /// Builds the driver's provider, given the container and the bound settings. A provider the host
  /// registered first is kept.
  /// </param>
  /// <returns>The service collection, for chaining.</returns>
  /// <remarks>
  /// Idempotent: a second call (both drivers in one host, or a driver registered twice) adds nothing,
  /// so one collector queries the catalog per cycle. Everything is registered by factory, so nothing
  /// is constructed reflectively, and the settings bind through the configuration binder source
  /// generator.
  /// </remarks>
  public static IServiceCollection AddWhizbangTableStatistics(
      this IServiceCollection services,
      Func<IServiceProvider, TableStatisticsOptions, ITableStatisticsProvider> createProvider) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(createProvider);

    services.AddOptions<TableStatisticsOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<TableStatisticsOptions>, ConfigureOptions<TableStatisticsOptions>>(sp => {
      var configuration = sp.GetService<IConfiguration>();
      return new ConfigureOptions<TableStatisticsOptions>(options => {
        if (configuration is not null) {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
          configuration.GetSection("Whizbang:TableStatistics").Bind(options);
#pragma warning restore IL2026
        }
      });
    }));

    services.AddMetrics();
    services.TryAddSingleton(sp => new WhizbangMetrics(sp.GetRequiredService<IMeterFactory>()));
    services.TryAddSingleton(sp => new TableStatisticsMetrics(sp.GetRequiredService<WhizbangMetrics>()));
    services.TryAddSingleton(sp => createProvider(sp, sp.GetRequiredService<IOptions<TableStatisticsOptions>>().Value));

    // The collector tolerates a host with no logging and no schema gate (it logs to nothing and
    // starts at once), so neither is required here.
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TableStatisticsCollector>(sp =>
      new TableStatisticsCollector(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<TableStatisticsMetrics>(),
        sp.GetService<ISchemaReadyGate>()!,
        sp.GetService<ILogger<TableStatisticsCollector>>()!)));

    return services;
  }
}
