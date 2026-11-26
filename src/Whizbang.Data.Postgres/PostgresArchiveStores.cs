// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Archival;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The archive store both Postgres drivers register (#2503): the archive coordinator writing through the selected
/// <see cref="IArchiveObjectStore"/> when a storage provider selected one, otherwise the built-in table store. Either way
/// the database half is the driver's, and a storage provider only moves objects.
/// </summary>
/// <docs>fundamentals/events/archive-store#object-stores-and-the-coordinator</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ObjectArchiveCoordinatorTests.cs:Registration_TheDriverRegistersTheCoordinator_WhenAnObjectStoreIsSelected_ElseTheTableStoreAsync</tests>
internal static class PostgresArchiveStores {
  /// <summary>Builds the driver's archive store from <paramref name="services"/>.</summary>
  /// <param name="services">The service provider.</param>
  /// <returns>The coordinator, or the table store.</returns>
  internal static IArchiveStore Create(IServiceProvider services) {
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    if (ArchiveObjectStoreSelection.Resolve(services) is { } objects) {
      return new PostgresObjectArchiveStore(
        database,
        objects,
        services.GetService<ILogger<PostgresObjectArchiveStore>>(),
        services.GetService<TimeProvider>(),
        services.GetService<WhizbangMetrics>() is { } metrics ? new ArchiveSweepMetrics(metrics) : null);
    }
    return new PostgresTableArchiveStore(database.Open, database.Schema, services.GetService<ILogger<PostgresTableArchiveStore>>());
  }
}
