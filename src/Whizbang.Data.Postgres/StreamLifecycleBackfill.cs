// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Drives the one-time backfill of the stream lifecycle ledger (<c>wh_stream_lifecycle</c>, migration 260, #1732):
/// seeds a row for every stream the event store held before the ledger existed.
/// </summary>
/// <remarks>
/// <para>
/// The migration creates the ledger and every write path keeps it from then on, but the streams already in the store
/// need their rows too, and computing them is a read of every stream. That cannot run inside the migration: it would
/// hold every stream it touched until the migration committed, and nothing would bound it but the size of the store.
/// So the migration only records whether there is anything to do (the <c>wh_settings</c> row
/// <c>stream_lifecycle_backfill_version</c>), and this runs the work from the maintenance cycle, one bounded batch per
/// call to <c>wh_backfill_stream_lifecycle_batch</c>, each in its own transaction.
/// </para>
/// <para>
/// A batch resumes after the cursor the last committed batch left, computes each of its streams' rows whole under the
/// streams' append locks, and may be repeated without changing anything. One instance walks the cursor at a time: a
/// batch that finds it held returns at once, and this call stops for the cycle. Once a batch finds nothing left the
/// marker is set, and every later call costs one keyed read.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/stream-end-of-life#the-one-time-backfill</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleBackfillMaintenanceStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamLifecycleLedgerTests.cs</tests>
public static partial class StreamLifecycleBackfill {
  /// <summary>
  /// The streams one batch visits unless told otherwise. A batch holds one advisory lock per stream until it commits,
  /// so the size is bounded by the lock table, not only by time.
  /// </summary>
  public const int DEFAULT_BATCH_SIZE = 500;

  /// <summary>The batches one maintenance cycle runs unless told otherwise.</summary>
  public const int DEFAULT_MAX_ROUNDS = 20;

  /// <summary>What a batch returns when another instance holds the cursor.</summary>
  private const int BUSY = -1;

  /// <summary>
  /// Runs batches until the backfill is done, another instance holds the cursor, or <paramref name="maxRounds"/>
  /// batches have run.
  /// </summary>
  /// <param name="connection">An open connection, not in a transaction: each batch commits on its own.</param>
  /// <param name="quotedSchema">The schema the ledger lives in, quoted as an identifier.</param>
  /// <param name="batchSize">The most streams one batch visits.</param>
  /// <param name="maxRounds">The most batches this call runs.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The streams this call visited.</returns>
  public static async Task<long> RunAsync(
      NpgsqlConnection connection, string quotedSchema, int batchSize, int maxRounds, ILogger? logger,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentException.ThrowIfNullOrWhiteSpace(quotedSchema);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
    var log = logger ?? NullLogger.Instance;
    long visited = 0;
    for (var round = 0; round < maxRounds; round++) {
      await using var command = new NpgsqlCommand(
        $"SELECT {quotedSchema}.wh_backfill_stream_lifecycle_batch($1)", connection);
      command.Parameters.AddWithValue(batchSize);
      var result = Convert.ToInt32(
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
      if (result == BUSY) {
        LogBusy(log);
        return visited;
      }
      if (result == 0) {
        if (visited > 0) {
          LogCompleted(log, visited);
        }
        return visited;
      }
      visited += result;
    }
    return visited;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Stream lifecycle backfill finished: this run seeded the ledger for {Visited} streams, and the store is recorded as done.")]
  private static partial void LogCompleted(ILogger logger, long visited);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
    Message = "Stream lifecycle backfill skipped this cycle: another instance holds its cursor.")]
  private static partial void LogBusy(ILogger logger);
}
