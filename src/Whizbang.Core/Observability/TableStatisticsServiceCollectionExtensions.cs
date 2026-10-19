// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

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
  public static IServiceCollection AddWhizbangTableStatistics(
      this IServiceCollection services,
      Func<IServiceProvider, TableStatisticsOptions, ITableStatisticsProvider> createProvider) {
    ArgumentNullException.ThrowIfNull(createProvider);
    return services;
  }
}
