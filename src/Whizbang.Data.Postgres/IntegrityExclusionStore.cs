// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// One bucket's adjustment (#2588): what the origin's answer counts that this side excluded. <see cref="Digest"/> is the
/// XOR of the held marks' lanes and their count, to be removed from the origin's bucket; <see cref="Pending"/> marks of
/// the bucket have no known place at the origin yet, so the bucket is undecided.
/// </summary>
/// <param name="Digest">The lanes and count to remove (its <see cref="StreamDigest.StreamId"/> is empty for a type bucket).</param>
/// <param name="Pending">Marks of the bucket still pending an answer from the origin.</param>
/// <docs>resilience/integrity-exclusions#cross-service-agreement</docs>
public sealed record IntegrityExclusionAdjustmentRow(StreamDigest Digest, int Pending);

/// <summary>
/// The integrity engine's reads and writes of the exclusion marks (#2588, migration 663): the comparison's adjustment,
/// the pending marks to ask their origin about, and the settling of those marks from the origin's answer. Reached through
/// <see cref="PostgresArchiveConnection"/>, as the origin-position fill is.
/// </summary>
/// <docs>resilience/integrity-exclusions#cross-service-agreement</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionAgreementTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed class IntegrityExclusionStore {
  private readonly PostgresArchiveConnection _database;
  private readonly string _prefix;

  /// <summary>Creates the store over the service's own database.</summary>
  /// <param name="database">How to reach it.</param>
  public IntegrityExclusionStore(PostgresArchiveConnection database) {
    ArgumentNullException.ThrowIfNull(database);
    _database = database;
    _prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
  }

  /// <summary>
  /// Per bucket, what to remove from an origin's answer (<c>wh_integrity_exclusion_adjustment</c>): the held marks inside
  /// the window <c>[since, until)</c> of the origin's commit sequences, or all of them without a window, and the pending
  /// marks. One index probe that returns nothing for an origin with no marks.
  /// </summary>
  /// <param name="originServiceId">The origin.</param>
  /// <param name="eventTypes">The types compared; null for every type.</param>
  /// <param name="streamIds">The streams compared; null for every stream.</param>
  /// <param name="since">The window's first origin commit sequence, or null for the whole history.</param>
  /// <param name="until">The window's end (exclusive), or null for no end.</param>
  /// <param name="byStream">Per stream, or per (tenant, type) with the empty stream.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The buckets with anything to adjust.</returns>
  public async Task<IReadOnlyList<IntegrityExclusionAdjustmentRow>> AdjustmentAsync(
      Guid originServiceId, IReadOnlyList<string>? eventTypes, IReadOnlyList<Guid>? streamIds, long? since, long? until,
      bool byStream, CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"""
      SELECT tenant_scope, event_type, stream_id, digest_lo, digest_hi, id_lo, id_hi, event_count, pending_count
      FROM {_prefix}wh_integrity_exclusion_adjustment($1, $2, $3, $4, $5, $6)
      """, connection);
    command.Parameters.AddWithValue(originServiceId);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = (object?)eventTypes?.ToArray() ?? DBNull.Value });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = (object?)streamIds?.ToArray() ?? DBNull.Value });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)since ?? DBNull.Value });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)until ?? DBNull.Value });
    command.Parameters.AddWithValue(byStream);
    var rows = new List<IntegrityExclusionAdjustmentRow>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var tenant = reader.GetString(0);
      rows.Add(new IntegrityExclusionAdjustmentRow(new StreamDigest {
        TenantScope = tenant.Length == 0 ? null : tenant,
        EventType = reader.GetString(1),
        StreamId = reader.GetGuid(2),
        DigestLo = reader.GetInt64(3),
        DigestHi = reader.GetInt64(4),
        IdLo = reader.GetInt64(5),
        IdHi = reader.GetInt64(6),
        EventCount = reader.GetInt32(7),
      }, reader.GetInt32(8)));
    }
    return rows;
  }

  /// <summary>The next batch of pending marks to ask one origin about (<c>wh_integrity_exclusion_ask</c>).</summary>
  /// <param name="batchSize">The most marks to ask about.</param>
  /// <param name="lapPause">How long before a mark asked already is asked again.</param>
  /// <param name="now">The clock.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The origin and the events, or null when nothing is due.</returns>
  public async Task<OriginPositionAsk?> AskAsync(int batchSize, TimeSpan lapPause, DateTimeOffset now, CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT origin_service_id, event_ids FROM {_prefix}wh_integrity_exclusion_ask($1, $2, $3)", connection);
    command.Parameters.AddWithValue(batchSize);
    command.Parameters.AddWithValue(lapPause);
    command.Parameters.AddWithValue(now);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      return null;
    }
    return new OriginPositionAsk(reader.GetGuid(0), reader.GetFieldValue<Guid[]>(1), LapEnded: false);
  }

  /// <summary>
  /// Settles this origin's pending marks from its answer (<c>wh_integrity_exclusion_settle</c>): a position makes a mark
  /// held, an unknown answer makes it unknown to the origin, an unstamped one stays pending. Where the answer names the
  /// bucket the origin holds the event in, the mark takes it, so the adjustment removes it from the bucket the origin
  /// counts it in.
  /// </summary>
  /// <param name="answer">The origin's answer.</param>
  /// <param name="now">The clock.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The answered events that are exclusions of the origin, whatever their state: the caller leaves them out
  /// of the position fill.</returns>
  public async Task<IReadOnlySet<Guid>> SettleAsync(OriginPositionsAnswer answer, DateTimeOffset now, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(answer);
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT {_prefix}wh_integrity_exclusion_settle($1, $2, $3, $4, $5, $6, $7, $8)", connection);
    command.Parameters.AddWithValue(answer.OriginServiceId);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = answer.Positions.Select(p => p.EventId).ToArray() });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = answer.Positions.Select(p => p.CommitSequence).ToArray() });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = answer.Unknown.ToArray() });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = answer.Positions.Select(p => p.TenantScope).ToArray() });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = answer.Positions.Select(p => p.EventType).ToArray() });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = answer.Positions.Select(p => p.StreamId).ToArray() });
    command.Parameters.AddWithValue(now);
    var marks = (Guid[])(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    return marks.ToHashSet();
  }
}
