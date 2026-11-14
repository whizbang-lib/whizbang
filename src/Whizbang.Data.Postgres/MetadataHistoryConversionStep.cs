// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Configuration;
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
  int Batches, long RowsScanned, long RowsAffected, long RowsKeptFull, long RoutesAdded, long RoutesUpdated, long TracesWritten, string Status) {
  /// <summary>
  /// The event rows the conversion has yet to read once the cycle ended (#2322), at most
  /// <see cref="MetadataHistoryConversionStep.REMAINING_CAP"/>; 0 once complete or while it is skipped.
  /// </summary>
  /// <docs>data/event-store#history-conversion</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Step_LogsWhatIsLeft_AndHowManyCyclesItWillTakeAsync</tests>
  public long RowsRemaining { get; init; }
}

/// <summary>
/// The maintenance step that converts event rows written with full metadata before slim stored metadata existed to the
/// slim form plus their route id (#1727, WS-2 S10). Each cycle it calls <c>wh_convert_event_metadata_history</c> in
/// batches of <see cref="MetadataHistoryConversionOptions.BatchSize"/> rows, each batch its own transaction, until a batch
/// comes back short, a batch does not come back <c>ok</c>, <see cref="MetadataHistoryConversionOptions.MaxBatchesPerCycle"/>
/// batches have run, or <see cref="MetadataHistoryConversionOptions.TimeBudget"/> has passed (#2322), and then logs how
/// many rows it has left to read and about how many cycles that is.
/// </summary>
/// <remarks>
/// <para>
/// It runs in the maintenance cycle, so it is idle-gated (the cycle waits for the service to settle, or for its deferral
/// limit) and, where role assignment manages it, runs on the holder of the maintainer duty only. The SQL keeps a cursor,
/// so the conversion resumes where the last cycle stopped, and reports <c>complete</c> once it has read the whole table.
/// </para>
/// <para>
/// This is the only rewrite of event rows, so it is bounded twice: a transaction holds one batch, and a cycle writes at
/// most the batch size times the batch limit rows, and starts no batch once the time budget has passed, so the WAL and
/// dead tuples of a large history spread over many idle cycles instead of arriving at once. A batch that leaves a row
/// another transaction holds (<c>held</c>) ends the cycle, since the next batch would read the same rows again. A cycle
/// that converted nothing and ended on a status other than <c>ok</c> while rows remain logs a warning, since the
/// conversion is then not moving.
/// </para>
/// </remarks>
/// <docs>data/event-store#history-conversion</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Step_ConvertsInBatches_UntilTheTableIsCompleteAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Step_StopsAtTheCycleLimit_AndTheNextCycleGoesOnAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Step_WhenSkippedOrComplete_CallsOnce_AndLogsItAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Step_AHeldBatch_EndsTheCycleAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoredMetadataTests.cs:AddWhizbangPostgres_RegistersTheHistoryConversionStep_ThatConvertsTheStoresOwnFormAsync</tests>
public sealed partial class MetadataHistoryConversionStep : IMaintenanceStep {
  /// <summary>Event rows one batch reads by default: one transaction's worth.</summary>
  public const int BATCH_SIZE = 1000;

  /// <summary>Batches one cycle runs at most by default, so one cycle rewrites at most 200,000 rows.</summary>
  public const int MAX_BATCHES_PER_CYCLE = 200;

  /// <summary>The most remaining rows one cycle counts for its log line: a count through the table's key, bounded.</summary>
  public const int REMAINING_CAP = 50_000_000;

  private const string OK = "ok";

  private readonly ILogger _logger;
  private readonly int _batchSize;
  private readonly int _maxBatches;
  private readonly TimeSpan _timeBudget;
  private readonly TimeProvider _time;

  /// <summary>Creates the step with the default budget.</summary>
  /// <param name="logger">Logs each cycle's outcome.</param>
  public MetadataHistoryConversionStep(ILogger<MetadataHistoryConversionStep> logger)
    : this(logger, new MetadataHistoryConversionOptions(), TimeProvider.System) { }

