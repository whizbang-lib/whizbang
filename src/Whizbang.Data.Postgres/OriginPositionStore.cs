// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>The next batch of missing origin positions to ask one origin for (#2544).</summary>
/// <param name="OriginServiceId">The origin the events were received from.</param>
/// <param name="EventIds">The events to ask for; empty when every row the batch read was answered unknown before.</param>
/// <param name="LapEnded">True when the walk reached the end of the origin's held rows and pauses before the next lap.</param>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
public sealed record OriginPositionAsk(Guid OriginServiceId, IReadOnlyList<Guid> EventIds, bool LapEnded);

/// <summary>What an origin answered for one asked event (#2544).</summary>
/// <param name="EventId">The event.</param>
/// <param name="State"><c>positioned</c>, <c>unstamped</c>, <c>archived</c> or <c>unknown</c>.</param>
/// <param name="CommitSequence">The origin's commit sequence when it has one.</param>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
public sealed record OriginPositionLookup(Guid EventId, string State, long? CommitSequence);

/// <summary>What applying one origin's position answer did (#2544).</summary>
/// <param name="Filled">Positions filled (only ever NULL ones).</param>
/// <param name="UnknownRecorded">Events recorded as unknown to the origin, never asked again.</param>
/// <param name="StreamsReordered">Streams where the fill changed the replay order of at least one perspective's events.</param>
/// <param name="PerspectivesRewound">Perspective cursors newly flagged to replay their stream from the first version their
/// own replay order changed at; a cursor an earlier fill already flagged is not counted again.</param>
/// <param name="RewindsSkipped">Rewinds the fill's reorder asked for and did not make, because no event at or after the
/// changed version that the perspective reads still has a body: the reap deleted them past their retention, so there is
/// nothing to replay (#2591). The cursor stays where it is and keeps its snapshots.</param>
/// <param name="FirstSkippedRewind">The first skipped rewind, for the log line that reports them; null when none was.</param>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
public sealed record OriginPositionFillResult(int Filled, int UnknownRecorded, int StreamsReordered, int PerspectivesRewound,
  int RewindsSkipped = 0, OriginPositionSkippedRewind? FirstSkippedRewind = null);

/// <summary>A rewind the fill asked for and skipped, because nothing at or after its trigger can be read (#2591).</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="PerspectiveName">The perspective whose cursor stays where it is.</param>
/// <param name="TriggerEventId">The event the fill's reorder named, whose body (and every later one the perspective reads)
/// was reaped.</param>
/// <docs>resilience/stream-integrity#a-rewind-never-reaches-below-the-reap-floor</docs>
public sealed record OriginPositionSkippedRewind(Guid StreamId, string PerspectiveName, Guid TriggerEventId);

