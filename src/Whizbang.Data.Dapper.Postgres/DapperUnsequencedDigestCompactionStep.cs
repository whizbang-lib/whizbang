// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The Dapper driver's maintenance step that keeps the held-without-sequence change log bounded (#2351): the same fold
/// the EF Core driver runs; see <see cref="UnsequencedDigestCompaction"/>.
/// </summary>
/// <param name="connectionString">The database the event store is in.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="batchSize">How many changes one fold takes at most.</param>
/// <param name="maxBatchesPerRun">How many folds one run issues at most.</param>
/// <docs>resilience/stream-integrity#the-change-log-stays-bounded</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperUnsequencedDigestCompactionStepTests.cs</tests>
public sealed class DapperUnsequencedDigestCompactionStep(
    string connectionString,
    ILogger<DapperUnsequencedDigestCompactionStep>? logger = null,
    int batchSize = UnsequencedDigestCompaction.BATCH_SIZE,
    int maxBatchesPerRun = UnsequencedDigestCompaction.MAX_BATCHES_PER_RUN) : IMaintenanceStep {

  private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
    ? throw new ArgumentException("A connection string is required.", nameof(connectionString))
    : connectionString;

  /// <inheritdoc />
  public string Name => UnsequencedDigestCompaction.STEP_NAME;

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    // The schema the coordinator writes to: the connection string's Search Path, else the server's answer (#1508).
    var schema = DapperCoordinatorSchema.FromConnectionString(_connectionString)
      ?? await DapperCoordinatorSchema.CurrentAsync(connection, cancellationToken).ConfigureAwait(false);
    await UnsequencedDigestCompaction.RunAsync(
      connection, schema, batchSize, maxBatchesPerRun, logger, cancellationToken).ConfigureAwait(false);
  }
}
