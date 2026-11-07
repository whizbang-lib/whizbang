// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Drives the one-time fill of the id lanes of stream digests stored before migration 336 (#2106), one bounded batch
/// per call to <c>wh_backfill_stream_digest_id_lanes_batch</c> (migration 337), each in its own transaction.
/// </summary>
/// <remarks>
/// <para>
/// A digest bucket created by the shared fold carries its id lanes; one stored before keeps NULL until this fills it,
/// and a compare that reads it falls back to whole-stream sync. Filling is a read of every stream's events, so it never
/// runs in a migration: the maintenance cycle, which runs only when the service is idle, calls this instead.
/// </para>
/// <para>
/// A batch recomputes its streams from their events. A bucket whose hash lanes or count disagree with its events is
/// DRIFT: an unaccounted write path touched it. The batch corrects it, and this reports every one first, at Warning,
/// naming the bucket with its stored and recomputed values, and counts it on
/// <see cref="StreamIntegrityMetrics.StreamDigestDriftCorrected"/>. Nothing is corrected silently.
/// </para>
/// <para>
/// One instance walks the cursor at a time: a batch that finds it held returns at once and this call stops for the
/// cycle. Once a batch finds nothing left the backfill is recorded as done, and every later call costs one keyed read.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#one-digest-fold</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestIdLaneBackfillSqlTests.cs</tests>
public static partial class StreamDigestIdLaneBackfill {
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
  /// batches have run, reporting every drifted bucket a batch corrected.
  /// </summary>
  /// <param name="connection">An open connection, not in a transaction: each batch commits on its own.</param>
  /// <param name="quotedSchema">The schema the digests live in, quoted as an identifier.</param>
  /// <param name="batchSize">The most streams one batch visits.</param>
  /// <param name="maxRounds">The most batches this call runs.</param>
  /// <param name="logger">Optional logger; drift is logged at Warning.</param>
  /// <param name="metrics">Optional metrics; drift is counted on <see cref="StreamIntegrityMetrics.StreamDigestDriftCorrected"/>.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The streams this call visited.</returns>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "The connection, the schema, the two bounds and the two reporting sinks are one call of the driver; the lifecycle backfill's driver takes the same shape.")]
  public static async Task<long> RunAsync(
      NpgsqlConnection connection, string quotedSchema, int batchSize, int maxRounds, ILogger? logger,
      StreamIntegrityMetrics? metrics, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentException.ThrowIfNullOrWhiteSpace(quotedSchema);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
    var log = logger ?? NullLogger.Instance;
    long visited = 0;
    for (var round = 0; round < maxRounds; round++) {
      var batch = await _batchAsync(connection, quotedSchema, batchSize, log, metrics, cancellationToken).ConfigureAwait(false);
      if (batch == BUSY) {
        LogBusy(log);
        return visited;
      }
      if (batch == 0) {
        if (visited > 0) {
          LogCompleted(log, visited);
        }
        return visited;
      }
      visited += batch;
    }
    return visited;
  }

  private static async Task<int> _batchAsync(
      NpgsqlConnection connection, string quotedSchema, int batchSize, ILogger log, StreamIntegrityMetrics? metrics,
      CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      "SELECT visited, kind, origin_service_id, scope_tenant, event_type, stream_id, stored_lo, stored_hi, stored_count, " +
      $"recomputed_lo, recomputed_hi, recomputed_count FROM {quotedSchema}.wh_backfill_stream_digest_id_lanes_batch($1)",
      connection);
    command.Parameters.AddWithValue(batchSize);
    var visited = 0;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      visited = reader.GetInt32(0);
      var kind = reader.IsDBNull(1) ? null : reader.GetString(1);
      if (kind is null || kind == "filled") {
        continue;
      }
      metrics?.StreamDigestDriftCorrected.Add(1, new KeyValuePair<string, object?>("kind", kind));
      LogDriftCorrected(log, kind, reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetGuid(5),
        _text(reader, 6), _text(reader, 7), _text(reader, 8), _text(reader, 9), _text(reader, 10), _text(reader, 11));
    }
    return visited;
  }

  private static string _text(NpgsqlDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? "none" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!;

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "A generated structured log message: each value is its own searchable property (bucket identity, stored and recomputed lanes and counts), so an operator can find every drifted bucket of one type or origin.")]
  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Stream digest drift corrected ({Kind}): origin {OriginServiceId}, tenant '{TenantScope}', type {EventType}, " +
              "stream {StreamId}. Stored lanes {StoredLo}/{StoredHi} over {StoredCount} event(s); its events fold to " +
              "{RecomputedLo}/{RecomputedHi} over {RecomputedCount}. A write path changed this bucket without the shared fold.")]
  private static partial void LogDriftCorrected(ILogger logger, string kind, Guid originServiceId, string tenantScope,
    string eventType, Guid streamId, string storedLo, string storedHi, string storedCount, string recomputedLo,
    string recomputedHi, string recomputedCount);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "Stream digest id-lane backfill finished: this run visited {Visited} streams, and the store is recorded as done.")]
  private static partial void LogCompleted(ILogger logger, long visited);

  [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
    Message = "Stream digest id-lane backfill skipped this cycle: another instance holds its cursor.")]
  private static partial void LogBusy(ILogger logger);
}
