// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one distillation cycle did.</summary>
/// <param name="Batches">The batches it ran.</param>
/// <param name="StagedConsumed">Staged rows distilled and removed.</param>
/// <param name="RoutesAdded">Routes seen for the first time.</param>
/// <param name="RoutesUpdated">Routes whose counters grew.</param>
/// <param name="TracesWritten">Traces written.</param>
/// <param name="Status">The last batch's status: <c>ok</c>, or <c>skipped (metadata_write_form=...)</c>.</param>
/// <docs>data/event-store#distillation</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Step_DistillsInBatches_UntilTheStagingIsEmptyAsync</tests>
public readonly record struct MetadataDistillationResult(
  int Batches, long StagedConsumed, long RoutesAdded, long RoutesUpdated, long TracesWritten, string Status);

/// <summary>
/// The maintenance step that distills staged event metadata into routes and traces (#1726, WS-2 S9b). Each cycle it
/// calls <c>wh_distill_event_metadata</c> in batches of <see cref="BATCH_SIZE"/>, each batch its own transaction, until a
/// batch comes back short or <see cref="MAX_BATCHES_PER_CYCLE"/> batches have run.
/// </summary>
/// <remarks>
/// <para>
/// It runs in the maintenance cycle, so it is idle-gated (the cycle waits for the service to settle, or for its deferral
/// limit) and, where role assignment manages it, runs on the holder of the maintainer duty only. Under the Full and Slim
/// write forms the SQL distills nothing and the step stops after one call.
/// </para>
/// <para>
/// The batch is bounded so a transaction never holds more than a batch of staged rows, and the cycle is bounded so a
/// large backlog drains over several cycles instead of holding one; whatever is still staged past
/// <c>metadata_staging_days</c> is removed by retention.
/// </para>
/// </remarks>
/// <docs>data/event-store#distillation</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Step_DistillsInBatches_UntilTheStagingIsEmptyAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Step_StopsAtTheCycleLimit_AndTheNextCycleGoesOnAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Step_UnderAnotherForm_CallsOnce_AndLogsTheSkipAsync</tests>
public sealed partial class MetadataDistillationStep : IMaintenanceStep {
  /// <summary>Staged rows one batch distills: one transaction's worth.</summary>
  public const int BATCH_SIZE = 2000;

  /// <summary>Batches one cycle runs at most, so one cycle distills at most 100,000 events.</summary>
  public const int MAX_BATCHES_PER_CYCLE = 50;

  private readonly ILogger _logger;
  private readonly int _batchSize;
  private readonly int _maxBatches;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each cycle's outcome.</param>
  public MetadataDistillationStep(ILogger<MetadataDistillationStep> logger) : this(logger, BATCH_SIZE, MAX_BATCHES_PER_CYCLE) { }

  /// <summary>Creates the step with its own limits, for tests that need a small batch.</summary>
  internal MetadataDistillationStep(ILogger logger, int batchSize, int maxBatches) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
    _batchSize = batchSize;
    _maxBatches = maxBatches;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:FromServices_WithOrWithoutLogging_BuildsTheStepAsync</tests>
  public static MetadataDistillationStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new MetadataDistillationStep(
      services.GetService<ILogger<MetadataDistillationStep>>() ?? NullLogger<MetadataDistillationStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "metadata-distillation";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    await DistillAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services; the database is reached through
  /// <see cref="PostgresArchiveConnection"/>.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  public async Task<MetadataDistillationResult> DistillAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    var total = new MetadataDistillationResult(0, 0, 0, 0, 0, "ok");
    while (total.Batches < _maxBatches) {
      var batch = await _distillOnceAsync(database, prefix, cancellationToken).ConfigureAwait(false);
      total = new MetadataDistillationResult(total.Batches + 1, total.StagedConsumed + batch.StagedConsumed,
        total.RoutesAdded + batch.RoutesAdded, total.RoutesUpdated + batch.RoutesUpdated,
        total.TracesWritten + batch.TracesWritten, batch.Status);
      if (batch.StagedConsumed < _batchSize || batch.Status != "ok") {
        break;
      }
    }
    if (total.StagedConsumed > 0) {
      LogDistilled(_logger, total.StagedConsumed, total.RoutesAdded, total.RoutesUpdated, total.TracesWritten, total.Batches);
    } else {
      LogNothingDistilled(_logger, total.Status);
    }
    return total;
  }

  private async Task<MetadataDistillationResult> _distillOnceAsync(PostgresArchiveConnection database, string prefix, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT staged_consumed, routes_added, routes_updated, traces_written, status FROM {prefix}wh_distill_event_metadata($1)", connection);
    command.Parameters.AddWithValue(_batchSize);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new MetadataDistillationResult(1, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Distilled {Staged} staged event metadata row(s) into {RoutesAdded} new and {RoutesUpdated} counted route(s) and {Traces} trace(s) in {Batches} batch(es)")]
  static partial void LogDistilled(ILogger logger, long staged, long routesAdded, long routesUpdated, long traces, int batches);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Metadata distillation found nothing to distill ({Status})")]
  static partial void LogNothingDistilled(ILogger logger, string status);
}
