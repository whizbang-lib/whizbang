// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// An index a stored-form migration drops because it casts a converted key to the old type, and the schema's
/// statement that builds it for the new type.
/// </summary>
/// <param name="Schema">The schema, unquoted.</param>
/// <param name="Table">The perspective table, unquoted.</param>
/// <param name="Name">The index's name, as the schema derives it.</param>
/// <param name="CreateStatement">The schema's <c>CREATE [UNIQUE] INDEX IF NOT EXISTS</c> statement for it.</param>
/// <docs>fundamentals/perspectives/stored-form-migrations#indexes</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StoredFormIndexRebuildTests.cs</tests>
public sealed partial record StoredFormIndexRebuild(string Schema, string Table, string Name, string CreateStatement) {
  /// <summary>
  /// Builds, concurrently, every index the migrations replace that is not there: after the stored-format rewrite
  /// phase has committed, so the build reads converted values, and outside any transaction, so writes to the table
  /// go on while it builds. An index already there and valid is left alone, so on a start with nothing to rebuild
  /// this is one catalog lookup per declared index.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A concurrent build that fails leaves an invalid index under the name, which every later
  /// <c>CREATE INDEX IF NOT EXISTS</c> would take for the index. So an invalid index under the name is dropped before
  /// the build and after a failed one, and a failure is a warning, never fatal: the schema pass that follows builds the
  /// index in its own transaction, which blocks writes to the table while it builds but always gets there.
  /// </para>
  /// <para>
  /// An index whose table does not exist yet is skipped; the schema pass creates both.
  /// </para>
  /// </remarks>
  /// <param name="connectionFactory">Produces the connection the builds run on, outside any transaction.</param>
  /// <param name="migrations">The migrations the build declares.</param>
  /// <param name="commandTimeoutSeconds">
  /// The timeout for one build, which waits for every transaction older than it before it reads the table.
  /// </param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many indexes were built.</returns>
  public static async Task<int> ApplyAsync(
      Func<NpgsqlConnection> connectionFactory,
      IReadOnlyList<StoredFormMigration> migrations,
      int commandTimeoutSeconds,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(migrations);
    var log = logger ?? NullLogger.Instance;

    var rebuilds = migrations.SelectMany(m => m.IndexRebuilds).DistinctBy(r => (r.Schema, r.Name)).ToList();
    if (rebuilds.Count == 0) {
      return 0;
    }

    await using var connection = connectionFactory();
    await connection.OpenAsync(cancellationToken);
    var built = 0;
    foreach (var rebuild in rebuilds) {
      var (state, invalid) = await _stateAsync(connection, rebuild, cancellationToken);
      if (state is IndexState.Valid or IndexState.TableAbsent) {
        continue;
      }
      try {
        if (invalid is not null) {
          await _executeAsync(connection, $"DROP INDEX CONCURRENTLY IF EXISTS {invalid}", commandTimeoutSeconds, cancellationToken);
        }
        await _executeAsync(connection, Concurrently(rebuild.CreateStatement)!, commandTimeoutSeconds, cancellationToken);
        built++;
        StoredFormIndexRebuildLog.Built(log, rebuild.Name, rebuild.Schema, rebuild.Table);
      } catch (NpgsqlException ex) {
        // A unique index over duplicate values, a lock or an older transaction that outlasted the timeout: the build
        // left an invalid index, which would pass for the index on every later start.
        StoredFormIndexRebuildLog.Failed(log, ex, rebuild.Name, rebuild.Schema, rebuild.Table);
        if ((await _stateAsync(connection, rebuild, cancellationToken)).Invalid is { } leftover) {
          await _executeAsync(connection, $"DROP INDEX CONCURRENTLY IF EXISTS {leftover}", commandTimeoutSeconds, cancellationToken);
        }
      }
    }
    return built;
  }

  /// <summary>
  /// <paramref name="createStatement"/> with <c>CONCURRENTLY</c> after <c>INDEX</c>, or <see langword="null"/> when it
  /// is not a <c>CREATE [UNIQUE] INDEX IF NOT EXISTS</c> statement.
  /// </summary>
  /// <param name="createStatement">The schema's statement.</param>
  public static string? Concurrently(string createStatement) {
    ArgumentNullException.ThrowIfNull(createStatement);
    var match = _createIndex().Match(createStatement);
    return match.Success ? createStatement.Insert(match.Groups["at"].Index, "CONCURRENTLY ") : null;
  }

  [GeneratedRegex(@"^\s*CREATE\s+(UNIQUE\s+)?INDEX\s+(?<at>)IF\s+NOT\s+EXISTS\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
  private static partial Regex _createIndex();

  private enum IndexState { Absent, Valid, Invalid, TableAbsent }

  // Where the index stands, and for an invalid one its name as the catalog has it, schema-qualified and quoted.
  private static async Task<(IndexState State, string? Invalid)> _stateAsync(
      NpgsqlConnection connection, StoredFormIndexRebuild rebuild, CancellationToken ct) {
    await using var command = new NpgsqlCommand("""
      SELECT CASE
        WHEN to_regclass(format('%I.%I', $1, $2)) IS NULL THEN 'table-absent'
        ELSE (SELECT CASE WHEN i.indisvalid THEN 'valid' ELSE 'invalid:' || c.oid::regclass::text END
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
              JOIN pg_index i ON i.indexrelid = c.oid
              WHERE n.nspname = $1 AND c.relname = left($3, 63))
      END
      """, connection);
    command.Parameters.AddWithValue(rebuild.Schema);
    command.Parameters.AddWithValue(rebuild.Table);
    command.Parameters.AddWithValue(rebuild.Name);
    return await command.ExecuteScalarAsync(ct) switch {
      "table-absent" => (IndexState.TableAbsent, null),
      "valid" => (IndexState.Valid, null),
      string invalid => (IndexState.Invalid, invalid["invalid:".Length..]),
      _ => (IndexState.Absent, null),
    };
  }

  private static async Task _executeAsync(NpgsqlConnection connection, string sql, int timeoutSeconds, CancellationToken ct) {
    await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
    await command.ExecuteNonQueryAsync(ct);
  }
}

/// <summary>Source-generated logging for the stored-form index rebuild.</summary>
internal static partial class StoredFormIndexRebuildLog {
  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Stored-form index rebuild: built {Index} on {Schema}.{Table} concurrently, for the type its key now holds")]
  public static partial void Built(ILogger logger, string index, string schema, string table);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Stored-form index rebuild: could not build {Index} on {Schema}.{Table} concurrently; the schema pass builds it "
              + "in its own transaction instead")]
  public static partial void Failed(ILogger logger, Exception exception, string index, string schema, string table);
}
