// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The Dapper driver's fleet source for the startup status surface and its instance-assignment reader (#2595): the same
/// shared reader the EF Core driver uses (<see cref="PostgresFleetStatus"/>), over this driver's connection string and
/// schema, so the fleet section, the stand-by handshake and the assignment answer alike on either driver.
/// </summary>
/// <param name="connectionString">The database.</param>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceAssignmentDriverParityTests.cs</tests>
internal sealed class DapperPostgresStartupFleetStatusSource(string connectionString)
    : IStartupFleetStatusSource, IInstanceAssignmentReader {
  /// <inheritdoc />
  public Task<IReadOnlyList<FleetInstanceStatus>> GetFleetAsync(CancellationToken cancellationToken) =>
    _readAsync(PostgresFleetStatus.ReadFleetAsync, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<InstanceAssignment>> ReadAssignmentsAsync(CancellationToken cancellationToken) =>
    _readAsync(PostgresFleetStatus.ReadAssignmentsAsync, cancellationToken);

  private async Task<T> _readAsync<T>(Func<NpgsqlConnection, string, CancellationToken, Task<T>> read, CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    var schema = DapperCoordinatorSchema.FromConnectionString(connectionString)
      ?? await DapperCoordinatorSchema.CurrentAsync(connection, cancellationToken).ConfigureAwait(false);
    return await read(connection, PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
  }
}
