// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Closes a stream's books on Postgres (#1741): reads the stream's newest Sourced version, links the opening balance to the
/// version the closing summary will take and to a new successor stream, then runs <c>wh_close_books</c> (migration 315),
/// which appends the summary, seals the stream and opens the successor in one transaction. The driver's
/// <see cref="IDirectAppendRowBuilder"/> stores each event exactly as that driver's direct append stores it.
/// </summary>
/// <param name="open">Opens a connection to the service's database.</param>
/// <param name="schema">The schema, unquoted; null resolves the functions through the connection's search path.</param>
/// <param name="rows">How the driver stores an event.</param>
/// <param name="timeProvider">The clock both events and the seal are stamped with; the system clock when null.</param>
/// <docs>fundamentals/events/closing-the-books</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CloseBooksSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperCloseBooksTests.cs</tests>
public sealed class PostgresBookCloser(
    Func<CancellationToken, ValueTask<NpgsqlConnection>> open,
    string? schema,
    IDirectAppendRowBuilder rows,
    TimeProvider? timeProvider = null) : IBookCloser {
  // An ephemeral event (8) or a compaction origin (16) is not on the ledger; a stream of only those has nothing to close.
  private const int FLAGS_OFF_THE_LEDGER = 24;

  private readonly Func<CancellationToken, ValueTask<NpgsqlConnection>> _open = open ?? throw new ArgumentNullException(nameof(open));
  private readonly IDirectAppendRowBuilder _rows = rows ?? throw new ArgumentNullException(nameof(rows));
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
  private readonly string _prefix = schema is null ? string.Empty : SqlText.Identifier(schema) + ".";

  /// <inheritdoc />
  public async Task<BookCloseResult> CloseBooksAsync<TClosing, TOpening>(
      Guid streamId, TClosing closingSummary, TOpening openingBalance, CancellationToken cancellationToken = default)
      where TClosing : IEvent
      where TOpening : OpeningBalance {
    ArgumentNullException.ThrowIfNull(closingSummary);
    ArgumentNullException.ThrowIfNull(openingBalance);
    if (streamId == Guid.Empty) {
      return new BookCloseResult(BookCloseStatus.NothingToClose);
    }
    await using var connection = await _open(cancellationToken).ConfigureAwait(false);
    if (await _newestAsync(connection, streamId, cancellationToken).ConfigureAwait(false) is not { } expected) {
      return new BookCloseResult(BookCloseStatus.NothingToClose);
    }

    var successor = TrackedGuid.New().Value;
    var finalVersion = expected + 1;
    // The link is the framework's: whatever the caller set is replaced. A record's clone keeps its runtime type.
    var linked = (TOpening)((OpeningBalance)openingBalance with { SuccessorStreamId = successor, PredecessorStreamId = streamId, PredecessorFinalVersion = finalVersion });
    var closing = await _rows.BuildAsync(closingSummary, cancellationToken).ConfigureAwait(false);
    var opening = await _rows.BuildAsync(linked, cancellationToken).ConfigureAwait(false);

    await using var command = new NpgsqlCommand(
      $"SELECT close_status FROM {_prefix}wh_close_books($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19)",
      connection);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(expected);
    _addRow(command, closing);
    command.Parameters.AddWithValue(successor);
    _addRow(command, opening);
    command.Parameters.AddWithValue($"Books closed; continued by stream {successor}");
    command.Parameters.AddWithValue(_timeProvider.GetUtcNow());
    var status = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    return status switch {
      "closed" => new BookCloseResult(BookCloseStatus.Closed, successor, finalVersion),
      "already_sealed" => new BookCloseResult(BookCloseStatus.AlreadySealed),
      "archived" => new BookCloseResult(BookCloseStatus.Archived),
      "stream_changed" => new BookCloseResult(BookCloseStatus.StreamChanged),
      _ => new BookCloseResult(BookCloseStatus.NothingToClose),
    };
  }

  // The newest Sourced version of the stream, as wh_close_books checks it, or, for a stream archived whole (nothing hot),
  // the version its tombstone records, so wh_close_books answers archived; null when it has none.
  private async Task<long?> _newestAsync(NpgsqlConnection connection, Guid streamId, CancellationToken ct) {
    await using var read = new NpgsqlCommand(
      $"SELECT COALESCE((SELECT MAX(version)::bigint FROM {_prefix}wh_event_store WHERE stream_id = $1 AND COALESCE(flags, 0) & {FLAGS_OFF_THE_LEDGER} = 0), " +
      $"(SELECT l.last_version FROM {_prefix}wh_stream_lifecycle l WHERE l.stream_id = $1 AND l.state = 'Archived'))",
      connection);
    read.Parameters.AddWithValue(streamId);
    var newest = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
    return newest is long version ? version : null;
  }

  private static void _addRow(NpgsqlCommand command, DirectAppendRow row) {
    command.Parameters.AddWithValue(row.EventId);
    command.Parameters.AddWithValue(row.AggregateType);
    command.Parameters.AddWithValue(row.EventType);
    command.Parameters.Add(DirectAppendRow.Jsonb(row.EventData));
    command.Parameters.Add(DirectAppendRow.Jsonb(row.Metadata));
    command.Parameters.Add(DirectAppendRow.Jsonb(row.Scope));
    command.Parameters.AddWithValue(row.Flags);
  }
}

/// <summary>How a driver stores an event on its direct append path (#1741), so another path can store it the same way.</summary>
/// <docs>fundamentals/events/closing-the-books</docs>
public interface IDirectAppendRowBuilder {
  /// <summary>The values the driver's direct append stores for <paramref name="payload"/>.</summary>
  /// <typeparam name="TMessage">The type the event is stored as.</typeparam>
  /// <param name="payload">The event.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The row.</returns>
  ValueTask<DirectAppendRow> BuildAsync<TMessage>(TMessage payload, CancellationToken cancellationToken = default) where TMessage : notnull;
}

/// <summary>
/// One event as a driver's direct append stores it (#1741): the values <c>wh_append_event</c> takes besides the stream and
/// the time. The bodies are JSON text, or an object the connection's JSON mapping writes (the EF Core driver's metadata).
/// </summary>
/// <param name="EventId">The event id.</param>
/// <param name="AggregateType">The CLR type name without its assembly.</param>
/// <param name="EventType">The type name with its assembly.</param>
/// <param name="EventData">The payload.</param>
/// <param name="Metadata">The envelope metadata.</param>
/// <param name="Scope">The scope, or null.</param>
/// <param name="Flags">The event's kind bits (<see cref="DirectAppendKind"/>).</param>
/// <docs>fundamentals/events/closing-the-books</docs>
public sealed record DirectAppendRow(
  Guid EventId, string AggregateType, string EventType, object EventData, object Metadata, object? Scope, int Flags) {
  /// <summary>A <c>jsonb</c> parameter holding <paramref name="value"/>, or SQL null.</summary>
  /// <param name="value">JSON text, or an object the connection's JSON mapping writes.</param>
  /// <returns>The parameter.</returns>
  public static NpgsqlParameter Jsonb(object? value) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value ?? DBNull.Value };
}
