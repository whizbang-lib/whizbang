// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one history-conversion cycle did.</summary>
/// <param name="Batches">The batches it ran.</param>
/// <param name="RowsScanned">Event rows read past the cursor.</param>
/// <param name="RowsAffected">Rows rewritten to the slim form plus their route id.</param>
/// <param name="RowsKeptFull">Rows without a route id left full because their type is kept full.</param>
/// <param name="RoutesAdded">Routes seen for the first time.</param>
/// <param name="RoutesUpdated">Routes whose counters grew.</param>
/// <param name="TracesWritten">Traces written for converted events inside the trace window.</param>
/// <param name="Status">The last batch's status: <c>ok</c>, <c>complete</c>, <c>held (...)</c> or <c>skipped (...)</c>.</param>
/// <docs>data/event-store#history-conversion</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Step_ConvertsInBatches_UntilTheTableIsCompleteAsync</tests>
public readonly record struct MetadataHistoryConversionResult(
  int Batches, long RowsScanned, long RowsAffected, long RowsKeptFull, long RoutesAdded, long RoutesUpdated, long TracesWritten, string Status);

/// <summary>
/// The maintenance step that converts event rows written with full metadata before slim stored metadata existed to the
/// slim form plus their route id (#1727, WS-2 S10). Each cycle it calls <c>wh_convert_event_metadata_history</c> in
/// batches of <see cref="BATCH_SIZE"/> rows, each batch its own transaction, until a batch comes back short, a batch
/// does not come back <c>ok</c>, or <see cref="MAX_BATCHES_PER_CYCLE"/> batches have run.
/// </summary>
/// <remarks>
/// <para>
/// It runs in the maintenance cycle, so it is idle-gated (the cycle waits for the service to settle, or for its deferral
/// limit) and, where role assignment manages it, runs on the holder of the maintainer duty only. The SQL keeps a cursor,
/// so the conversion resumes where the last cycle stopped, and reports <c>complete</c> once it has read the whole table.
/// </para>
/// <para>
/// This is the only rewrite of event rows, so it is bounded twice: a transaction holds one batch, and a cycle writes at
/// most <see cref="BATCH_SIZE"/> times <see cref="MAX_BATCHES_PER_CYCLE"/> rows, so the WAL and dead tuples of a large
/// history spread over many idle cycles instead of arriving at once. A batch that leaves a row another transaction
/// holds (<c>held</c>) ends the cycle, since the next batch would read the same rows again.
/// </para>
/// </remarks>
/// <docs>data/event-store#history-conversion</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Step_ConvertsInBatches_UntilTheTableIsCompleteAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Step_StopsAtTheCycleLimit_AndTheNextCycleGoesOnAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Step_WhenSkippedOrComplete_CallsOnce_AndLogsItAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Step_AHeldBatch_EndsTheCycleAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoredMetadataTests.cs:AddWhizbangPostgres_RegistersTheHistoryConversionStep_ThatConvertsTheStoresOwnFormAsync</tests>
public sealed partial class MetadataHistoryConversionStep : IMaintenanceStep {
  /// <summary>Event rows one batch reads: one transaction's worth.</summary>
  public const int BATCH_SIZE = 1000;

  /// <summary>Batches one cycle runs at most, so one cycle rewrites at most 20,000 rows.</summary>
  public const int MAX_BATCHES_PER_CYCLE = 20;

  private const string OK = "ok";

  private readonly ILogger _logger;
  private readonly int _batchSize;
  private readonly int _maxBatches;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each cycle's outcome.</param>
  public MetadataHistoryConversionStep(ILogger<MetadataHistoryConversionStep> logger) : this(logger, BATCH_SIZE, MAX_BATCHES_PER_CYCLE) { }

  /// <summary>Creates the step with its own limits, for tests that need a small batch.</summary>
  internal MetadataHistoryConversionStep(ILogger logger, int batchSize, int maxBatches) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
    _batchSize = batchSize;
    _maxBatches = maxBatches;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:FromServices_WithOrWithoutLogging_BuildsTheStepAsync</tests>
  public static MetadataHistoryConversionStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new MetadataHistoryConversionStep(
      services.GetService<ILogger<MetadataHistoryConversionStep>>() ?? NullLogger<MetadataHistoryConversionStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "metadata-history-conversion";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    await ConvertAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services; the database is reached through
  /// <see cref="PostgresArchiveConnection"/>.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  public async Task<MetadataHistoryConversionResult> ConvertAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    var total = new MetadataHistoryConversionResult(0, 0, 0, 0, 0, 0, 0, OK);
    while (total.Batches < _maxBatches) {
      var batch = await _convertOnceAsync(database, prefix, cancellationToken).ConfigureAwait(false);
      total = new MetadataHistoryConversionResult(total.Batches + 1, total.RowsScanned + batch.RowsScanned,
        total.RowsAffected + batch.RowsAffected, total.RowsKeptFull + batch.RowsKeptFull, total.RoutesAdded + batch.RoutesAdded,
        total.RoutesUpdated + batch.RoutesUpdated, total.TracesWritten + batch.TracesWritten, batch.Status);
      if (batch.RowsScanned < _batchSize || batch.Status != OK) {
        break;
      }
    }
    if (total.RowsAffected > 0) {
      LogConverted(_logger, total.RowsAffected, total.RowsScanned, total.RowsKeptFull, total.RoutesAdded, total.RoutesUpdated,
        total.TracesWritten, total.Batches, total.Status);
    } else {
      LogNothingConverted(_logger, total.RowsScanned, total.Status);
    }
    return total;
  }

  private async Task<MetadataHistoryConversionResult> _convertOnceAsync(PostgresArchiveConnection database, string prefix, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      "SELECT rows_scanned, rows_affected, rows_kept_full, routes_added, routes_updated, traces_written, status " +
      $"FROM {prefix}wh_convert_event_metadata_history($1)", connection);
    command.Parameters.AddWithValue(_batchSize);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new MetadataHistoryConversionResult(1, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
      reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6));
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Converted {Converted} historical event metadata row(s) to the slim form ({Scanned} read, {KeptFull} kept full) with {RoutesAdded} new and {RoutesUpdated} counted route(s) and {Traces} trace(s) in {Batches} batch(es); {Status}")]
  static partial void LogConverted(ILogger logger, long converted, long scanned, long keptFull, long routesAdded, long routesUpdated, long traces, int batches, string status);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Historical metadata conversion converted nothing ({Scanned} row(s) read; {Status})")]
  static partial void LogNothingConverted(ILogger logger, long scanned, string status);
}
