// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Table maintenance on the Dapper driver: the rewrite queue and its measurements, the bloat estimate and the approximate
/// measurement, the offline, external and online (pg_squeeze) rewrites, the work-table vacuum, and the queue-index survey,
/// drop and rebuild.
/// </summary>
/// <remarks>
/// <para>
/// This coordinator used to inherit the interface's inert defaults for every member here, so on this driver the
/// maintenance cycle found nothing to rewrite, vacuum or rebuild, whatever the tables held (#2051). Each member now runs
/// the statements the EF Core coordinator runs, from <see cref="TableMaintenanceSql"/>, qualified with this coordinator's
/// schema, under a slot of the coordinator gate when one is configured.
/// </para>
/// <para>
/// The Dapper driver creates its schema on the application connection, so the role that runs this coordinator normally
/// owns the tables. There is no separate schema-initialization connection: a table the role does not own is reported and
/// left alone, exactly as the EF Core coordinator does when no <c>-init</c> connection is configured.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperTableMaintenanceTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorDriverParityTests.cs</tests>
public partial class DapperWorkCoordinator {
  /// <summary>The remedy named when the coordinator's role does not own a table it was asked to maintain.</summary>
  internal const string OWNER_REMEDY = "the Dapper coordinator runs as that role; run it as the role that owns the schema";

  /// <summary>
  /// Decides whether the prerequisites a squeeze needs are met (null) or names the first unmet one. Always
  /// <see cref="PgSqueezePrerequisites.Unmet"/> in production; a test replaces it to reach the call on a server without
  /// logical WAL, which the test image is.
  /// </summary>
  internal Func<PgSqueezePrerequisiteFacts, string, string?> SqueezePrerequisitesUnmet { get; init; } = PgSqueezePrerequisites.Unmet;

