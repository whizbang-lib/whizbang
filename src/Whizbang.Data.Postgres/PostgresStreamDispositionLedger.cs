// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The stream lifecycle ledger's side of the scheduled disposition of sealed streams (#1734), on Postgres: the functions
/// of migration 280 (<c>wh_schedule_stream_dispositions</c>, <c>wh_set_stream_disposition_due</c>) and 297
/// (<c>wh_due_stream_dispositions</c>, and the end-of-life policies of #1740: <c>wh_stream_policy_candidates</c>,
/// <c>wh_stream_disposition_readiness</c>, <c>wh_seal_stream_if_unchanged</c>, <c>wh_set_stream_truncated_through</c>)
/// and a read of the stream's perspective cursors. Both drivers register one over
/// their <see cref="PostgresArchiveConnection"/>, so it reaches the same database and schema as the archive store.
/// </summary>
/// <param name="database">The service's database.</param>
/// <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDispositionSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamDispositionTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs</tests>
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
    await using var command = new NpgsqlCommand($"SELECT due_stream_id, readiness, due_stream_type FROM {_prefix}wh_due_stream_dispositions($1, $2)", connection);
    command.Parameters.AddWithValue(now);
    command.Parameters.AddWithValue(limit);
    var due = new List<DueStreamDisposition>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      // The SQL answers with the enum's own numbers (0 Ready, 1 Blocked, 2 Held, 3 Done).
      due.Add(new DueStreamDisposition(reader.GetGuid(0), (StreamDispositionReadiness)reader.GetInt32(1), reader.GetString(2)));
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

  /// <inheritdoc />
  public async Task<IReadOnlyList<StreamPolicyCandidate>> GetPolicyCandidatesAsync(
      IReadOnlyList<StreamEndOfLifePolicy> policies, DateTimeOffset now, int limit, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(policies);
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      "SELECT candidate_stream_id, candidate_stream_type, end_of_life_trigger, candidate_last_version, through_version " +
      $"FROM {_prefix}wh_stream_policy_candidates($1, $2, $3, $4, $5, $6, $7)", connection);
    command.Parameters.AddWithValue(now);
    command.Parameters.AddWithValue(limit);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = policies.Select(p => p.StreamType).ToArray() });
    command.Parameters.Add(_bigints(policies.Select(p => _microseconds(p.IdleTtl))));
    command.Parameters.Add(_bigints(policies.Select(p => _microseconds(p.MaxAge))));
    command.Parameters.Add(_bigints(policies.Select(p => p.KeepTailVersions)));
    command.Parameters.Add(_bigints(policies.Select(p => p.TenantByteCap)));
    var candidates = new List<StreamPolicyCandidate>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      // The SQL answers with the enum's own numbers (1 IdleTtl, 2 MaxAge, 3 TenantByteCap, 4 VersionCap); only the version
      // cap carries the prefix's last version.
      var through = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? (long?)null : reader.GetInt64(4);
      candidates.Add(new StreamPolicyCandidate(reader.GetGuid(0), reader.GetString(1), (StreamEndOfLifeTrigger)reader.GetInt32(2), reader.GetInt64(3), through));
    }
    return candidates;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyDictionary<Guid, StreamDispositionReadiness>> GetReadinessAsync(IReadOnlyList<Guid> streamIds, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT ready_stream_id, readiness FROM {_prefix}wh_stream_disposition_readiness($1)", connection);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = streamIds.ToArray() });
    var readiness = new Dictionary<Guid, StreamDispositionReadiness>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      readiness[reader.GetGuid(0)] = (StreamDispositionReadiness)reader.GetInt32(1);
    }
    return readiness;
  }

  /// <inheritdoc />
  public async Task<bool> SealIfUnchangedAsync(Guid streamId, long lastVersion, string reason, DateTimeOffset now, CancellationToken cancellationToken = default) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT {_prefix}wh_seal_stream_if_unchanged($1, $2, $3, $4)", connection);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(lastVersion);
    command.Parameters.AddWithValue(reason);
    command.Parameters.AddWithValue(now);
    return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }

  /// <inheritdoc />
  public async Task SetTruncatedThroughAsync(Guid streamId, long throughVersion, CancellationToken cancellationToken = default) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT {_prefix}wh_set_stream_truncated_through($1, $2)", connection);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(throughVersion);
    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  // A time span as whole microseconds, the resolution PostgreSQL keeps.
  private static long? _microseconds(TimeSpan? span) => span is { } value ? value.Ticks / TimeSpan.TicksPerMicrosecond : null;

  private static NpgsqlParameter _bigints(IEnumerable<long?> values) =>
    new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = values.ToArray() };
}
