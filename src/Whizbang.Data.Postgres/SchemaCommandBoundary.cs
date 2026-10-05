// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Splits a schema script at the points where one statement can only see the effect of an earlier
/// one after that earlier one has committed, and applies the pieces in order, committing each.
/// </summary>
/// <remarks>
/// <para>
/// Ordering statements inside one transaction is not enough for every dependency. An index over an
/// expression is built by evaluating that expression on every heap tuple that is not yet dead, and a
/// row version superseded by an uncommitted update stays live, because other transactions can still
/// see it. So an index built in the same transaction that rewrote the column it indexes is built
/// over the values as they were before the rewrite. The rewrite is correct, its ordering is correct,
/// and the index still fails.
/// </para>
/// <para>
/// A commit is necessary and not sufficient either. A plain <c>CREATE INDEX</c> indexes every row
/// version an open snapshot can still see, so while any session holds a snapshot taken before the
/// rewrite committed, the version the rewrite replaced is indexed too, in its old rendering. So a
/// piece that wrote is followed by a wait for the sessions older than it, bounded by the command
/// timeout, before the next piece begins.
/// </para>
/// <para>
/// The failure is worse than a failed startup. The whole transaction rolls back, which undoes the
/// rewrite along with the index, so the next attempt begins from the same state and fails the same
/// way. A retry loop around it never makes progress and the schema stays behind forever while the
/// service reports only that it is still migrating.
/// </para>
/// <para>
/// A boundary marker is emitted by the generator rather than inferred here. Deciding where a script
/// needs one means knowing which statement depends on which other statement's committed effect,
/// which is what the generator knows and a string this type receives does not. Splitting on
/// statement terminators instead would also break every function body, which carries them.
/// </para>
/// <para>
/// Each piece is applied on its own connection so it commits independently of any ambient
/// transaction. That is deliberate: a data rewrite that has succeeded must survive a later failure
/// in the same startup, or the retry cannot get further than the attempt before it. Mutual exclusion
/// between instances is unaffected, because the caller reaches this only while holding the schema
/// initialization lock.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#statements-that-need-a-commit-between-them</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaCommandBoundaryTests.cs</tests>
public static class SchemaCommandBoundary {
  /// <summary>
  /// The line a generator emits where the script must commit before continuing.
  /// </summary>
  /// <remarks>
  /// A comment, so a script carrying it stays valid SQL. Running the whole script through a client
  /// that does not know about this type is then still correct in every case except the dependency
  /// the marker describes, which is the case this type exists for.
  /// </remarks>
  public const string MARKER = "-- @whizbang:commit-boundary";

  /// <summary>
  /// The pieces of <paramref name="sql"/> that must be applied and committed in order.
  /// </summary>
  /// <param name="sql">The script, with or without markers.</param>
  /// <returns>
  /// One entry per piece, in order, with no piece that is only whitespace. A script carrying no
  /// marker yields itself, so a caller can apply the result the same way in both cases.
  /// </returns>
  public static ImmutableArray<string> Segments(string sql) {
    ArgumentNullException.ThrowIfNull(sql);

    if (!sql.Contains(MARKER, StringComparison.Ordinal)) {
      return string.IsNullOrWhiteSpace(sql) ? [] : [sql];
    }

    var segments = ImmutableArray.CreateBuilder<string>();
    var current = new List<string>();

    foreach (var line in sql.Split('\n')) {
      if (line.Trim().Equals(MARKER, StringComparison.Ordinal)) {
        _flush(segments, current);
        continue;
      }

      current.Add(line);
    }

    _flush(segments, current);
    return segments.ToImmutable();
  }

  /// <summary>
  /// Applies <paramref name="sql"/> piece by piece, committing each piece before the next begins,
  /// and reporting a wait at a boundary nowhere.
  /// </summary>
  /// <param name="connectionFactory">
  /// Produces a fresh, unopened connection for each piece. A factory rather than a connection
  /// string because the string is frequently not available: Npgsql redacts the password from every
  /// <c>ConnectionString</c> surface once a connection has opened, and a context configured with a
  /// data source never had one to redact. The data source itself still holds the credentials, so a
  /// caller in that position hands over <c>CreateConnection</c> and this works unchanged.
  /// </param>
  /// <param name="sql">The script, with or without markers.</param>
  /// <param name="commandTimeoutSeconds">
  /// The timeout for each piece. A rewrite over a large table is one statement that legitimately
  /// takes far longer than an ordinary command, so the caller's schema timeout is used rather than
  /// the connection's default.
  /// </param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <remarks>
  /// Committing progressively is the point, so this deliberately takes no transaction: a caller that
  /// passed one would get the behavior this type exists to avoid.
  /// </remarks>
  public static Task ApplyAsync(
      Func<NpgsqlConnection> connectionFactory,
      string sql,
      int commandTimeoutSeconds,
      CancellationToken cancellationToken = default) =>
    ApplyAsync(connectionFactory, sql, commandTimeoutSeconds, logger: null, cancellationToken);