  /// <inheritdoc />
  public Task<IReadOnlyList<TableRewriteCandidate>> GetTablesNeedingRewriteAsync(CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) => TableMaintenanceSql.TablesNeedingRewriteAsync(cmd, prefix, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task RequestTableRewriteAsync(string tableName, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    return _withGatedCommandAsync(async (cmd, prefix) => {
      await TableMaintenanceSql.RequestRewriteAsync(cmd, prefix, tableName, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task RequestMeasuredTableRewriteAsync(
      string tableName, BloatMeasurement measurement, double estimatedRatio, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    return _withGatedCommandAsync(async (cmd, prefix) => {
      await TableMaintenanceSql.RequestMeasuredRewriteAsync(cmd, prefix, tableName, measurement, estimatedRatio, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task ClearTableRewriteRequestAsync(string tableName, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    return _withGatedCommandAsync(async (cmd, prefix) => {
      await TableMaintenanceSql.ClearRewriteRequestAsync(cmd, prefix, tableName, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// The estimate is the catalog read the rewrites re-measure with; the approximate measurement calls
  /// <c>wh_measure_table_bloat_approx</c> and raises when pgstattuple is not installed. The exact scan is refused.
  /// </remarks>
  public async Task<double?> MeasureTableBloatAsync(
      string tableName, BloatMeasurementMethod method, CancellationToken cancellationToken = default) {
    TableMaintenanceSql.RequireFrameworkTable(tableName);
    if (method is not (BloatMeasurementMethod.Estimate or BloatMeasurementMethod.Approx)) {
      throw new NotSupportedException(
        $"Bloat is measured by estimate or approx only; {method} (the full pgstattuple scan) is never run by the library.");
    }
    return await _withGatedConnectionAsync(async (connection, prefix) => {
      if (method == BloatMeasurementMethod.Estimate) {
        return await TableMaintenanceSql.MeasureRatioAsync(connection, tableName, _schema, cancellationToken).ConfigureAwait(false);
      }
      await using var cmd = CoordinatorCommandTimeout.Apply(connection.CreateCommand());
      return await TableMaintenanceSql.MeasureApproxAsync(cmd, prefix, tableName, cancellationToken).ConfigureAwait(false);
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>
  /// <c>VACUUM FULL</c> cannot be parameterized and cannot run in a transaction, so the name is checked against the
  /// framework's naming rule and the statement runs on the coordinator's own connection outside any transaction, and only
  /// for a table the coordinator's role owns: Postgres warns and skips a table it does not, so that is reported instead.
  /// </remarks>
  public Task<double?> RewriteTableAsync(string tableName, CancellationToken cancellationToken = default) {
    TableMaintenanceSql.RequireFrameworkTable(tableName);
    return _withGatedConnectionAsync(async (connection, prefix) => {
      var qualified = prefix + tableName;
      if (await _notOwnedAsync(connection, qualified, tableName, cancellationToken).ConfigureAwait(false)) {
        return null;
      }
      await TableMaintenanceSql.VacuumFullAndAnalyzeAsync(connection, qualified, cancellationToken).ConfigureAwait(false);
      return await TableMaintenanceSql.MeasureRatioAsync(connection, tableName, _schema, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// The program is handed this coordinator's connection string as libpq environment variables, password included,
  /// because it owns the table this coordinator maintains. After it reports success the table is analyzed and
  /// re-measured, exactly as after <c>VACUUM FULL</c>.
  /// </remarks>
  public Task<double?> RewriteTableWithAsync(
      string tableName, Func<TableRewriteTarget, CancellationToken, Task<bool>> rewrite, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(rewrite);
    TableMaintenanceSql.RequireFrameworkTable(tableName);
    return _withGatedConnectionAsync(async (connection, prefix) => {
      var qualified = prefix + tableName;
      if (await _notOwnedAsync(connection, qualified, tableName, cancellationToken).ConfigureAwait(false)) {
        return null;
      }
      var target = new TableRewriteTarget(qualified, LibpqEnvironment.FromConnectionString(_connectionString));
      if (!await rewrite(target, cancellationToken).ConfigureAwait(false)) {
        return null;
      }
      await TableMaintenanceSql.AnalyzeAsync(connection, qualified, cancellationToken).ConfigureAwait(false);
      return await TableMaintenanceSql.MeasureRatioAsync(connection, tableName, _schema, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// The prerequisites are read on the coordinator's connection, as its role, and pg_squeeze is given the schema and name
  /// the catalog resolves the table to. An unmet prerequisite is reported and nothing runs.
  /// </remarks>
  public Task<double?> SqueezeTableAsync(string tableName, CancellationToken cancellationToken = default) {
    TableMaintenanceSql.RequireFrameworkTable(tableName);
    return _withGatedConnectionAsync(async (connection, prefix) => {
      var qualified = prefix + tableName;
      if (await TableMaintenanceSql.SqueezeAndAnalyzeAsync(connection, qualified, tableName, SqueezePrerequisitesUnmet, cancellationToken)
            .ConfigureAwait(false) is { } unmet) {
        _reportTableNotRewritten(tableName, unmet);
        return null;
      }
      return await TableMaintenanceSql.MeasureRatioAsync(connection, tableName, _schema, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// <c>VACUUM</c> cannot run in a transaction, so it runs on the coordinator's connection outside any. The table names
  /// are the framework's own constants. A table the coordinator's role does not own, or one that is not there, comes back
  /// with the reason and is not vacuumed.
  /// </remarks>
  public Task<IReadOnlyList<WorkTableVacuum>> VacuumWorkTablesAsync(CancellationToken cancellationToken = default) =>
    _withGatedConnectionAsync<IReadOnlyList<WorkTableVacuum>>(async (connection, prefix) => {
      var outcomes = new List<WorkTableVacuum>(TableMaintenanceSql.VACUUMED_WORK_TABLES.Length);
      foreach (var table in TableMaintenanceSql.VACUUMED_WORK_TABLES) {
        var qualified = prefix + table;
        if (await TableMaintenanceSql.NotOwnedReasonAsync(connection, qualified, table, cancellationToken, OWNER_REMEDY)
              .ConfigureAwait(false) is { } notOwned) {
          outcomes.Add(new WorkTableVacuum(table, false, notOwned));
          continue;
        }
        await TableMaintenanceSql.VacuumAnalyzeAsync(connection, qualified, cancellationToken).ConfigureAwait(false);
        outcomes.Add(new WorkTableVacuum(table, true, null));
      }
      return outcomes;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<QueueIndexBloat>> GetQueueIndexBloatAsync(CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) => TableMaintenanceSql.QueueIndexSurveyAsync(cmd, prefix, cancellationToken), cancellationToken);

  /// <inheritdoc />
  /// <remarks>
  /// <c>DROP INDEX CONCURRENTLY</c> cannot run in a transaction, so it runs on the coordinator's connection outside any,
  /// and only once the catalog has said the name is an invalid index on a queue table the coordinator's role owns.
  /// </remarks>
  public Task<bool> DropInvalidQueueIndexAsync(string indexName, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
    return _onQueueIndexAsync(indexName, wantValid: false, QueueIndexSql.DROPPED, refused: false,
      TableMaintenanceSql.DropIndexConcurrentlyAsync, cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// <c>REINDEX INDEX CONCURRENTLY</c> runs as <see cref="DropInvalidQueueIndexAsync"/> does, only on a valid index on a
  /// queue table the coordinator's role owns.
  /// </remarks>
  public Task<long?> ReindexQueueIndexAsync(string indexName, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
    return _onQueueIndexAsync(indexName, wantValid: true, QueueIndexSql.REBUILT, refused: null,
      TableMaintenanceSql.ReindexConcurrentlyAsync, cancellationToken);
  }

  /// <summary>
  /// Runs <paramref name="work"/> on the index when the catalog says it is an index on a queue table with the validity
  /// <paramref name="wantValid"/> and the coordinator's role owns that table; otherwise logs why not and returns
  /// <paramref name="refused"/>. The work is handed the index's name quoted and schema-qualified.
  /// </summary>
  private Task<T> _onQueueIndexAsync<T>(
      string indexName, bool wantValid, string action, T refused,
      Func<System.Data.Common.DbConnection, string, CancellationToken, Task<T>> work, CancellationToken cancellationToken,
      [System.Runtime.CompilerServices.CallerMemberName] string caller = "") =>
    _withGatedConnectionAsync(async (connection, prefix) => {
      var (table, valid) = await TableMaintenanceSql.QueueIndexLookupAsync(connection, prefix, indexName, cancellationToken).ConfigureAwait(false);
      var refusal = TableMaintenanceSql.QueueIndexRefusal(table, valid, wantValid)
        ?? await TableMaintenanceSql.NotOwnedReasonAsync(connection, prefix + table, table!, cancellationToken, OWNER_REMEDY).ConfigureAwait(false);
      if (refusal is not null) {
        LogQueueIndexNotTouched(_log, indexName, action, refusal);
        return refused;
      }
      return await work(connection, prefix + PgIdentifier.Quote(indexName), cancellationToken).ConfigureAwait(false);
    }, cancellationToken, caller);

  /// <summary>True, having reported why, when the coordinator's role does not own the table.</summary>
  private async Task<bool> _notOwnedAsync(NpgsqlConnection connection, string qualified, string tableName, CancellationToken cancellationToken) {
    if (await TableMaintenanceSql.NotOwnedReasonAsync(connection, qualified, tableName, cancellationToken, OWNER_REMEDY)
          .ConfigureAwait(false) is not { } notOwned) {
      return false;
    }
    _reportTableNotRewritten(tableName, notOwned);
    return true;
  }

  private void _reportTableNotRewritten(string tableName, string reason) => LogTableNotRewritten(_log, tableName, reason);

  /// <summary>The coordinator's logger, or one that discards, so a report never depends on whether one was given.</summary>
  private ILogger _log => (ILogger?)_logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

  /// <summary>
  /// Work on one coordinator connection under a slot of the coordinator gate when one is configured, taken under the
  /// calling member's name, with this coordinator's schema prefix. The connection is disposed when the work completes.
  /// </summary>
  private async Task<T> _withGatedConnectionAsync<T>(
      Func<NpgsqlConnection, string, Task<T>> work, CancellationToken cancellationToken,
      [System.Runtime.CompilerServices.CallerMemberName] string caller = "") {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken, caller).ConfigureAwait(false);
    await using var __scope = await CoordinatorConnectionScope.AcquireAsync(_connectionString, cancellationToken);
    var prefix = await _prefixAsync(__scope.Connection, cancellationToken);
    return await work(__scope.Connection, prefix).ConfigureAwait(false);
  }

  [LoggerMessage(Level = LogLevel.Warning, Message = "Table {Table} was not rewritten: {Reason}")]
  static partial void LogTableNotRewritten(ILogger logger, string table, string reason);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Index {Index} was not {Action}: {Reason}")]
  static partial void LogQueueIndexNotTouched(ILogger logger, string index, string action, string reason);
}
