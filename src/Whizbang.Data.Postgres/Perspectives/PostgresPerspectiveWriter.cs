// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>
/// Writes a batch of pending perspective writes on one connection: grouped by table and statement shape, one
/// statement per group, with each row's outcome told apart the way the per-stream write tells it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Outcomes.</b> A row the statement returned was written. A row an upsert did not return was refused by the
/// ordering guard. A row an insert or an update did not return is explained by one read of the group's versions:
/// an update whose row is still at the version the apply read was refused by the guard, and anything else (the
/// row moved, or vanished, or an insert found one) is a conflict, as the per-stream write reports it.
/// </para>
/// <para>
/// <b>Failure isolation.</b> A statement the database rejects (a constraint, a value a column cannot take) or a
/// value Npgsql cannot bind is split in halves until the rejected rows stand alone; those are failed and every
/// other row is written. Inside a transaction each attempt runs under a savepoint, so a rejected attempt leaves the
/// transaction usable. Any other error (a broken connection, a cancellation) is not about a row and propagates.
/// </para>
/// <para>
/// <b>Order.</b> Two writes of the same row in one batch go in separate rounds, in batch order, because one
/// statement cannot affect a row twice.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SetBasedPerspectiveWriteSinkTests.cs</tests>
internal static class PostgresPerspectiveWriter {
  /// <summary>The parameters one statement may bind. The protocol allows 65535.</summary>
  internal const int MAX_PARAMETERS = 65_000;

  private const string SAVEPOINT = "wb_perspective_write";

