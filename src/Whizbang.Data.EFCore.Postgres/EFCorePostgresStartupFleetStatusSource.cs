// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Startup;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The Postgres fleet source for the startup status surface: reads <c>wh_service_instances</c> so
/// the fleet section can report every live instance and how long since each was last heard from, and each
/// instance's assignment (#2595), through the reader the Dapper driver shares (<c>PostgresFleetStatus</c>).
/// Supplied by the driver because the fleet lives in the database and only a driver knows how to
/// reach it; the surface treats a throw as "fleet unavailable", stated with a reason — during a
/// cold-boot migration this table may not exist yet, and that is an honest answer, not an error.
/// </summary>
/// <docs>operations/startup/startup-status</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupFleetStatusSourceTests.cs</tests>
public sealed class EFCorePostgresStartupFleetStatusSource : IStartupFleetStatusSource, IInstanceAssignmentReader {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly Type _dbContextType;

  /// <summary>Creates the source over the consumer's DbContext type, resolved per call from a fresh scope.</summary>
  public EFCorePostgresStartupFleetStatusSource(IServiceScopeFactory scopeFactory, Type dbContextType) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(dbContextType);
    _scopeFactory = scopeFactory;
    _dbContextType = dbContextType;
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<FleetInstanceStatus>> GetFleetAsync(CancellationToken cancellationToken) =>
    _readAsync(Whizbang.Data.Postgres.PostgresFleetStatus.ReadFleetAsync, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<InstanceAssignment>> ReadAssignmentsAsync(CancellationToken cancellationToken) =>
    _readAsync(Whizbang.Data.Postgres.PostgresFleetStatus.ReadAssignmentsAsync, cancellationToken);

  /// <summary>Runs one shared read (#2595) over the context's connection, in the context's schema.</summary>
  private async Task<T> _readAsync<T>(Func<NpgsqlConnection, string, CancellationToken, Task<T>> read, CancellationToken cancellationToken) {
    using var scope = _scopeFactory.CreateScope();
    var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(_dbContextType);
    var schema = dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    await using var connectionScope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
      (NpgsqlConnection)dbContext.Database.GetDbConnection(), cancellationToken).ConfigureAwait(false);
    return await read(connectionScope.Connection, Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(schema), cancellationToken)
      .ConfigureAwait(false);
  }
}