/// <summary>
/// The database side of filling missing origin positions (#2544, migration 561): the receiver's walk over the events it
/// holds without a position, the origin's lookup, and the receiver's fill. Reached through
/// <see cref="PostgresArchiveConnection"/>, as the other maintenance-cycle integrity work is.
/// </summary>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed class OriginPositionStore {
  /// <summary>The longest list of event ids one answer covers; the rest of a larger request is asked again on a later lap.</summary>
  public const int MAX_EVENTS_PER_ANSWER = 10_000;

  private readonly PostgresArchiveConnection _database;
  private readonly string _prefix;

  /// <summary>Creates the store over the service's own database.</summary>
  /// <param name="database">How to reach it.</param>
  public OriginPositionStore(PostgresArchiveConnection database) {
    ArgumentNullException.ThrowIfNull(database);
    _database = database;
    _prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
  }

  /// <summary>Takes the next batch to ask one origin for, advancing that origin's walk (<c>wh_origin_position_ask</c>).</summary>
  /// <param name="batchSize">The most held rows the batch reads.</param>
  /// <param name="lapPause">The pause before a walk that reached the end starts over.</param>
  /// <param name="now">The clock.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The batch, or null when no origin is due.</returns>
  public async Task<OriginPositionAsk?> AskAsync(int batchSize, TimeSpan lapPause, DateTimeOffset now, CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT origin_service_id, event_ids, lap_ended FROM {_prefix}wh_origin_position_ask($1, $2, $3)", connection);
    command.Parameters.AddWithValue(batchSize);
    command.Parameters.AddWithValue(lapPause);
    command.Parameters.AddWithValue(now);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      return null;
    }
    return new OriginPositionAsk(reader.GetGuid(0), reader.GetFieldValue<Guid[]>(1), reader.GetBoolean(2));
  }

  /// <summary>The origin's side: what it can say about each asked event (<c>wh_origin_positions_of</c>).</summary>
  /// <param name="eventIds">The asked events; at most <see cref="MAX_EVENTS_PER_ANSWER"/> are looked up.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>One lookup per distinct id looked up.</returns>
  public async Task<IReadOnlyList<OriginPositionLookup>> PositionsOfAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(eventIds);
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT event_id, state, commit_sequence FROM {_prefix}wh_origin_positions_of($1)", connection);
    var asked = eventIds.Take(MAX_EVENTS_PER_ANSWER).ToArray();
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = asked });
    var lookups = new List<OriginPositionLookup>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      lookups.Add(new OriginPositionLookup(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
    }
    return lookups;
  }

  /// <summary>
  /// The receiver's side: applies one origin's answer in one transaction. The streams the fill touches are locked and
  /// that origin's events of them read (<c>wh_origin_position_fill_plan</c>), with the cursors that read those streams and
  /// the event types each one's perspective handles (<c>wh_origin_position_fill_readers</c>). Each cursor whose own events'
  /// replay order the fill changes (<see cref="ReceivedStreamOrder.FirstSlotTheFillChanges"/>) is rewound from the first
  /// changed version (#2571); the positions are filled, NULL ones only, and the unknown answers recorded
  /// (<c>wh_fill_origin_positions</c>).
  /// </summary>
  /// <param name="answer">The origin's answer.</param>
  /// <param name="now">The clock.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>What the fill did.</returns>
  public async Task<OriginPositionFillResult> FillAsync(OriginPositionsAnswer answer, DateTimeOffset now, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(answer);
    var fills = new Dictionary<Guid, long>();
    foreach (var position in answer.Positions) {
      fills[position.EventId] = position.CommitSequence;
    }
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    var streams = new Dictionary<Guid, List<(StoredStreamEvent Event, string Type)>>();
    await using (var plan = new NpgsqlCommand(
      $"SELECT stream_id, event_id, version, origin_commit_sequence, event_type FROM {_prefix}wh_origin_position_fill_plan($1, $2)",
      connection, transaction)) {
      plan.Parameters.AddWithValue(answer.OriginServiceId);
      plan.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = fills.Keys.ToArray() });
      await using var reader = await plan.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var stream = reader.GetGuid(0);
        if (!streams.TryGetValue(stream, out var events)) {
          streams[stream] = events = [];
        }
        events.Add((new StoredStreamEvent(reader.GetGuid(1), reader.GetInt64(2), answer.OriginServiceId,
          reader.IsDBNull(3) ? 0 : reader.GetInt64(3)), reader.GetString(4)));
      }
    }
    var rewinds = new List<(Guid Stream, string Perspective, Guid Trigger)>();
    if (streams.Count > 0) {
      await using var readers = new NpgsqlCommand(
        $"SELECT stream_id, perspective_name, event_types FROM {_prefix}wh_origin_position_fill_readers($1)", connection, transaction);
      readers.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = streams.Keys.ToArray() });
      await using var reader = await readers.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var stream = reader.GetGuid(0);
        var types = reader.IsDBNull(2) ? null : reader.GetFieldValue<string[]>(2);
        if (ReceivedStreamOrder.FirstSlotTheFillChanges(_eventsRead(streams[stream], types), fills) is StoredStreamEvent first) {
          rewinds.Add((stream, reader.GetString(1), first.EventId));
        }
      }
    }
    // #2591: each rewind starts no earlier than the reap floor; one with no floor is skipped and reported.
    var (floored, skipped) = await _reapFloorAsync(connection, transaction, rewinds, cancellationToken).ConfigureAwait(false);
    rewinds = floored;
    OriginPositionFillResult result;
    await using (var fill = new NpgsqlCommand(
      $"SELECT filled, unknown_recorded, perspectives_rewound FROM {_prefix}wh_fill_origin_positions($1, $2, $3, $4, $5, $6, $7, $8)",
      connection, transaction)) {
      fill.Parameters.AddWithValue(answer.OriginServiceId);
      fill.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = fills.Keys.ToArray() });
      fill.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = fills.Values.ToArray() });
      fill.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = answer.Unknown.ToArray() });
      fill.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = rewinds.Select(r => r.Stream).ToArray() });
      fill.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = rewinds.Select(r => r.Perspective).ToArray() });
      fill.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = rewinds.Select(r => r.Trigger).ToArray() });
      fill.Parameters.AddWithValue(now);
      await using var reader = await fill.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      result = new OriginPositionFillResult(reader.GetInt32(0), reader.GetInt32(1),
        rewinds.Select(r => r.Stream).Concat(skipped.Select(r => r.StreamId)).Distinct().Count(),
        reader.GetInt32(2), skipped.Count, skipped.FirstOrDefault());
    }
    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    return result;
  }

  /// <summary>
  /// #2591: the rewinds the fill asked for, each moved to its reap floor (<c>wh_rewind_reap_floor</c>): the first event at
  /// or after its trigger that still has a body and that the perspective reads. A rewind with no floor cannot replay
  /// anything; it is returned among the skipped ones, and the fill does not make it.
  /// </summary>
  private async Task<(List<(Guid Stream, string Perspective, Guid Trigger)> Floored, List<OriginPositionSkippedRewind> Skipped)> _reapFloorAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, List<(Guid Stream, string Perspective, Guid Trigger)> rewinds,
      CancellationToken cancellationToken) {
    var floored = new List<(Guid Stream, string Perspective, Guid Trigger)>(rewinds.Count);
    var skipped = new List<OriginPositionSkippedRewind>();
    if (rewinds.Count == 0) {
      return (floored, skipped);
    }
    await using var floor = new NpgsqlCommand(
      $"SELECT stream_id, perspective_name, asked_trigger_event_id, trigger_event_id FROM {_prefix}wh_rewind_reap_floor($1, $2, $3)",
      connection, transaction);
    floor.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = rewinds.Select(r => r.Stream).ToArray() });
    floor.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = rewinds.Select(r => r.Perspective).ToArray() });
    floor.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = rewinds.Select(r => r.Trigger).ToArray() });
    await using var reader = await floor.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      if (reader.IsDBNull(3)) {
        skipped.Add(new OriginPositionSkippedRewind(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2)));
      } else {
        floored.Add((reader.GetGuid(0), reader.GetString(1), reader.GetGuid(3)));
      }
    }
    return (floored, skipped);
  }

  // #2571: the origin's events a perspective replays, those of the types its association rows name. #2601: none when it
  // has no association rows. The store queues a perspective only the types its rows name (the fan-out joins them), and a
  // service's startup deletes the rows of a perspective it no longer runs, so a cursor with none is one nothing here runs:
  // rewinding it would queue a replay no runner takes.
  private static StoredStreamEvent[] _eventsRead(List<(StoredStreamEvent Event, string Type)> events, string[]? types) =>
    types is null
      ? []
      : [.. events.Where(e => Array.IndexOf(types, e.Type) >= 0).Select(e => e.Event)];

  /// <summary>Per origin, the eligible received events still held without a position (<c>wh_origin_positions_unfilled</c>).</summary>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The count per origin; origins with none are left out.</returns>
  public async Task<IReadOnlyList<(Guid OriginServiceId, long Unfilled)>> UnfilledAsync(CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT origin_service_id, unfilled FROM {_prefix}wh_origin_positions_unfilled()", connection);
    var counts = new List<(Guid, long)>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      counts.Add((reader.GetGuid(0), reader.GetInt64(1)));
    }
    return counts;
  }
}
