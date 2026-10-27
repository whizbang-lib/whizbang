// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The Postgres <see cref="IFanoutChildRefiler"/>: reads every stored fan-out child (flag
/// <see cref="EventFlags.NoRebroadcast"/>), names the stream its payload's <c>[StreamId]</c> names through the generated
/// extractor, and hands those filed elsewhere to <c>wh_refile_events</c> (migration 247), then purges the composite
/// streams they leave empty with <c>wh_purge_streams</c> (180), in the same transaction (#1798).
/// </summary>
/// <remarks>
/// The choice of events is made here because SQL cannot read a payload's <c>[StreamId]</c>: the property differs by type
/// and only the generated extractor knows it. A batch holds whole composite streams, so a stream is purged in the
/// transaction that empties it and a crash leaves no emptied stream unpurged.
/// </remarks>
/// <param name="openConnection">Opens a connection to the service's database.</param>
/// <param name="streamIds">Reads a payload's <c>[StreamId]</c>; the generated registry's when null.</param>
/// <param name="jsonOptions">Reads stored payloads; the registry's combined options when null.</param>
/// <param name="schema">The service's schema, or null for the connection's search path.</param>
/// <param name="logger">Records each batch.</param>
/// <docs>operations/infrastructure/purging-streams#refiling-composite-children</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FanoutChildRefileSqlTests.cs</tests>
public sealed partial class PostgresFanoutChildRefiler(
  Func<CancellationToken, ValueTask<NpgsqlConnection>> openConnection,
  IStreamIdExtractor? streamIds,
  JsonSerializerOptions? jsonOptions = null,
  string? schema = null,
  ILogger<PostgresFanoutChildRefiler>? logger = null) : IFanoutChildRefiler {

  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? JsonContextRegistry.CreateCombinedOptions();
  private readonly IStreamIdExtractor _streamIds = streamIds ?? Core.Registry.StreamIdExtractorRegistry.GetComposite();

  /// <inheritdoc />
  public async Task<FanoutChildRefileReport> RefileAsync(FanoutChildRefileRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    request.Validate();
    var prefix = schema is null ? string.Empty : SqlText.Identifier(schema) + ".";
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);

    var (scanned, undecidable, misfiled) = await _findMisfiledAsync(connection, prefix, request.BatchSize, cancellationToken).ConfigureAwait(false);

    var moves = new List<RefiledEvent>();
    var emptied = new List<Guid>();
    var purged = new Dictionary<string, long>(StringComparer.Ordinal);
    var batchIndex = 0;
    foreach (var batch in _batchesOfWholeStreams(misfiled, request.BatchSize)) {
      cancellationToken.ThrowIfCancellationRequested();
      await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
      var batchMoves = await _refileAsync(connection, transaction, prefix, batch, cancellationToken).ConfigureAwait(false);
      var batchEmptied = await _emptiedAsync(connection, transaction, prefix, batchMoves, cancellationToken).ConfigureAwait(false);
      if (batchEmptied.Count > 0) {
        foreach (var (table, rows) in await _purgeAsync(connection, transaction, prefix, request, batchIndex, batchEmptied, cancellationToken).ConfigureAwait(false)) {
          purged[table] = purged.GetValueOrDefault(table) + rows;
        }
      }
      // A dry run does the whole batch and rolls it back: the report is then exactly what the repair would do.
      if (request.DryRun) {
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
      } else {
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      }
      moves.AddRange(batchMoves);
      emptied.AddRange(batchEmptied);
      if (logger is not null) {
        LogBatch(logger, request.RepairId, batchIndex, request.DryRun, batchMoves.Count, batchEmptied.Count, request.RequestedBy, request.Reason);
      }
      batchIndex++;
    }

    var report = new FanoutChildRefileReport(request.RepairId, request.DryRun, scanned, undecidable, moves, emptied, purged);
    if (logger is not null) {
      LogRun(logger, request.RepairId, request.DryRun, scanned, moves.Count, emptied.Count, undecidable);
    }
    return report;
  }

  /// <summary>One stored fan-out child filed under a stream its payload does not name.</summary>
  private readonly record struct Misfiled(Guid EventId, Guid FromStream, Guid ToStream);

  private async Task<(int Scanned, int Undecidable, List<Misfiled> Misfiled)> _findMisfiledAsync(
      NpgsqlConnection connection, string prefix, int pageSize, CancellationToken cancellationToken) {
    var scanned = 0;
    var undecidable = 0;
    var misfiled = new List<Misfiled>();
    var after = Guid.Empty;
    while (true) {
      await using var page = new NpgsqlCommand(
        // A child with no body row reads as JSON null: unreadable, so left in place and counted.
        $"SELECT es.event_id, es.stream_id, es.event_type, COALESCE(b.event_data::text, 'null') "
        + $"FROM {prefix}wh_event_store es LEFT JOIN {prefix}wh_event_body b ON b.event_id = es.event_id "
        + $"WHERE (COALESCE(es.flags, 0) & {(int)EventFlags.NoRebroadcast}) <> 0 AND es.event_id > $1 "
        + "ORDER BY es.event_id LIMIT $2",
        connection);
      page.Parameters.AddWithValue(after);
      page.Parameters.AddWithValue(pageSize);
      var read = 0;
      await using (var reader = await page.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          read++;
          var eventId = reader.GetGuid(0);
          var storedOn = reader.GetGuid(1);
          after = eventId;
          var named = _streamNamedBy(reader.GetString(2), reader.GetString(3));
          if (named is null) {
            undecidable++;
          } else if (named.Value != Guid.Empty && named.Value != storedOn) {
            misfiled.Add(new Misfiled(eventId, storedOn, named.Value));
          }
        }
      }
      scanned += read;
      if (read < pageSize) {
        return (scanned, undecidable, misfiled);
      }
    }
  }

  /// <summary>
  /// The stream a stored payload names: <see cref="Guid.Empty"/> when its type has no <c>[StreamId]</c> (it belongs
  /// nowhere else), null when this service cannot read it.
  /// </summary>
  private Guid? _streamNamedBy(string eventType, string body) {
    if (JsonContextRegistry.GetTypeInfoByName(eventType, _jsonOptions) is not { } typeInfo) {
      return null;
    }
    object? payload;
    try {
      payload = JsonSerializer.Deserialize(body, typeInfo);
    } catch (JsonException) {
      return null;
    }
    return payload is null ? null : _streamIds.ExtractStreamId(payload, payload.GetType()) ?? Guid.Empty;
  }

  /// <summary>Batches of whole composite streams, about <paramref name="batchSize"/> events each.</summary>
  private static IEnumerable<IReadOnlyList<Misfiled>> _batchesOfWholeStreams(List<Misfiled> misfiled, int batchSize) {
    var batch = new List<Misfiled>();
    foreach (var stream in misfiled.GroupBy(m => m.FromStream).OrderBy(g => g.Key)) {
      if (batch.Count > 0 && batch.Count + stream.Count() > batchSize) {
        yield return batch;
        batch = [];
      }
      batch.AddRange(stream);
    }
    if (batch.Count > 0) {
      yield return batch;
    }
  }

  private static async Task<List<RefiledEvent>> _refileAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, string prefix, IReadOnlyList<Misfiled> batch,
      CancellationToken cancellationToken) {
    await using var refile = new NpgsqlCommand(
      $"SELECT event_id, from_stream, to_stream, to_version FROM {prefix}wh_refile_events($1, $2)", connection, transaction);
    refile.Parameters.Add(new NpgsqlParameter { Value = batch.Select(m => m.EventId).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    refile.Parameters.Add(new NpgsqlParameter { Value = batch.Select(m => m.ToStream).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    var moves = new List<RefiledEvent>(batch.Count);
    await using var reader = await refile.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      moves.Add(new RefiledEvent(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt64(3)));
    }
    return moves;
  }

  /// <summary>The streams the moves left with no events: composite streams whose rows are ghosts.</summary>
  private static async Task<List<Guid>> _emptiedAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, string prefix, List<RefiledEvent> moves,
      CancellationToken cancellationToken) {
    await using var empty = new NpgsqlCommand(
      $"SELECT s FROM unnest($1) AS s WHERE NOT EXISTS (SELECT 1 FROM {prefix}wh_event_store es WHERE es.stream_id = s) ORDER BY s",
      connection, transaction);
    var leftStreams = moves.Select(m => m.FromStream).Distinct().ToArray();
    empty.Parameters.Add(new NpgsqlParameter { Value = leftStreams, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    var emptied = new List<Guid>();
    await using var reader = await empty.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      emptied.Add(reader.GetGuid(0));
    }
    return emptied;
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "One batch's purge: the connection and its transaction, where the tables live, the request, which batch and its streams.")]
  private static async Task<Dictionary<string, long>> _purgeAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, string prefix, FanoutChildRefileRequest request, int batchIndex,
      List<Guid> streams, CancellationToken cancellationToken) {
    await using var purge = new NpgsqlCommand(
      $"SELECT purged_table, purged_rows FROM {prefix}wh_purge_streams($1, $2, $3, $4, $5, $6)", connection, transaction);
    purge.Parameters.Add(new NpgsqlParameter { Value = streams.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    // Always the real purge: a dry run rolls the whole batch back, so its report is exactly what the repair would do.
    purge.Parameters.AddWithValue(false);
    purge.Parameters.AddWithValue(request.RepairId);
    purge.Parameters.AddWithValue(batchIndex);
    purge.Parameters.AddWithValue(request.RequestedBy);
    purge.Parameters.AddWithValue(request.Reason);
    var rows = new Dictionary<string, long>(StringComparer.Ordinal);
    await using var reader = await purge.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      rows[reader.GetString(0)] = reader.GetInt64(1);
    }
    return rows;
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Composite child refile {RepairId} batch {BatchIndex} (dry run: {DryRun}): {Moved} event(s) moved, {Emptied} composite stream(s) emptied and purged; requested by {RequestedBy}: {Reason}")]
  static partial void LogBatch(ILogger logger, Guid repairId, int batchIndex, bool dryRun, int moved, int emptied, string requestedBy, string reason);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Composite child refile {RepairId} (dry run: {DryRun}): {Scanned} fan-out child(ren) examined, {Moved} moved to the stream their payload names, {Emptied} composite stream(s) emptied, {Undecidable} unreadable here and left in place. Rebuild every perspective for the streams that received events")]
  static partial void LogRun(ILogger logger, Guid repairId, bool dryRun, int scanned, int moved, int emptied, int undecidable);
}