  /// <summary>Creates the step with its own limits, for tests that need a small batch.</summary>
  internal MetadataHistoryConversionStep(ILogger logger, int batchSize, int maxBatches)
    : this(logger, new MetadataHistoryConversionOptions { BatchSize = batchSize, MaxBatchesPerCycle = maxBatches }, TimeProvider.System) { }

  /// <summary>Creates the step with a configured budget and the clock it measures the time budget on.</summary>
  /// <param name="logger">Logs each cycle's outcome.</param>
  /// <param name="options">The batch size, the batch limit and the time budget.</param>
  /// <param name="time">The clock the time budget is measured on.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Step_StopsStartingBatches_OnceTheTimeBudgetHasPassedAsync</tests>
  internal MetadataHistoryConversionStep(ILogger logger, MetadataHistoryConversionOptions options, TimeProvider time) {
    ArgumentNullException.ThrowIfNull(logger);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(time);
    _logger = logger;
    _batchSize = options.BatchSize;
    _maxBatches = options.MaxBatchesPerCycle;
    _timeBudget = options.TimeBudget;
    _time = time;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:FromServices_WithOrWithoutLogging_BuildsTheStepAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:FromServices_TakesTheConfiguredBudget_AndTheHostsClockAsync</tests>
  public static MetadataHistoryConversionStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new MetadataHistoryConversionStep(
      services.GetService<ILogger<MetadataHistoryConversionStep>>() ?? NullLogger<MetadataHistoryConversionStep>.Instance,
      new MetadataHistoryConversionOptions(),
      services.GetService<TimeProvider>() ?? TimeProvider.System);
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
    _ = (_timeBudget, _time);
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
    var remaining = await _remainingAsync(database, prefix, cancellationToken).ConfigureAwait(false);
    total = total with { RowsRemaining = remaining };
    _log(total);
    return total;
  }

  // One line per cycle: what it converted and what is left, and a warning when it converted nothing and could not move on
  // (a held or skipped batch) while rows remain. Converting nothing with status ok is reading rows that are already slim.
  private void _log(MetadataHistoryConversionResult total) {
    var cycles = total.RowsRemaining == 0 ? 0 : (total.RowsRemaining + Math.Max(total.RowsScanned, 1) - 1) / Math.Max(total.RowsScanned, 1);
    if (total.RowsAffected > 0) {
      LogConverted(_logger, total.RowsAffected, total.RowsScanned, total.RowsKeptFull, total.RoutesAdded, total.RoutesUpdated,
        total.TracesWritten, total.Batches, total.Status, total.RowsRemaining, cycles);
    } else if (total.RowsRemaining > 0 && total.Status != OK) {
      LogNotMoving(_logger, total.RowsRemaining, total.Status);
    } else {
      LogNothingConverted(_logger, total.RowsScanned, total.Status, total.RowsRemaining);
    }
  }

  private static Task<long> _remainingAsync(PostgresArchiveConnection database, string prefix, CancellationToken cancellationToken) {
    _ = (database, prefix, cancellationToken);
    return Task.FromResult(0L);
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

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "A LoggerMessage's parameters are its structured log fields, one per counter the conversion reports; a parameter object would log as one opaque value and lose the fields operators filter on.")]
  [LoggerMessage(Level = LogLevel.Information,
    Message = "Converted {Converted} historical event metadata row(s) to the slim form ({Scanned} read, {KeptFull} kept full) with {RoutesAdded} new and {RoutesUpdated} counted route(s) and {Traces} trace(s) in {Batches} batch(es); {Status}; {Remaining} row(s) left to read, about {Cycles} more cycle(s)")]
  static partial void LogConverted(ILogger logger, long converted, long scanned, long keptFull, long routesAdded, long routesUpdated, long traces, int batches, string status, long remaining, long cycles);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Historical metadata conversion converted nothing ({Scanned} row(s) read; {Status}); {Remaining} row(s) left to read")]
  static partial void LogNothingConverted(ILogger logger, long scanned, string status, long remaining);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Historical metadata conversion converted nothing this cycle with {Remaining} row(s) left to read ({Status}); it is not moving")]
  static partial void LogNotMoving(ILogger logger, long remaining, string status);
}
