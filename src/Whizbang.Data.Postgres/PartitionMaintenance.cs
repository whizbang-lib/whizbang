// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>One thing a partition maintenance cycle did to one table.</summary>
/// <param name="Table">The table.</param>
/// <param name="Action">
/// What happened: <c>converted</c>; or, per partition, <c>created</c>, <c>tuned</c> (given the storage settings its
/// table declares), <c>carried</c> (expired, and the rows its table still needs, such as the signals a live instance
/// has not consumed, were moved to the current partition first), <c>dropped</c>, <c>emptied</c> (expired but the
/// table was in use, so only truncated),
/// <c>deferred</c> (a lock was not free), <c>kept</c> (debug mode), or <c>skipped</c> (the table is not partitioned,
/// or another instance is maintaining it). A pg_partman pass (#1752) adds, per table, <c>rebounding</c> and
/// <c>rebounded</c> (its oldest partition re-bounded from MINVALUE), <c>registered</c> (<c>create_parent</c>),
/// <c>configured</c> (its pg_partman settings put back) and <c>maintained</c> (<c>run_maintenance</c>).
/// </param>
/// <param name="Partition">The partition, or the table itself for <c>converted</c> and <c>skipped</c>.</param>
/// <docs>operations/infrastructure/partitioned-ledgers#what-a-cycle-does</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionMaintenanceStepTests.cs</tests>
internal readonly record struct PartitionMaintenanceResult(string Table, string Action, string Partition);

/// <summary>What one instance's partition cycle covers.</summary>
/// <param name="CreateAhead">How many intervals past the current one to create.</param>
/// <param name="Tables">Every distinct registered table, in registration order.</param>
/// <param name="Manager">Who creates partitions this cycle (#1752): pg_partman, or the built-in manager.</param>
/// <param name="Announce">Whether the cycle reports <paramref name="Manager"/>'s reason: the first time, and whenever it changes.</param>
/// <docs>operations/infrastructure/partitioned-ledgers#what-a-cycle-does</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionMaintenanceStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PgPartmanPartitionMaintenanceTests.cs</tests>
internal sealed record PartitionMaintenancePlan(
  int CreateAhead, IReadOnlyList<PartitionedTable> Tables, PartitionManagementPlan Manager, bool Announce);

/// <summary>
/// The partition manager's cycle, shared by both drivers' maintenance steps: whether it runs, which tables, and the
/// SQL calls per table (<c>wh_partition_convert</c>; where the strategy resolves to pg_partman,
/// <c>wh_partition_partman</c>; then <c>wh_partition_maintain</c>, which drops expired partitions whoever created them).
/// </summary>
/// <docs>operations/infrastructure/partitioned-ledgers</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionMaintenanceStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PgPartmanPartitionMaintenanceTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/PartitionMaintenanceStepTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema identifier is formatted in, quoted by PgIdentifier at the caller; the table, column, interval, window and moment are all bound parameters.")]
internal static partial class PartitionMaintenance {
  private const string CONVERTED = "converted";
  private const string CREATED = "created";
  private const string REBOUNDING = "rebounding";
  private const string MAINTAINED = "maintained";
  private const string DEFAULT_SUFFIX = "_default";

