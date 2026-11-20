// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The Postgres <see cref="IPerspectivePurgeMarkerStore"/>, shared by both drivers: rows in
/// <c>wh_stream_purge_markers</c> (migration 180). Each call is one statement on its own connection, so a
/// marker commits before the runner removes the row.
/// </summary>
/// <param name="openConnection">Opens a connection to the service's database.</param>
/// <param name="schema">The schema, unquoted; null resolves the table through the connection's search path. A
/// service whose tables live outside <c>public</c> must pass it: the connection comes from a shared data source.</param>
/// <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectivePurgeMarkerStoreTests.cs</tests>
public sealed class PostgresPerspectivePurgeMarkerStore(
  Func<CancellationToken, ValueTask<NpgsqlConnection>> openConnection,
  string? schema = null) : IPerspectivePurgeMarkerStore {
  private readonly string _prefix = schema is null ? string.Empty : SqlText.Identifier(schema) + ".";
  private string _table => _prefix + "wh_stream_purge_markers";

  /// <inheritdoc />
  public async Task<bool> IsPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT EXISTS (SELECT 1 FROM {_table} WHERE stream_id = $1 AND perspective_name IN ($2, $3))",
      connection);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(perspectiveName);
    command.Parameters.AddWithValue(PerspectivePurgeMarkers.ALL_PERSPECTIVES);
    return BooleanScalar.Read(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), "purge marker");
  }

  /// <inheritdoc />
  /// <remarks>One statement for every stream (<c>stream_id = ANY</c>).</remarks>
  public async Task<IReadOnlySet<Guid>> PurgedAmongAsync(
      IReadOnlyCollection<Guid> streamIds, string perspectiveName, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    var purged = new HashSet<Guid>();
    if (streamIds.Count == 0) {
      return purged;
    }
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT DISTINCT stream_id FROM {_table} WHERE stream_id = ANY($1) AND perspective_name IN ($2, $3)",
      connection);
    command.Parameters.Add(new NpgsqlParameter { Value = streamIds.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid });
    command.Parameters.AddWithValue(perspectiveName);
    command.Parameters.AddWithValue(PerspectivePurgeMarkers.ALL_PERSPECTIVES);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      purged.Add(reader.GetGuid(0));
    }
    return purged;
  }

  /// <inheritdoc />
  public async Task MarkPurgedAsync(Guid streamId, string perspectiveName, Guid? purgeEventId, CancellationToken cancellationToken = default) {
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"""
      INSERT INTO {_table} (stream_id, perspective_name, purged_at, purge_event_id)
      VALUES ($1, $2, NOW(), $3)
      ON CONFLICT (stream_id, perspective_name) DO UPDATE
        SET purged_at = EXCLUDED.purged_at, purge_event_id = EXCLUDED.purge_event_id
      """,
      connection);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(perspectiveName);
    command.Parameters.Add(new NpgsqlParameter { Value = (object?)purgeEventId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>One call of <c>wh_backfill_purge_markers</c> (migration 533).</remarks>
  public async Task<int> BackfillMarkersAsync(
      IReadOnlyList<(string PerspectiveName, string EventType)> purgingTypes, int batchSize,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(purgingTypes);
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = _purgingTypesCommand($"SELECT {_prefix}wh_backfill_purge_markers($1, $2, $3)", connection, purgingTypes, batchSize);
    return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <inheritdoc />
  /// <remarks>One call of <c>wh_count_unmarked_rowless_pairs</c> (migration 533).</remarks>
  public async Task<long> CountUnmarkedRowlessPairsAsync(
      IReadOnlyList<(string PerspectiveName, string EventType)> purgingTypes, int cap,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(purgingTypes);
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = _purgingTypesCommand($"SELECT {_prefix}wh_count_unmarked_rowless_pairs($1, $2, $3)", connection, purgingTypes, cap);
    return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
  }

  // The (perspective, event type) pairs as two parallel arrays, and the limit.
  private static NpgsqlCommand _purgingTypesCommand(
      string sql, NpgsqlConnection connection, IReadOnlyList<(string PerspectiveName, string EventType)> purgingTypes, int limit) {
    var command = new NpgsqlCommand(sql, connection);
    command.Parameters.Add(new NpgsqlParameter {
      Value = purgingTypes.Select(p => p.PerspectiveName).ToArray(),
      NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text
    });
    command.Parameters.Add(new NpgsqlParameter {
      Value = purgingTypes.Select(p => p.EventType).ToArray(),
      NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text
    });
    command.Parameters.AddWithValue(limit);
    return command;
  }

  /// <inheritdoc />
  public async Task ClearAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"DELETE FROM {_table} WHERE stream_id = $1 AND perspective_name = $2",
      connection);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(perspectiveName);
    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }
}
