// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>One thing a partition maintenance cycle did to one table.</summary>
/// <param name="Table">The table.</param>
/// <param name="Action">
/// What happened: <c>converted</c>; or, per partition, <c>created</c>, <c>dropped</c>, <c>emptied</c> (expired but the
/// table was in use, so only truncated), <c>deferred</c> (a lock was not free), <c>kept</c> (debug mode), or
/// <c>skipped</c> (the table is not partitioned, or another instance is maintaining it).
/// </param>
/// <param name="Partition">The partition, or the table itself for <c>converted</c> and <c>skipped</c>.</param>
/// <docs>operations/infrastructure/partitioned-ledgers#what-a-cycle-does</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionMaintenanceStepTests.cs</tests>
internal readonly record struct PartitionMaintenanceResult(string Table, string Action, string Partition);

/// <summary>What one instance's partition cycle covers.</summary>
/// <param name="CreateAhead">How many intervals past the current one to create.</param>
/// <param name="Tables">Every distinct registered table, in registration order.</param>
/// <docs>operations/infrastructure/partitioned-ledgers#what-a-cycle-does</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionMaintenanceStepTests.cs</tests>
internal sealed record PartitionMaintenancePlan(int CreateAhead, IReadOnlyList<PartitionedTable> Tables);

/// <summary>
/// The partition manager's cycle, shared by both drivers' maintenance steps: whether it runs, which tables, and the
/// two SQL calls per table (<c>wh_partition_convert</c>, then <c>wh_partition_maintain</c>).
/// </summary>
/// <docs>operations/infrastructure/partitioned-ledgers</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionMaintenanceStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintenanceStepTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema identifier is formatted in, quoted by PgIdentifier at the caller; the table, column, interval, window and moment are all bound parameters.")]
internal static partial class PartitionMaintenance {
  private const string CONVERTED = "converted";

  /// <summary>
  /// What this instance's cycle does: <see langword="null"/> when partitioning is off (or its options are not
  /// registered), when another instance holds the maintainer duty, or when no table is registered.
  /// </summary>
  /// <param name="services">The cycle's services.</param>
  /// <returns>How far ahead to create, and every distinct registered table, or <see langword="null"/>.</returns>
  public static PartitionMaintenancePlan? Plan(IServiceProvider services) {
    if (services.GetService<IOptions<MaintenanceWorkerOptions>>()?.Value.Partitioning is not { Enabled: true } partitioning
        || !_isThisInstancesTurn(services)) {
      return null;
    }
    var tables = services.GetServices<PartitionedTable>().Distinct().ToList();
    return tables.Count == 0 ? null : new PartitionMaintenancePlan(partitioning.CreateAhead, tables);
  }

  // Only the holder of the maintainer duty, where role assignment manages it. Elsewhere every instance may, and
  // wh_partition_maintain's per-table advisory lock lets one through.
  private static bool _isThisInstancesTurn(IServiceProvider services) =>
    services.GetService<DutyHolderWorker>() is not { } holder
    || !holder.Roles.Contains(StartupDuties.MAINTAINER, StringComparer.Ordinal)
    || holder.Holds(StartupDuties.MAINTAINER);

  /// <summary>Converts (once) and maintains each table of <paramref name="plan"/>.</summary>
  /// <param name="connection">An open connection with the right to change the tables.</param>
  /// <param name="quotedSchema">The service's schema, quoted as an identifier.</param>
  /// <param name="plan">The tables and how far ahead to create.</param>
  /// <param name="now">The moment the cycle runs at.</param>
  /// <param name="logger">The logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>What the cycle did.</returns>
  public static async Task<List<PartitionMaintenanceResult>> RunAsync(
      NpgsqlConnection connection, string quotedSchema, PartitionMaintenancePlan plan,
      DateTimeOffset now, ILogger logger, CancellationToken cancellationToken) {
    var results = new List<PartitionMaintenanceResult>();
    foreach (var table in plan.Tables) {
      if (await _convertAsync(connection, quotedSchema, table, now, cancellationToken).ConfigureAwait(false) == CONVERTED) {
        results.Add(new PartitionMaintenanceResult(table.Table, CONVERTED, table.Table));
        LogConverted(logger, table.Table, table.PartitionColumn, table.Interval);
      }
      await _maintainAsync(connection, quotedSchema, table, plan.CreateAhead, now, results, logger, cancellationToken).ConfigureAwait(false);
    }
    return results;
  }

  private static async Task<string> _convertAsync(
      NpgsqlConnection connection, string quotedSchema, PartitionedTable table, DateTimeOffset now, CancellationToken cancellationToken) {
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT {quotedSchema}.wh_partition_convert(@table, @column, @interval, @now)";
    command.Parameters.AddWithValue("table", table.Table);
    command.Parameters.AddWithValue("column", table.PartitionColumn);
    command.Parameters.AddWithValue("interval", table.Interval);
    command.Parameters.AddWithValue("now", now.ToUniversalTime());
    return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }

  private static async Task _maintainAsync(
      NpgsqlConnection connection, string quotedSchema, PartitionedTable table, int createAhead, DateTimeOffset now,
      List<PartitionMaintenanceResult> results, ILogger logger, CancellationToken cancellationToken) {
    await using var command = connection.CreateCommand();
    // The window is read in the same statement, so a fixed window, a stored window and the stored window's default
    // are one expression: COALESCE(fixed, stored days, default days).
    command.CommandText =
      $"SELECT action, partition_name FROM {quotedSchema}.wh_partition_maintain(@table, @interval, @ahead, " +
      $"COALESCE(@keep, (SELECT make_interval(days => s.setting_value::INTEGER) FROM {quotedSchema}.wh_settings s " +
      "WHERE s.setting_key = @key), make_interval(days => @defaultDays)), @now)";
    command.Parameters.AddWithValue("table", table.Table);
    command.Parameters.AddWithValue("interval", table.Interval);
    command.Parameters.AddWithValue("ahead", createAhead);
    command.Parameters.Add(new NpgsqlParameter("keep", NpgsqlDbType.Interval) { Value = table.Retention is { } keep ? keep : (object)DBNull.Value });
    command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)table.RetentionSettingKey ?? DBNull.Value });
    command.Parameters.AddWithValue("defaultDays", table.RetentionDefaultDays);
    command.Parameters.AddWithValue("now", now.ToUniversalTime());
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var result = new PartitionMaintenanceResult(table.Table, reader.GetString(0), reader.GetString(1));
      results.Add(result);
      LogPartition(logger, result.Action, result.Partition, result.Table);
    }
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Converted {Table} to partitions of {Interval} by {Column}; the table is now its oldest partition")]
  private static partial void LogConverted(ILogger logger, string table, string column, TimeSpan interval);

  [LoggerMessage(Level = LogLevel.Information, Message = "Partition maintenance {Action} {Partition} of {Table}")]
  private static partial void LogPartition(ILogger logger, string action, string partition, string table);
}
