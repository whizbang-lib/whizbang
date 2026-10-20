// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The schema the Dapper work coordinator qualifies its statements with.
/// </summary>
/// <remarks>
/// <para>
/// The coordinator used to write bare names and leave them to the session's <c>search_path</c>. A pooled connection
/// (PgBouncer in transaction mode, for one) does not carry the startup <c>search_path</c> to the server connection a
/// statement lands on, so those names could resolve to <c>public</c> while the framework's tables live elsewhere (#1508).
/// </para>
/// <para>
/// The schema is therefore taken from configuration: the schema the coordinator is constructed with, else the first
/// schema the connection string's <c>Search Path</c> names. With neither, the coordinator asks the server once
/// (<c>current_schema()</c>, the question the Dapper schema initializer asks), which honors a default set on the role
/// or the database, and keeps the answer.
/// </para>
/// </remarks>
/// <docs>data/drivers</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperCoordinatorSchemaTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperWorkCoordinatorNamedSchemaTests.cs</tests>
internal static class DapperCoordinatorSchema {
  /// <summary>The <c>search_path</c> entry that stands for the session user's own schema, which configures none.</summary>
  private const string SESSION_USER_ENTRY = "$user";

  /// <summary>
  /// The first schema the connection string's <c>Search Path</c> names, as the server resolves the name: an unquoted
  /// name folded to lower case, a quoted one taken literally. Null when the string names none.
  /// </summary>
  /// <param name="connectionString">The coordinator's connection string.</param>
  internal static string? FromConnectionString(string connectionString) {
    var searchPath = new NpgsqlConnectionStringBuilder(connectionString).SearchPath;
    foreach (var entry in (searchPath ?? string.Empty).Split(',')) {
      var schema = _resolve(entry.Trim());
      if (schema.Length > 0 && schema != SESSION_USER_ENTRY) {
        return schema;
      }
    }
    return null;
  }

  /// <summary>Asks the server which schema the session resolves first; <c>public</c> when it resolves none.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  internal static async Task<string> CurrentAsync(NpgsqlConnection connection, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand("SELECT current_schema()", connection);
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
      ?? Whizbang.Data.Postgres.PgIdentifier.PUBLIC_SCHEMA;
  }

  private static string _resolve(string entry) =>
    entry.Length >= 2 && entry[0] == '"' && entry[^1] == '"'
      ? entry[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
      : entry.ToLowerInvariant();
}
