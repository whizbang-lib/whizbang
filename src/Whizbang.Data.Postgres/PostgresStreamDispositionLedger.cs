// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Lifecycle;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The stream lifecycle ledger's side of the scheduled disposition of sealed streams (#1734), on Postgres: the functions
/// of migration 280 (<c>wh_schedule_stream_dispositions</c>, <c>wh_due_stream_dispositions</c>,
/// <c>wh_set_stream_disposition_due</c>) and a read of the stream's perspective cursors. Both drivers register one over
/// their <see cref="PostgresArchiveConnection"/>, so it reaches the same database and schema as the archive store.
/// </summary>
/// <param name="database">The service's database.</param>
/// <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDispositionSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamDispositionTests.cs</tests>
public sealed class PostgresStreamDispositionLedger(PostgresArchiveConnection database) : IStreamDispositionLedger {
  private readonly PostgresArchiveConnection _database = database ?? throw new ArgumentNullException(nameof(database));

  private string _prefix => _database.Schema is null ? string.Empty : SqlText.Identifier(_database.Schema) + ".";

  /// <inheritdoc />
  public async Task<int> ScheduleAsync(TimeSpan grace, int limit, DateTimeOffset now, CancellationToken cancellationToken = default) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT {_prefix}wh_schedule_stream_dispositions($1, $2, $3)", connection);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Interval, Value = grace });
    command.Parameters.AddWithValue(limit);
    command.Parameters.AddWithValue(now);
    return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<DueStreamDisposition>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT due_stream_id, readiness FROM {_prefix}wh_due_stream_dispositions($1, $2)", connection);
    command.Parameters.AddWithValue(now);
    command.Parameters.AddWithValue(limit);
    var due = new List<DueStreamDisposition>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      // The SQL answers with the enum's own numbers (0 Ready, 1 Blocked, 2 Held, 3 Done).
      due.Add(new DueStreamDisposition(reader.GetGuid(0), (StreamDispositionReadiness)reader.GetInt32(1)));
    }
    return due;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<StreamCursorPosition>> GetCursorsAsync(Guid streamId, CancellationToken cancellationToken = default) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT perspective_name, last_event_id FROM {_prefix}wh_perspective_cursors WHERE stream_id = $1 ORDER BY perspective_name", connection);
    command.Parameters.AddWithValue(streamId);
    var cursors = new List<StreamCursorPosition>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      cursors.Add(new StreamCursorPosition(reader.GetString(0), await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(1)));
    }
    return cursors;
  }

  /// <inheritdoc />
  public async Task SetDueAsync(Guid streamId, DateTimeOffset? due, CancellationToken cancellationToken = default) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT {_prefix}wh_set_stream_disposition_due($1, $2)", connection);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = due is { } at ? at : DBNull.Value });
    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }
}
