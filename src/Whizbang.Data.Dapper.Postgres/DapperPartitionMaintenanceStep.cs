// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The Dapper driver's partition maintenance step: the same cycle the EF Core driver runs (#1611). It converts each
/// registered table once, creates partitions ahead, and drops partitions wholly past the retention window.
/// </summary>
/// <remarks>
/// Idle unless <c>Whizbang:Workers:Maintenance:Partitioning:Enabled</c> is true, and only on the instance holding the
/// maintainer duty where role assignment manages it; see <c>PartitionMaintenanceStep</c> in the EF Core driver.
/// </remarks>
/// <param name="connectionString">The database the tables are in.</param>
/// <param name="logger">The logger.</param>
/// <param name="timeProvider">The clock partitions are created and expired against.</param>
/// <docs>operations/infrastructure/partitioned-ledgers</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionMaintenanceStepTests.cs</tests>
public sealed class DapperPartitionMaintenanceStep(
    string connectionString,
    ILogger<DapperPartitionMaintenanceStep> logger,
    TimeProvider timeProvider) : IMaintenanceStep {

  // The Dapper driver keeps its tables in the connection's default schema.
  private const string QUOTED_SCHEMA = "\"public\"";

  private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
    ? throw new ArgumentException("A connection string is required.", nameof(connectionString))
    : connectionString;
  private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

  /// <inheritdoc />
  public string Name => "partition-maintenance";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    if (PartitionMaintenance.Plan(services) is not { } plan) {
      return;
    }

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    await PartitionMaintenance.RunAsync(
      connection, QUOTED_SCHEMA, plan, _timeProvider.GetUtcNow(), _logger, cancellationToken).ConfigureAwait(false);
  }
}
