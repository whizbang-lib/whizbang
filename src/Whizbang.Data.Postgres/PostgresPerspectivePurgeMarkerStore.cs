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
  private readonly string _table = (schema is null ? string.Empty : SqlText.Identifier(schema) + ".") + "wh_stream_purge_markers";

  /// <inheritdoc />
  public async Task<bool> IsPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT EXISTS (SELECT 1 FROM {_table} WHERE stream_id = $1 AND perspective_name IN ($2, $3))",
      connection);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(perspectiveName);
    command.Parameters.AddWithValue(PerspectivePurgeMarkers.ALL_PERSPECTIVES);
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
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
