// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The Postgres <see cref="IReceivedStreamResequencer"/> (#1629): walks the store a window of streams at a time with
/// <c>wh_received_stream_order_window</c> (migration 555), plans each out-of-order stream with
/// <see cref="ReceivedStreamOrder"/>, the replay's own rule, and applies the plan with <c>wh_resequence_stream</c>, which
/// also rewinds the stream's perspectives from its start.
/// </summary>
/// <remarks>
/// A batch is one transaction: it tries the streams' locks (<c>wh_try_lock_streams</c>, never waiting), reads the
/// streams it holds again under those locks, plans from that read and applies. A crash rolls the batch back whole, and a
/// re-run finds the streams it did not finish still out of order. A dry run reads without locks and writes nothing.
/// </remarks>
/// <param name="openConnection">Opens a connection to the service's database.</param>
/// <param name="schema">The service's schema, or null for the connection's search path.</param>
/// <param name="logger">Records the run and each batch.</param>
/// <docs>operations/infrastructure/resequencing-received-streams</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs</tests>
public sealed partial class PostgresReceivedStreamResequencer(
  Func<CancellationToken, ValueTask<NpgsqlConnection>> openConnection,
  string? schema = null,
  ILogger<PostgresReceivedStreamResequencer>? logger = null) : IReceivedStreamResequencer {

  private readonly string _prefix = schema is null ? string.Empty : SqlText.Identifier(schema) + ".";

  /// <inheritdoc />
  public async Task<ReceivedStreamResequenceReport> ResequenceAsync(
      ReceivedStreamResequenceRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    request.Validate();
    if (logger is not null) {
      LogStarting(logger, request.RunId, request.DryRun, request.RequestedBy, request.Reason);
    }
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);

    var after = request.StartAfterStreamId ?? Guid.Empty;
    var examined = 0;
    var resequenced = new List<StreamResequence>();
    var skipped = new List<StreamLeftAlone>();
    Guid? resumeAfter = null;
    var batchIndex = 0;
    while (true) {
      cancellationToken.ThrowIfCancellationRequested();
      var window = request.MaxStreams is { } max ? Math.Min(request.BatchSize, max - examined) : request.BatchSize;
      var streams = await _windowAsync(connection, after, window, cancellationToken).ConfigureAwait(false);
      if (streams.Count == 0) {
        break;
      }
      examined += streams.Count;
      after = streams[^1].StreamId;
      skipped.AddRange(streams.Where(s => s.OutOfOrder && s.Skip is not null).Select(s => new StreamLeftAlone(s.StreamId, s.Skip!.Value)));
      var candidates = streams.Where(s => s.OutOfOrder && s.Skip is null).Select(s => s.StreamId).ToArray();
      if (candidates.Length > 0) {
        var (done, left) = request.DryRun
          ? (await _planDryAsync(connection, candidates, cancellationToken).ConfigureAwait(false), [])
          : await _applyBatchAsync(connection, candidates, cancellationToken).ConfigureAwait(false);
        resequenced.AddRange(done);
        skipped.AddRange(left);
        if (logger is not null) {
          LogBatch(logger, request.RunId, batchIndex, request.DryRun, done.Count, left.Count);
        }
      }
      batchIndex++;
      if (streams.Count < window) {
        break;
      }
      if (request.MaxStreams is { } limit && examined >= limit) {
        resumeAfter = after;
        break;
      }
    }

    var report = new ReceivedStreamResequenceReport(request.RunId, request.DryRun, examined, resequenced, skipped, resumeAfter);
    if (logger is not null) {
      LogRun(logger, request.RunId, request.DryRun, examined, resequenced.Count, resequenced.Sum(s => s.Moves.Count),
        skipped.Count, resumeAfter);
    }
    return report;
  }

  /// <summary>One stream of a window: whether it is out of its origins' order, and why it is left alone if it is.</summary>
  private readonly record struct WindowStream(Guid StreamId, bool OutOfOrder, ResequenceSkipReason? Skip);

  private async Task<List<WindowStream>> _windowAsync(
      NpgsqlConnection connection, Guid after, int window, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      $"SELECT stream_id, out_of_order, skip_reason FROM {_prefix}wh_received_stream_order_window($1, $2)", connection);
    command.Parameters.AddWithValue(after);
    command.Parameters.AddWithValue(window);
    var streams = new List<WindowStream>(window);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      streams.Add(new WindowStream(reader.GetGuid(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : Enum.Parse<ResequenceSkipReason>(reader.GetString(2))));
    }
    return streams;
  }

  /// <summary>A dry run: each stream's plan from a read without locks, and what applying it would rewind.</summary>
  private async Task<List<StreamResequence>> _planDryAsync(
      NpgsqlConnection connection, Guid[] streams, CancellationToken cancellationToken) {
    var plans = await _plansAsync(connection, null, streams, cancellationToken).ConfigureAwait(false);
    var result = new List<StreamResequence>(plans.Count);
    if (plans.Count == 0) {
      return result;
    }
    await using var command = new NpgsqlCommand(
      $"SELECT s, (SELECT count(*)::int FROM {_prefix}wh_perspective_cursors c WHERE c.stream_id = s AND c.perspective_name <> '__collective__'), "
      + $"(SELECT count(*)::int FROM {_prefix}wh_perspective_snapshots sn WHERE sn.stream_id = s) FROM unnest($1) AS s",
      connection);
    command.Parameters.Add(new NpgsqlParameter { Value = plans.Keys.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var stream = reader.GetGuid(0);
      result.Add(new StreamResequence(stream, plans[stream], reader.GetInt32(1), reader.GetInt32(2)));
    }
    return result;
  }

  /// <summary>One batch, in one transaction: the locks it can take, a read under them, the plans applied.</summary>
  private async Task<(List<StreamResequence> Done, List<StreamLeftAlone> Left)> _applyBatchAsync(
      NpgsqlConnection connection, Guid[] streams, CancellationToken cancellationToken) {
    var done = new List<StreamResequence>();
    var left = new List<StreamLeftAlone>();
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    var locked = new HashSet<Guid>();
    await using (var lockCommand = new NpgsqlCommand($"SELECT s FROM {_prefix}wh_try_lock_streams($1) AS s", connection, transaction)) {
      lockCommand.Parameters.Add(new NpgsqlParameter { Value = streams, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
      await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        locked.Add(reader.GetGuid(0));
      }
    }
    left.AddRange(streams.Where(s => !locked.Contains(s)).Select(s => new StreamLeftAlone(s, ResequenceSkipReason.Busy)));

    var plans = await _plansAsync(connection, transaction, [.. locked], cancellationToken).ConfigureAwait(false);
    foreach (var (stream, moves) in plans.OrderBy(p => p.Key)) {
      await using var apply = new NpgsqlCommand(
        $"SELECT outcome, perspectives_rewound, snapshots_removed FROM {_prefix}wh_resequence_stream($1, $2, $3, $4, now())",
        connection, transaction);
      apply.Parameters.AddWithValue(stream);
      apply.Parameters.Add(new NpgsqlParameter { Value = moves.Select(m => m.EventId).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
      apply.Parameters.Add(new NpgsqlParameter { Value = moves.Select(m => checked((int)m.FromVersion)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
      apply.Parameters.Add(new NpgsqlParameter { Value = moves.Select(m => checked((int)m.ToVersion)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
      await using var reader = await apply.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      if (reader.GetString(0) == "resequenced") {
        done.Add(new StreamResequence(stream, moves, reader.GetInt32(1), reader.GetInt32(2)));
      } else {
        left.Add(new StreamLeftAlone(stream, ResequenceSkipReason.Changed));
      }
    }

    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    return (done, left);
  }

  /// <summary>The plan of each stream that needs one, from one read of the streams' events.</summary>
  private async Task<Dictionary<Guid, IReadOnlyList<ResequencedEvent>>> _plansAsync(
      NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid[] streams, CancellationToken cancellationToken) {
    var events = new Dictionary<Guid, List<StoredStreamEvent>>();
    if (streams.Length > 0) {
      await using var read = new NpgsqlCommand(
        "SELECT stream_id, event_id, version, COALESCE(origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid), "
        + $"COALESCE(origin_commit_sequence, 0) FROM {_prefix}wh_event_store WHERE stream_id = ANY($1) ORDER BY stream_id, version",
        connection, transaction);
      read.Parameters.Add(new NpgsqlParameter { Value = streams, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
      await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var stream = reader.GetGuid(0);
        if (!events.TryGetValue(stream, out var list)) {
          events[stream] = list = [];
        }
        list.Add(new StoredStreamEvent(reader.GetGuid(1), reader.GetInt32(2), reader.GetGuid(3), reader.GetInt64(4)));
      }
    }
    var plans = new Dictionary<Guid, IReadOnlyList<ResequencedEvent>>();
    foreach (var (stream, list) in events) {
      var moves = ReceivedStreamOrder.Resequence(list);
      if (moves.Count > 0) {
        plans[stream] = moves;
      }
    }
    return plans;
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Received stream re-sequence {RunId} started (dry run: {DryRun}); requested by {RequestedBy}: {Reason}")]
  static partial void LogStarting(ILogger logger, Guid runId, bool dryRun, string requestedBy, string reason);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Received stream re-sequence {RunId} batch {BatchIndex} (dry run: {DryRun}): {Resequenced} stream(s) re-sequenced, {LeftAlone} left for a re-run")]
  static partial void LogBatch(ILogger logger, Guid runId, int batchIndex, bool dryRun, int resequenced, int leftAlone);

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "A generated structured log message: each value is its own searchable property of the run's outcome.")]
  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Received stream re-sequence {RunId} (dry run: {DryRun}): {Examined} stream(s) examined, {Resequenced} re-sequenced ({Moved} event(s) moved, their perspectives rewound from the stream's start), {LeftAlone} left alone; resume after {ResumeAfter}")]
  static partial void LogRun(ILogger logger, Guid runId, bool dryRun, int examined, int resequenced, int moved, int leftAlone, Guid? resumeAfter);
}
