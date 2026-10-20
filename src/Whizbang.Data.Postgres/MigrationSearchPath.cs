// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Points a migration's <c>search_path</c> at the schema it migrates, for the length of that migration
/// only, so a shipped migration that asks <c>current_schema()</c> asks about the schema its DDL alters.
/// </summary>
/// <remarks>
/// <para>
/// A shipped migration is never edited, and several shipped guards compare
/// <c>table_schema = current_schema()</c>. On the EF Core driver's connection the default
/// <c>search_path</c> makes that <c>public</c>, whatever schema the context owns, so on a re-run (an
/// upgrade re-runs every file whose hash changed) such a guard reads "not there yet" and its DDL fails
/// or a repair is skipped. Setting the path for the migration makes the shipped text correct as it
/// stands. The Dapper driver needs none of this: it takes its schema from <c>current_schema()</c>.
/// </para>
/// <para>
/// The setting is transaction-local (<c>set_config(..., true)</c>) and restored when the migration
/// finishes, because the runner applies every migration and then the perspective DDL in one
/// transaction, and nothing after a migration should resolve names differently because of it. A
/// migration that fails aborts the transaction, which discards the setting with everything else.
/// </para>
/// <para>
/// This makes the migrations' own guards correct; it is not what makes run-time functions correct.
/// Those run later on pooled connections that never pass through here, and name their schema
/// explicitly (migrations README, rule 14).
/// </para>
/// </remarks>
/// <docs>contributors/data-engines/writing-migrations</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/MigrationSearchPathTests.cs</tests>
public static class MigrationSearchPath {
  /// <summary>
  /// The <c>search_path</c> a migration of <paramref name="schema"/> runs under, or <see langword="null"/>
  /// when the schema is <c>public</c> (or not named), whose default path already resolves to it.
  /// </summary>
  /// <param name="schema">The schema, bare (<c>svc</c>) or quoted (<c>"svc"</c>).</param>
  /// <returns>The schema as a quoted identifier, then <c>public</c>; for example <c>"svc", public</c>.</returns>
  public static string? For(string? schema) {
    var bare = (schema ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal);
    if (bare.Length == 0 || bare == "public") {
      return null;
    }

    return $"\"{bare}\", public";
  }

  /// <summary>
  /// Sets the transaction's <c>search_path</c> for a migration of <paramref name="schema"/>.
  /// </summary>
  /// <param name="connection">An open connection inside the transaction the migration runs in.</param>
  /// <param name="schema">The schema the migration alters.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// The path in force before, to hand to <see cref="ExitAsync"/>; <see langword="null"/> when
  /// nothing was changed because the schema is <c>public</c>.
  /// </returns>
  public static async Task<string?> EnterAsync(
      NpgsqlConnection connection, string? schema, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    var path = For(schema);
    if (path is null) {
      return null;
    }

    await using var command = new NpgsqlCommand(
      "SELECT current_setting('search_path'), set_config('search_path', $1, true)", connection);
    command.Parameters.AddWithValue(path);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return reader.GetString(0);
  }

  /// <summary>Restores the <c>search_path</c> that <see cref="EnterAsync"/> replaced.</summary>
  /// <param name="connection">The connection passed to <see cref="EnterAsync"/>.</param>
  /// <param name="previous">What <see cref="EnterAsync"/> returned; <see langword="null"/> restores nothing.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public static async Task ExitAsync(
      NpgsqlConnection connection, string? previous, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    if (previous is null) {
      return;
    }

    await using var command = new NpgsqlCommand("SELECT set_config('search_path', $1, true)", connection);
    command.Parameters.AddWithValue(previous);
    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }
}