  /// <summary>Writes the batch.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="transaction">The ambient transaction, or null to let each statement commit on its own.</param>
  /// <param name="writes">The writes.</param>
  /// <param name="writeClock">The system time stamped on every row (<c>sys_created_at</c>, <c>sys_updated_at</c>).</param>
  /// <param name="cancellationToken">Cancels the write.</param>
  /// <param name="maxParameters">The parameter budget of one statement; a group that needs more is split.</param>
  /// <returns>One outcome per write, at the write's index.</returns>
  internal static async Task<IReadOnlyList<PerspectiveWriteOutcome>> WriteAsync(
      NpgsqlConnection connection,
      NpgsqlTransaction? transaction,
      IReadOnlyList<PendingPerspectiveWrite> writes,
      DateTime writeClock,
      CancellationToken cancellationToken,
      int maxParameters = MAX_PARAMETERS) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(writes);
    var outcomes = new PerspectiveWriteOutcome[writes.Count];
    var context = new WriteContext(connection, transaction, writes, writeClock, outcomes);
    foreach (var (shape, rows) in Plan(writes, outcomes, maxParameters)) {
      await _writeAsync(context, shape, rows, cancellationToken).ConfigureAwait(false);
    }
    return outcomes;
  }

  /// <summary>
  /// The statements a batch is written with, in order: its writes grouped by round and shape, each group cut to the
  /// parameter budget. A write whose physical column cannot be written into a statement is failed here instead.
  /// </summary>
  internal static List<(PerspectiveWriteShape Shape, List<int> Rows)> Plan(
      IReadOnlyList<PendingPerspectiveWrite> writes, PerspectiveWriteOutcome[] outcomes, int maxParameters) {
    var occurrences = new Dictionary<(string, Guid), int>();
    var groups = new Dictionary<(int, (string, PerspectiveWriteOperation, bool, bool, bool, bool, bool, string)), (PerspectiveWriteShape Shape, List<int> Rows)>();
    var order = new List<(int Round, int First, (int, (string, PerspectiveWriteOperation, bool, bool, bool, bool, bool, string)) Key)>();
    for (var i = 0; i < writes.Count; i++) {
      var write = writes[i];
      var shape = PerspectiveWriteShape.Of(write);
      if (_unwritable(shape) is { } error) {
        outcomes[i] = PerspectiveWriteOutcome.Failed(error);
        continue;
      }
      var row = (write.Table, write.StreamId);
      var round = occurrences.GetValueOrDefault(row);
      occurrences[row] = round + 1;
      var key = (round, shape.Key);
      if (!groups.TryGetValue(key, out var group)) {
        group = (shape, []);
        groups[key] = group;
        order.Add((round, i, key));
      }
      group.Rows.Add(i);
    }

    var statements = new List<(PerspectiveWriteShape, List<int>)>();
    foreach (var (_, _, key) in order.OrderBy(g => g.Round).ThenBy(g => g.First)) {
      var (shape, rows) = groups[key];
      var perStatement = shape.Operation == PerspectiveWriteOperation.Purge
        ? rows.Count
        : Math.Max(1, (maxParameters - PerspectiveWriteSql.GROUP_PARAMETERS) / PerspectiveWriteSql.ParametersPerRow(shape));
      for (var start = 0; start < rows.Count; start += perStatement) {
        statements.Add((shape, rows.GetRange(start, Math.Min(perStatement, rows.Count - start))));
      }
    }
    return statements;
  }

  private static ArgumentException? _unwritable(PerspectiveWriteShape shape) {
    foreach (var column in shape.PhysicalColumns) {
      if (column.StoreType is { } storeType && !PerspectiveWriteSql.IsPlainStoreType(storeType)) {
        return new ArgumentException(
          $"The store type '{storeType}' of column {column.Column} on {shape.Table} is not a plain type name, so it is not written into a statement.");
      }
    }
    return null;
  }

  private sealed record WriteContext(
      NpgsqlConnection Connection,
      NpgsqlTransaction? Transaction,
      IReadOnlyList<PendingPerspectiveWrite> Writes,
      DateTime WriteClock,
      PerspectiveWriteOutcome[] Outcomes);

  private static async Task _writeAsync(
      WriteContext context, PerspectiveWriteShape shape, List<int> rows, CancellationToken cancellationToken) {
    HashSet<Guid> written;
    try {
      written = await _attemptAsync(context, shape, rows, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is PostgresException or InvalidCastException) {
      // Inside a transaction the rejected attempt is undone to its savepoint, so the transaction stays usable.
      if (context.Transaction is { } transaction) {
        await transaction.RollbackAsync(SAVEPOINT, cancellationToken).ConfigureAwait(false);
      }
      if (rows.Count == 1) {
        context.Outcomes[rows[0]] = PerspectiveWriteOutcome.Failed(ex);
        return;
      }
      var half = rows.Count / 2;
      await _writeAsync(context, shape, rows.GetRange(0, half), cancellationToken).ConfigureAwait(false);
      await _writeAsync(context, shape, rows.GetRange(half, rows.Count - half), cancellationToken).ConfigureAwait(false);
      return;
    }

    var unwritten = new List<int>();
    foreach (var index in rows) {
      if (shape.Operation == PerspectiveWriteOperation.Purge || written.Contains(context.Writes[index].StreamId)) {
        context.Outcomes[index] = PerspectiveWriteOutcome.Written;
      } else if (shape.Operation == PerspectiveWriteOperation.Upsert) {
        // The upsert writes any row it reaches; one it did not write was refused by the ordering guard (#1342).
        context.Outcomes[index] = PerspectiveWriteOutcome.Refused;
      } else {
        unwritten.Add(index);
      }
    }
    if (unwritten.Count > 0) {
      await _explainAsync(context, shape, unwritten, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// One attempt at a statement. Inside a transaction it runs under a savepoint, released when it succeeds; a rejected
  /// attempt is rolled back to it by the caller, which isolates the row.
  /// </summary>
  private static async Task<HashSet<Guid>> _attemptAsync(
      WriteContext context, PerspectiveWriteShape shape, List<int> rows, CancellationToken cancellationToken) {
    if (context.Transaction is not { } transaction) {
      return await _executeAsync(context, shape, rows, cancellationToken).ConfigureAwait(false);
    }
    await transaction.SaveAsync(SAVEPOINT, cancellationToken).ConfigureAwait(false);
    var written = await _executeAsync(context, shape, rows, cancellationToken).ConfigureAwait(false);
    await transaction.ReleaseAsync(SAVEPOINT, cancellationToken).ConfigureAwait(false);
    return written;
  }

  private static async Task<HashSet<Guid>> _executeAsync(
      WriteContext context, PerspectiveWriteShape shape, List<int> rows, CancellationToken cancellationToken) {
    await using var command = context.Connection.CreateCommand();
    command.Transaction = context.Transaction;
    var written = new HashSet<Guid>();
    if (shape.Operation == PerspectiveWriteOperation.Purge) {
      command.CommandText = PerspectiveWriteSql.PurgeStatement(shape.Table);
      command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) {
        Value = rows.Select(i => context.Writes[i].StreamId).ToArray(),
      });
      await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      return written;
    }

    command.CommandText = PerspectiveWriteSql.RowStatement(shape, rows.Count);
    command.Parameters.Add(new NpgsqlParameter("wb_sys", context.WriteClock));
    command.Parameters.Add(new NpgsqlParameter("wb_suppressactivity", shape.SuppressActivity));
    command.Parameters.Add(new NpgsqlParameter("wb_versionbump", shape.BumpVersion ? 1 : 0));
    for (var r = 0; r < rows.Count; r++) {
      _bindRow(command, shape, r, context.Writes[rows[r]]);
    }
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      written.Add(reader.GetGuid(0));
    }
    return written;
  }

  private static void _bindRow(NpgsqlCommand command, PerspectiveWriteShape shape, int r, PendingPerspectiveWrite write) {
    // The shape of a non-purge write was taken from its row, so the row is there.
    var row = write.Row!;
    command.Parameters.Add(new NpgsqlParameter("i" + r, write.StreamId));
    command.Parameters.Add(new NpgsqlParameter("d" + r, row.Data));
    command.Parameters.Add(new NpgsqlParameter("m" + r, row.Metadata));
    command.Parameters.Add(new NpgsqlParameter("s" + r, row.Scope));
    command.Parameters.Add(new NpgsqlParameter("u" + r, row.UpdatedAt));
    if (shape.Operation == PerspectiveWriteOperation.Update) {
      command.Parameters.Add(PerspectiveRowVersionCommands.ExpectedVersionParameter("x" + r, write.ExpectedVersion));
    } else {
      command.Parameters.Add(new NpgsqlParameter("c" + r, row.CreatedAt));
    }
    if (shape.HasExpiry) {
      command.Parameters.Add(new NpgsqlParameter("e" + r, NpgsqlDbType.TimestampTz) { Value = row.ExpiresAt!.Value });
    }
    for (var p = 0; p < row.PhysicalValues.Count; p++) {
      command.Parameters.Add(PerspectiveWriteSql.PhysicalParameter("p" + r + "_" + p, row.PhysicalValues[p]));
    }
  }

  /// <summary>
  /// The rows a checked statement did not write, explained by their current version: an update whose row is still at
  /// the version the apply read was refused by the ordering guard; anything else is a conflict.
  /// </summary>
  private static async Task _explainAsync(
      WriteContext context, PerspectiveWriteShape shape, List<int> rows, CancellationToken cancellationToken) {
    var actual = new Dictionary<Guid, PerspectiveRowVersion>();
    await using (var command = context.Connection.CreateCommand()) {
      command.Transaction = context.Transaction;
      command.CommandText = PerspectiveWriteSql.VersionsStatement(shape.Table);
      command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) {
        Value = rows.Select(i => context.Writes[i].StreamId).ToArray(),
      });
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        actual[reader.GetGuid(0)] = PerspectiveRowVersion.Of(await reader.GetFieldValueAsync<uint>(1, cancellationToken).ConfigureAwait(false));
      }
    }
    foreach (var index in rows) {
      var write = context.Writes[index];
      var version = actual.GetValueOrDefault(write.StreamId, PerspectiveRowVersion.Absent);
      context.Outcomes[index] = shape.Operation == PerspectiveWriteOperation.Update && version == write.ExpectedVersion
        ? PerspectiveWriteOutcome.Refused
        : PerspectiveWriteOutcome.Conflict(version);
    }
  }
}