  /// <summary>
  /// Applies <paramref name="sql"/> piece by piece, committing each piece and waiting until no older
  /// snapshot can see what it replaced before the next begins.
  /// </summary>
  /// <param name="connectionFactory">
  /// Produces a fresh, unopened connection for each piece. See the overload without a logger.
  /// </param>
  /// <param name="sql">The script, with or without markers.</param>
  /// <param name="commandTimeoutSeconds">
  /// The timeout for each piece, and the longest the apply waits at a boundary for older snapshots.
  /// </param>
  /// <param name="logger">Where a wait at a boundary is reported, naming what it waits for.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <remarks>
  /// <para>
  /// A commit between the pieces is necessary and not sufficient. A plain <c>CREATE INDEX</c>
  /// indexes every row version an open snapshot can still see, and a snapshot taken before a piece
  /// committed can still see the rows that piece replaced, in their old rendering. So a piece that
  /// wrote is followed by a wait for every session older than it, the way
  /// <c>CREATE INDEX CONCURRENTLY</c> waits for older transactions. A piece that wrote nothing
  /// replaced nothing, and the next piece follows it at once.
  /// </para>
  /// <para>
  /// Each piece runs in a transaction of its own so its transaction id can be read before it
  /// commits, which is what makes the wait exact: a session that began after the piece committed is
  /// never waited for. A piece holding a statement that cannot run in a transaction, such as
  /// <c>VACUUM</c>, does not belong between boundaries.
  /// </para>
  /// </remarks>
  /// <exception cref="TimeoutException">
  /// A session older than a piece that wrote was still open when the budget ran out. The piece has
  /// committed and stays; the message names the sessions, and the next apply continues from there.
  /// </exception>
  public static async Task ApplyAsync(
      Func<NpgsqlConnection> connectionFactory,
      string sql,
      int commandTimeoutSeconds,
      ILogger? logger,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(sql);

    var log = logger ?? NullLogger.Instance;
    string? previousWriter = null;

    foreach (var segment in Segments(sql)) {
      await using var connection = connectionFactory();
      await connection.OpenAsync(cancellationToken);

      if (previousWriter is not null) {
        var holders = await SupersededRowVersionFence.WaitAsync(
          connection, previousWriter, TimeSpan.FromSeconds(commandTimeoutSeconds), TimeProvider.System,
          log, cancellationToken);
        if (holders.Count > 0) {
          throw new TimeoutException(
            $"Schema SQL could not continue past a commit boundary within {commandTimeoutSeconds}s: "
            + $"{holders.Count} session(s) still hold a snapshot older than the piece before it, and "
            + "can still see the rows that piece replaced, which an index built now would meet in "
            + $"their old form. The piece has committed; apply again once they finish: {string.Join("; ", holders)}");
        }
      }

      await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
      await using (var command = new NpgsqlCommand(segment, connection, transaction) {
        CommandTimeout = commandTimeoutSeconds,
      }) {
        await command.ExecuteNonQueryAsync(cancellationToken);
      }

      previousWriter = await SupersededRowVersionFence.CaptureWriterAsync(
        connection, transaction, cancellationToken);
      await transaction.CommitAsync(cancellationToken);
    }
  }

  /// <summary>
  /// Applies <paramref name="sql"/> piece by piece, opening each piece's connection from
  /// <paramref name="connectionString"/>.
  /// </summary>
  /// <param name="connectionString">Where to apply it. Must still carry its credentials.</param>
  /// <param name="sql">The script, with or without markers.</param>
  /// <param name="commandTimeoutSeconds">The timeout for each piece.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public static Task ApplyAsync(
      string connectionString,
      string sql,
      int commandTimeoutSeconds,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

    return ApplyAsync(
      () => new NpgsqlConnection(connectionString), sql, commandTimeoutSeconds, cancellationToken);
  }

  private static void _flush(ImmutableArray<string>.Builder segments, List<string> lines) {
    var segment = string.Join("\n", lines);
    lines.Clear();

    if (!string.IsNullOrWhiteSpace(segment)) {
      segments.Add(segment);
    }
  }
}