  // Re-bounding a converted table's oldest partition takes three steps, each committed on its own (#1752).
  private const int REBOUND_STEPS = 3;

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
    if (tables.Count == 0) {
      return null;
    }
    var manager = PartitionManagementStrategyResolver.Resolve(
      partitioning.Strategy, PartitionManagementStrategyResolver.PgPartmanState(services.GetService<ICapabilityRegistry>()));
    return new PartitionMaintenancePlan(partitioning.CreateAhead, tables, manager,
      Announce: services.GetService<PartitionStrategyAnnouncer>()?.IsNew(manager.Reason) ?? true);
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
    if (plan.Announce) {
      LogStrategy(logger, plan.Manager.Reason);
    }
    var results = new List<PartitionMaintenanceResult>();
    foreach (var table in plan.Tables) {
      if (await _convertAsync(connection, quotedSchema, table, now, cancellationToken).ConfigureAwait(false) == CONVERTED) {
        results.Add(new PartitionMaintenanceResult(table.Table, CONVERTED, table.Table));
        LogConverted(logger, table.Table, table.PartitionColumn, table.Interval);
      }
      var partmanMaintained = false;
      if (plan.Manager.UsePgPartman) {
        (partmanMaintained, var partmanResults) =
          await _partmanAsync(connection, quotedSchema, table, plan, now, logger, cancellationToken).ConfigureAwait(false);
        results.AddRange(partmanResults);
      }
      var maintained = await _maintainAsync(connection, quotedSchema, table, plan.CreateAhead, now, logger, cancellationToken).ConfigureAwait(false);
      if (partmanMaintained) {
        // pg_partman made what it could; a partition the built-in pass still had to create is one it missed (it
        // reports a set it skips only as a WARNING). The default partition is always the built-in manager's.
        foreach (var missed in maintained.Where(r => r.Action == CREATED && r.Partition != table.Table + DEFAULT_SUFFIX)) {
          LogNotCreatedByPgPartman(logger, missed.Partition, table.Table);
        }
      }
      results.AddRange(maintained);
    }
    return results;
  }

  // One table's pg_partman pass, repeated while the oldest partition is being re-bounded. Returns whether pg_partman
  // maintained the table, and what every pass did. Under Auto a pass that fails is reported and the built-in manager
  // keeps the table this cycle.
  private static async Task<(bool Maintained, List<PartitionMaintenanceResult> Results)> _partmanAsync(
      NpgsqlConnection connection, string quotedSchema, PartitionedTable table, PartitionMaintenancePlan plan,
      DateTimeOffset now, ILogger logger, CancellationToken cancellationToken) {
    var results = new List<PartitionMaintenanceResult>();
    try {
      List<PartitionMaintenanceResult> pass;
      var steps = 0;
      do {
        pass = await _partmanPassAsync(connection, quotedSchema, table, plan.CreateAhead, now, logger, cancellationToken)
          .ConfigureAwait(false);
        results.AddRange(pass);
        steps++;
      } while (steps < REBOUND_STEPS && pass.Exists(r => r.Action == REBOUNDING));
      return (pass.Exists(r => r.Action == MAINTAINED), results);
    } catch (PostgresException ex) when (plan.Manager.BuiltInOnFailure) {
      LogPgPartmanFailed(logger, ex, table.Table);
      return (false, results);
    }
  }

  private static async Task<List<PartitionMaintenanceResult>> _partmanPassAsync(
      NpgsqlConnection connection, string quotedSchema, PartitionedTable table, int createAhead, DateTimeOffset now,
      ILogger logger, CancellationToken cancellationToken) {
    var results = new List<PartitionMaintenanceResult>();
    // pg_partman reports a partition set it skipped only as a WARNING, which reaches here as a notice.
    void relay(object sender, NpgsqlNoticeEventArgs e) => LogPgPartmanNotice(logger, table.Table, e.Notice.MessageText);
    connection.Notice += relay;
    try {
      await using var command = connection.CreateCommand();
      command.CommandText = $"SELECT action, partition_name FROM {quotedSchema}.wh_partition_partman(@table, @interval, @ahead, NULL, @now)";
      command.Parameters.AddWithValue(nameof(table), table.Table);
      command.Parameters.AddWithValue("interval", table.Interval);
      command.Parameters.AddWithValue("ahead", createAhead);
      command.Parameters.AddWithValue(nameof(now), now.ToUniversalTime());
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var result = new PartitionMaintenanceResult(table.Table, reader.GetString(0), reader.GetString(1));
        results.Add(result);
        // Maintained is every cycle's outcome; the rest are changes worth saying.
        if (result.Action == MAINTAINED) {
          LogPgPartmanMaintained(logger, table.Table);
        } else {
          LogPgPartmanPass(logger, result.Action, table.Table);
        }
      }
    } finally {
      connection.Notice -= relay;
    }
    return results;
  }

  private static async Task<string> _convertAsync(
      NpgsqlConnection connection, string quotedSchema, PartitionedTable table, DateTimeOffset now, CancellationToken cancellationToken) {
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT {quotedSchema}.wh_partition_convert(@table, @column, @interval, @now)";
    command.Parameters.AddWithValue(nameof(table), table.Table);
    command.Parameters.AddWithValue("column", table.PartitionColumn);
    command.Parameters.AddWithValue("interval", table.Interval);
    command.Parameters.AddWithValue(nameof(now), now.ToUniversalTime());
    return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }

  private static async Task<List<PartitionMaintenanceResult>> _maintainAsync(
      NpgsqlConnection connection, string quotedSchema, PartitionedTable table, int createAhead, DateTimeOffset now,
      ILogger logger, CancellationToken cancellationToken) {
    var results = new List<PartitionMaintenanceResult>();
    await using var command = connection.CreateCommand();
    // The window is read in the same statement, so a fixed window, a stored window and the stored window's default
    // are one expression: COALESCE(fixed, stored days, default days). All three null means no window.
    command.CommandText =
      $"SELECT action, partition_name FROM {quotedSchema}.wh_partition_maintain(@table, @interval, @ahead, " +
      $"COALESCE(@keep, (SELECT make_interval(days => s.setting_value::INTEGER) FROM {quotedSchema}.wh_settings s " +
      "WHERE s.setting_key = @key), make_interval(days => @defaultDays)), @now)";
    command.Parameters.AddWithValue(nameof(table), table.Table);
    command.Parameters.AddWithValue("interval", table.Interval);
    command.Parameters.AddWithValue("ahead", createAhead);
    command.Parameters.Add(new NpgsqlParameter("keep", NpgsqlDbType.Interval) { Value = table.Retention is { } keep ? keep : (object)DBNull.Value });
    command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)table.RetentionSettingKey ?? DBNull.Value });
    // With neither a fixed window nor a key, the default is null too, so the window is null and nothing is dropped.
    command.Parameters.Add(new NpgsqlParameter("defaultDays", NpgsqlDbType.Integer) {
      Value = table.RetentionSettingKey is null ? DBNull.Value : table.RetentionDefaultDays,
    });
    command.Parameters.AddWithValue(nameof(now), now.ToUniversalTime());
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var result = new PartitionMaintenanceResult(table.Table, reader.GetString(0), reader.GetString(1));
      results.Add(result);
      LogPartition(logger, result.Action, result.Partition, result.Table);
    }
    return results;
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Converted {Table} to partitions of {Interval} by {Column}; the table is now its oldest partition")]
  private static partial void LogConverted(ILogger logger, string table, string column, TimeSpan interval);

  [LoggerMessage(Level = LogLevel.Information, Message = "Partition maintenance {Action} {Partition} of {Table}")]
  private static partial void LogPartition(ILogger logger, string action, string partition, string table);

  [LoggerMessage(Level = LogLevel.Information, Message = "Partition management: {Reason}")]
  private static partial void LogStrategy(ILogger logger, string reason);

  [LoggerMessage(Level = LogLevel.Information, Message = "pg_partman pass {Action} {Table}")]
  private static partial void LogPgPartmanPass(ILogger logger, string action, string table);

  [LoggerMessage(Level = LogLevel.Debug, Message = "pg_partman pass maintained {Table}")]
  private static partial void LogPgPartmanMaintained(ILogger logger, string table);

  [LoggerMessage(Level = LogLevel.Warning, Message = "pg_partman reported for {Table}: {Message}")]
  private static partial void LogPgPartmanNotice(ILogger logger, string table, string message);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "pg_partman pass failed for {Table}; the built-in manager keeps it this cycle (Whizbang:Workers:Maintenance:Partitioning:Strategy = Auto)")]
  private static partial void LogPgPartmanFailed(ILogger logger, Exception exception, string table);

  [LoggerMessage(Level = LogLevel.Warning, Message = "pg_partman did not create {Partition} of {Table}; the built-in manager created it")]
  private static partial void LogNotCreatedByPgPartman(ILogger logger, string partition, string table);
}
