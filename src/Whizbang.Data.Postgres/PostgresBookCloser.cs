// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;
using Whizbang.Core.Lifecycle;

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
  private readonly Func<CancellationToken, ValueTask<NpgsqlConnection>> _open = open ?? throw new ArgumentNullException(nameof(open));
  private readonly IDirectAppendRowBuilder _rows = rows ?? throw new ArgumentNullException(nameof(rows));
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
  private readonly string _prefix = schema is null ? string.Empty : SqlText.Identifier(schema) + ".";

  /// <inheritdoc />
  public Task<BookCloseResult> CloseBooksAsync<TClosing, TOpening>(
      Guid streamId, TClosing closingSummary, TOpening openingBalance, CancellationToken cancellationToken = default)
      where TClosing : IEvent
      where TOpening : OpeningBalance =>
    throw new NotImplementedException($"{_open}{_rows}{_timeProvider}{_prefix}{streamId}{closingSummary}{openingBalance}");
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
