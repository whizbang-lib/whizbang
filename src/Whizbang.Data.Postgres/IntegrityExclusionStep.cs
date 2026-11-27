// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one exclusion cycle did (#2588).</summary>
/// <param name="Batches">Request batches applied.</param>
/// <param name="Applied">Events marked or unmarked.</param>
/// <param name="Refused">Events refused (own events, events written here, not stored, superseding).</param>
/// <param name="RolledOff">Audit log rows and finished requests removed past the log retention.</param>
/// <docs>resilience/integrity-exclusions#requests-and-the-maintenance-step</docs>
public readonly record struct IntegrityExclusionCycle(int Batches, int Applied, int Refused, int RolledOff);

/// <summary>
/// The maintenance step that applies queued integrity exclusion requests (#2588), as <see cref="ArchiveRestoreRequestStep"/>
/// applies restore requests: each cycle it applies up to <see cref="MAX_BATCHES_PER_CYCLE"/> batches of
/// <see cref="IntegrityExclusionOptions.BatchSize"/> events (<c>wh_integrity_exclusion_apply_batch</c>), oldest request
/// first, logs a Warning per origin and batch and counts the marks, removes audit log rows and finished requests past
/// <see cref="IntegrityExclusionOptions.LogRetention"/>, and publishes the per-origin gauges.
/// </summary>
/// <remarks>
/// It runs in the maintenance cycle, so under the maintainer duty, idle-gated, and bounded by its batch budget; marking
/// is never on a receive path. Pending marks are asked of their origin by <see cref="OriginPositionFillStep"/>.
/// </remarks>
/// <docs>resilience/integrity-exclusions#requests-and-the-maintenance-step</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionsApiTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed partial class IntegrityExclusionStep : IMaintenanceStep {
  /// <summary>The most request batches one cycle applies.</summary>
  public const int MAX_BATCHES_PER_CYCLE = 10;

  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each batch.</param>
  public IntegrityExclusionStep(ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  public static IntegrityExclusionStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new IntegrityExclusionStep(services.GetService<ILogger<IntegrityExclusionStep>>() ?? (ILogger)NullLogger.Instance);
  }

  /// <inheritdoc />
  public string Name => "integrity-exclusions";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    _ = await ApplyAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services: the store through <see cref="PostgresArchiveConnection"/>,
  /// the options, the clock, this service's name and the metrics.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  public async Task<IntegrityExclusionCycle> ApplyAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var options = (services.GetService<IOptions<StreamIntegrityOptions>>()?.Value ?? new StreamIntegrityOptions()).Exclusions;
    var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
    var serviceName = services.GetService<IServiceInstanceProvider>()?.ServiceName;
    var metrics = services.GetService<StreamIntegrityMetrics>();
    var exclusions = new PostgresIntegrityExclusions(database, new StreamIntegrityOptions { Exclusions = options }, clock, serviceName,
      _logger, metrics);
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";

    var batches = 0;
    var applied = 0;
    var refused = 0;
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    while (batches < MAX_BATCHES_PER_CYCLE) {
      var batch = await _applyBatchAsync(connection, prefix, options.BatchSize, serviceName, clock.GetUtcNow(), cancellationToken)
        .ConfigureAwait(false);
      if (batch.Count == 0) {
        break;
      }
      batches++;
      var first = batch[0];
      if (first.Error is not null) {
        LogRequestFailed(_logger, first.RequestId, first.Action, first.Error);
        continue;
      }
      var outcome = first.Action == PostgresIntegrityExclusions.EXCLUDE ? PostgresIntegrityExclusions.MARKED : PostgresIntegrityExclusions.UNMARKED;
      List<IntegrityExclusionOutcome> outcomes = [.. batch.Where(b => b.Applied > 0).Select(b => new IntegrityExclusionOutcome(b.Origin, outcome, b.Applied))];
      exclusions.Report(first.Action, outcomes, first.Reason, first.RequestedBy, first.RequestId);
      applied += batch.Sum(b => b.Applied);
      refused += batch.Sum(b => b.Refused);
      if (batch.Sum(b => b.Refused) > 0) {
        LogBatchRefused(_logger, batch.Sum(b => b.Refused), first.RequestId);
      }
      if (first.Done) {
        LogRequestDone(_logger, first.RequestId, first.Action);
      }
    }

    var rolledOff = await _rollOffAsync(connection, prefix, clock.GetUtcNow() - options.LogRetention, cancellationToken).ConfigureAwait(false);
    var summary = await exclusions.SummarizeAsync(null, cancellationToken).ConfigureAwait(false);
    metrics?.UpdateExclusions([.. summary.Origins.Select(o => (o.OriginServiceId, o.Excluded, o.Pending))]);
    return new IntegrityExclusionCycle(batches, applied, refused, rolledOff);
  }

  private sealed record BatchRow(Guid RequestId, string Action, Guid? Origin, int Applied, int Refused, bool Done, string? Error,
    string Reason, string RequestedBy);

  private static async Task<List<BatchRow>> _applyBatchAsync(NpgsqlConnection connection, string prefix, int batchSize,
      string? serviceName, DateTimeOffset now, CancellationToken cancellationToken) {
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"""
      SELECT b.request_id, b.action, b.origin_service_id, b.applied, b.refused, b.done, b.error, r.reason, r.requested_by
      FROM {prefix}wh_integrity_exclusion_apply_batch($1, $2, $3, $4) b
      LEFT JOIN {prefix}wh_integrity_exclusion_requests r ON r.request_id = b.request_id
      """, connection, transaction);
    command.Parameters.AddWithValue(batchSize);
    command.Parameters.AddWithValue(batchSize * PostgresIntegrityExclusions.SCAN_FACTOR);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)serviceName ?? DBNull.Value });
    command.Parameters.AddWithValue(now);
    var rows = new List<BatchRow>();
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        rows.Add(new BatchRow(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
          reader.GetInt32(3), reader.GetInt32(4), reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetString(6),
          reader.GetString(7), reader.GetString(8)));
      }
    }
    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    return rows;
  }

  private static async Task<int> _rollOffAsync(NpgsqlConnection connection, string prefix, DateTimeOffset before,
      CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand($"SELECT log_rows + requests FROM {prefix}wh_integrity_exclusion_roll_off($1)", connection);
    command.Parameters.AddWithValue(before);
    return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Integrity {Action} request {RequestId} failed and is marked Failed: {Error}; the other requests go on (#2588)")]
  static partial void LogRequestFailed(ILogger logger, Guid requestId, string action, string error);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Integrity {Action} request {RequestId} is done (#2588)")]
  static partial void LogRequestDone(ILogger logger, Guid requestId, string action);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "{Count} event(s) of integrity exclusion request {RequestId} were refused: only received history this service " +
              "stores can be excluded, never its own events or events written here (#2588)")]
  static partial void LogBatchRefused(ILogger logger, int count, Guid requestId);
}
