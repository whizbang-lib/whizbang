// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>
/// Loads new perspective rows into a table with <c>COPY … FROM STDIN (FORMAT BINARY)</c>: the write sink of a blue-green
/// rebuild's staging load (#2248), shared by both PostgreSQL drivers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same row as the insert.</b> Each row is copied into exactly the columns the set-based and per-stream inserts write
/// (<see cref="PerspectiveWriteSql"/>), with the same values: the documents as <c>jsonb</c>, the business times, the
/// system clock in both system-time columns, version 1, the expiry when the rows carry one, and the physical columns.
/// Each value is sent as the type of the column it lands in, read from the catalog, because a binary copy casts nothing.
/// </para>
/// <para>
/// <b>Fallback.</b> A copy is all or nothing. A group the copy cannot take (a value of a type the column does not take, a
/// row a constraint refuses, a column the table does not have) is undone (to a savepoint, inside a transaction) and
/// written with the set-based statements instead (<see cref="PostgresPerspectiveWriter"/>), which isolate the row the
/// database rejects and write every other one. An update or a purge is never copied: it goes to those statements too.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild#staging-load</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SetBasedPerspectiveWriteSinkTests.Load.cs</tests>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
  Justification = "Only the pending writes' quoted table and quoted column identifiers are interpolated; they come from the perspective's model, never from input.")]
[SuppressMessage("csharpsquid", "S2077:Formatting SQL queries is security-sensitive",
  Justification = "Only the pending writes' quoted table and quoted column identifiers are interpolated; they come from the perspective's model, never from input.")]
internal static class PostgresPerspectiveCopyLoader {
  private const string SAVEPOINT = "wb_perspective_copy";

  private static readonly string[] _fixedColumns =
    ["id", "data", "metadata", "scope", "created_at", "updated_at", "sys_created_at", "sys_updated_at", "version"];

  /// <summary>Loads the writes.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="transaction">The ambient transaction, or null to let each copy commit on its own.</param>
  /// <param name="writes">The writes: inserts and upserts of rows the table does not hold are copied.</param>
  /// <param name="writeClock">The system time stamped on every row.</param>
  /// <param name="cancellationToken">Cancels the load.</param>
  /// <returns>One outcome per write, at the write's index.</returns>
  internal static async Task<IReadOnlyList<PerspectiveWriteOutcome>> LoadAsync(
      NpgsqlConnection connection,
      NpgsqlTransaction? transaction,
      IReadOnlyList<PendingPerspectiveWrite> writes,
      DateTime writeClock,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(writes);
    var outcomes = new PerspectiveWriteOutcome[writes.Count];
    var groups = new Dictionary<(string, bool, string), (PerspectiveWriteShape Shape, List<int> Rows)>();
    var statements = new List<int>();
    for (var i = 0; i < writes.Count; i++) {
      if (writes[i].Operation is not (PerspectiveWriteOperation.Insert or PerspectiveWriteOperation.Upsert)) {
        statements.Add(i);
        continue;
      }
      var shape = PerspectiveWriteShape.Of(writes[i]);
      var key = (shape.Table, shape.HasExpiry, shape.Signature);
      if (!groups.TryGetValue(key, out var group)) {
        group = (shape, []);
        groups[key] = group;
      }
      group.Rows.Add(i);
    }

    foreach (var (shape, rows) in groups.Values) {
      if (await _tryCopyAsync(connection, transaction, shape, [.. rows.Select(i => writes[i])], writeClock, cancellationToken).ConfigureAwait(false)) {
        foreach (var row in rows) {
          outcomes[row] = PerspectiveWriteOutcome.Written;
        }
      } else {
        statements.AddRange(rows);
      }
    }

    if (statements.Count > 0) {
      statements.Sort();
      var written = await PostgresPerspectiveWriter.WriteAsync(
        connection, transaction, [.. statements.Select(i => writes[i])], writeClock, cancellationToken).ConfigureAwait(false);
      for (var i = 0; i < statements.Count; i++) {
        outcomes[statements[i]] = written[i];
      }
    }
    return outcomes;
  }

  /// <summary>Copies one group of rows that share a table and columns; false, with nothing written, when the copy failed.</summary>
  private static async Task<bool> _tryCopyAsync(
      NpgsqlConnection connection, NpgsqlTransaction? transaction, PerspectiveWriteShape shape,
      List<PendingPerspectiveWrite> rows, DateTime writeClock, CancellationToken cancellationToken) {
    List<string> columns = [.. _fixedColumns];
    if (shape.HasExpiry) {
      columns.Add("expires_at");
    }
    columns.AddRange(shape.PhysicalColumns.Select(c => c.Column));
    var types = await _columnTypesAsync(connection, transaction, shape.Table, cancellationToken).ConfigureAwait(false);
    if (columns.Any(c => !types.ContainsKey(c))) {
      return false;
    }

    if (transaction is not null) {
      await transaction.SaveAsync(SAVEPOINT, cancellationToken).ConfigureAwait(false);
    }
    try {
      var copy = "COPY " + shape.Table + " (" + string.Join(", ", columns.Select(PerspectiveWriteSql.QuoteColumn)) + ") FROM STDIN (FORMAT BINARY)";
      await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken).ConfigureAwait(false);
      foreach (var write in rows) {
        var row = write.Row!;
        await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(write.StreamId, types["id"], cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Data, types["data"], cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Metadata, types["metadata"], cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Scope, types["scope"], cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.CreatedAt, types["created_at"], cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.UpdatedAt, types["updated_at"], cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(writeClock, types["sys_created_at"], cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(writeClock, types["sys_updated_at"], cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(1, types["version"], cancellationToken).ConfigureAwait(false);
        if (shape.HasExpiry) {
          await importer.WriteAsync(row.ExpiresAt!.Value, types["expires_at"], cancellationToken).ConfigureAwait(false);
        }
        foreach (var value in row.PhysicalValues) {
          await _writeValueAsync(importer, value, types[value.Column], cancellationToken).ConfigureAwait(false);
        }
      }
      await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
      return true;
    } catch (Exception ex) when (ex is NpgsqlException or InvalidCastException or NotSupportedException or ArgumentException or InvalidOperationException) {
      // The copy wrote nothing. Inside a transaction it is undone to its savepoint, so the statements that follow can
      // write the group and say which row the database refused.
      if (transaction is not null) {
        await transaction.RollbackAsync(SAVEPOINT, cancellationToken).ConfigureAwait(false);
      }
      return false;
    }
  }

  private static Task _writeValueAsync(
      NpgsqlBinaryImporter importer, PerspectivePhysicalValue value, string type, CancellationToken cancellationToken) =>
    value.Value is null
      ? importer.WriteNullAsync(cancellationToken)
      : importer.WriteAsync(value.Value, type, cancellationToken);

  /// <summary>Every column of the table and the type a binary copy must send its value as.</summary>
  private static async Task<Dictionary<string, string>> _columnTypesAsync(
      NpgsqlConnection connection, NpgsqlTransaction? transaction, string table, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      "SELECT a.attname, format_type(a.atttypid, NULL) FROM pg_attribute a " +
      "WHERE a.attrelid = @table::regclass AND a.attnum > 0 AND NOT a.attisdropped", connection, transaction);
    command.Parameters.AddWithValue(nameof(table), table);
    var types = new Dictionary<string, string>(StringComparer.Ordinal);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      types[reader.GetString(0)] = reader.GetString(1);
    }
    return types;
  }
}
