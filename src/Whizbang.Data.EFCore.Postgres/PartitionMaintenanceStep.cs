// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The maintenance step that keeps the framework's time-partitioned tables: it converts each registered table once,
/// creates partitions ahead, and drops partitions wholly past the retention window (#1611).
/// </summary>
/// <remarks>
/// <para>
/// Idle unless <c>Whizbang:Workers:Maintenance:Partitioning:Enabled</c> is true. Partition DDL runs only on the
/// instance holding the maintainer duty, where role assignment manages it; elsewhere the SQL's per-table advisory lock
/// lets one instance through and the others skip. Changing a table needs its owner's rights, so the step runs on the
/// schema-initialization connection when one is configured, as the startup migrations do, and on the context's own
/// connection otherwise.
/// </para>
/// </remarks>
/// <param name="dbContextType">The DbContext whose schema holds the tables.</param>
/// <param name="logger">The logger.</param>
/// <param name="timeProvider">The clock partitions are created and expired against.</param>
/// <docs>operations/infrastructure/partitioned-ledgers</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintenanceStepTests.cs</tests>
public sealed class PartitionMaintenanceStep(
    Type dbContextType,
    ILogger<PartitionMaintenanceStep> logger,
    TimeProvider timeProvider) : IMaintenanceStep {

  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));
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

    var context = (DbContext)services.GetRequiredService(_dbContextType);
    var schema = context.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    var quotedSchema = PgIdentifier.Quote(string.IsNullOrWhiteSpace(schema) ? "public" : schema);
    var now = _timeProvider.GetUtcNow();

    if (SchemaInitConnection.For(services, _dbContextType) is { } initConnectionString) {
      await _runOnInitConnectionAsync(initConnectionString, quotedSchema, plan, now, cancellationToken).ConfigureAwait(false);
      return;
    }

    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    try {
      await PartitionMaintenance.RunAsync(
        (NpgsqlConnection)context.Database.GetDbConnection(), quotedSchema, plan, now, _logger,
        cancellationToken).ConfigureAwait(false);
    } finally {
      await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
  }

  private async Task _runOnInitConnectionAsync(
      string connectionString, string quotedSchema, PartitionMaintenancePlan plan, DateTimeOffset now, CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    await PartitionMaintenance.RunAsync(connection, quotedSchema, plan, now, _logger, cancellationToken).ConfigureAwait(false);
  }
}
