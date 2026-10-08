// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// How long any one schema statement may take. Schema initialization runs on the <c>-init</c> connection
/// string when a deployment provides one (a direct connection that bypasses PgBouncer for DDL), so that
/// string's own <c>Command Timeout</c> is the operator's lever over DDL. When it does not set one, the
/// default stands.
/// </summary>
/// <remarks>
/// A rewrite of a stored format is one statement over every row of a table, which legitimately takes far
/// longer than an ordinary command, and a schema pass that gives up half way leaves the service unable to
/// start. Before this, DDL used a fixed ten minutes and the <c>-init</c> string's <c>Command Timeout</c> was
/// ignored, so a migration that needed longer could not be given longer.
/// </remarks>
/// <docs>data/turnkey-initialization</docs>
/// <tests>tests/Whizbang.Data.Postgres.Tests/SchemaCommandBudgetTests.cs</tests>
public static class SchemaCommandBudget {
  /// <summary>
  /// Ten minutes. Used when the initialization connection string does not set a <c>Command Timeout</c> of
  /// its own. Both the EF path and the boundary path need this to be the same number.
  /// </summary>
  public const int DEFAULT_SECONDS = 600;

  /// <summary>
  /// The budget a single schema statement gets: the initialization connection string's
  /// <c>Command Timeout</c> when it sets one, otherwise <see cref="DEFAULT_SECONDS"/>.
  /// </summary>
  /// <param name="initConnectionString">
  /// The <c>-init</c> connection string schema initialization runs on, or <see langword="null"/> when the
  /// deployment provides none.
  /// </param>
  /// <returns>A positive number of seconds. Never throws: a connection string this cannot parse falls back
  /// to the default, because failing here would fail startup over a timeout.</returns>
  public static int ForInitConnection(string? initConnectionString) {
    if (string.IsNullOrWhiteSpace(initConnectionString)) {
      return DEFAULT_SECONDS;
    }

    try {
      // A plain DbConnectionStringBuilder holds only the keywords the string actually set.
      // NpgsqlConnectionStringBuilder.ContainsKey cannot be used for this: it pre-populates every keyword
      // it knows and answers true for all of them, so an absent Command Timeout would read as its default
      // of 30 s and quietly shorten every DDL statement from ten minutes to thirty seconds.
      var present = new DbConnectionStringBuilder { ConnectionString = initConnectionString };
      foreach (string key in present.Keys) {
        if (!_isCommandTimeout(key)) {
          continue;
        }
        // Parsed through Npgsql's builder so aliases and validation behave exactly as they do when the
        // connection is opened. Zero means "no timeout" to Npgsql, and DDL that can hang forever is what
        // the ceiling exists to prevent, so zero reads as "not meant to shorten this" and the default stands.
        var timeout = new NpgsqlConnectionStringBuilder(initConnectionString).CommandTimeout;
        return timeout > 0 ? timeout : DEFAULT_SECONDS;
      }
      return DEFAULT_SECONDS;
    } catch (ArgumentException) {
      return DEFAULT_SECONDS;
    }
  }

  // "Command Timeout", "CommandTimeout", "command_timeout" are the same keyword to Npgsql.
  private static bool _isCommandTimeout(string key) =>
    string.Equals(
      key.Replace(" ", string.Empty, StringComparison.Ordinal)
         .Replace("_", string.Empty, StringComparison.Ordinal),
      "commandtimeout",
      StringComparison.OrdinalIgnoreCase);
}
