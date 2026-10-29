// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The Dapper driver's maintenance step that seeds the stream lifecycle ledger for the streams the event store held
/// before the ledger existed (#1732): the same backfill the EF Core driver runs; see <see cref="StreamLifecycleBackfill"/>.
/// </summary>
/// <remarks>
/// The step finds the schema the way the Dapper work coordinator does: the connection string's <c>Search Path</c>, else
/// the server's <c>current_schema()</c>. Idle once the backfill is recorded as done, which a new database is from its first start: one keyed read per cycle.
/// Until then each cycle runs a bounded number of bounded batches, each its own transaction, and an instance that finds
/// another walking the cursor steps aside for the cycle.
/// </remarks>
/// <param name="connectionString">The database the event store is in.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="batchSize">The most streams one batch visits.</param>
/// <param name="maxRoundsPerRun">The most batches one cycle runs.</param>
/// <docs>fundamentals/events/stream-end-of-life#the-one-time-backfill</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamLifecycleLedgerTests.cs</tests>
public sealed class DapperStreamLifecycleBackfillMaintenanceStep(
    string connectionString,
    ILogger<DapperStreamLifecycleBackfillMaintenanceStep>? logger = null,
    int batchSize = StreamLifecycleBackfill.DEFAULT_BATCH_SIZE,
    int maxRoundsPerRun = StreamLifecycleBackfill.DEFAULT_MAX_ROUNDS) : IMaintenanceStep {

  private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
    ? throw new ArgumentException("A connection string is required.", nameof(connectionString))
    : connectionString;

  /// <inheritdoc />
  public string Name => "stream-lifecycle-backfill";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    // The schema the coordinator writes to: the connection string's Search Path, else the server's answer (#1508).
    var schema = DapperCoordinatorSchema.FromConnectionString(_connectionString)
      ?? await DapperCoordinatorSchema.CurrentAsync(connection, cancellationToken).ConfigureAwait(false);
    await StreamLifecycleBackfill.RunAsync(
      connection, PgIdentifier.Quote(schema), batchSize, maxRoundsPerRun, logger, cancellationToken).ConfigureAwait(false);
  }
}
