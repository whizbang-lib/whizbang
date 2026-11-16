// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Whether a connection goes through a transaction-mode pooler (#2347):
/// <c>Whizbang:Postgres:&lt;connection&gt;:TransactionPooler</c>, keyed by the connection's name the same way its
/// connection string and its command timeout are. Off by default.
/// </summary>
/// <remarks>
/// Npgsql resets a pooled session with <c>DISCARD ALL</c> each time a connection returns to its pool. Behind a
/// transaction-mode pooler that reset does nothing useful (the pooler hands out a different server session per
/// transaction) and costs one statement per return, so a connection marked as behind one sets
/// <c>No Reset On Close=true</c>. Against a direct server the reset keeps one borrower's session state from reaching the
/// next, and W! cannot tell from a connection string whether a pooler sits behind the host, so the operator says so.
/// Auto-prepare is left as it is (off unless the consumer set it): prepared statements do not survive a pooler that
/// swaps the server session. Called by generated code.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#transaction-mode-poolers</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresTransactionPoolerTests.cs</tests>
public static class PostgresTransactionPooler {
  private const string SECTION = "Whizbang:Postgres";
  private const string KEY = "TransactionPooler";

  /// <summary>Whether <paramref name="connectionName"/> is marked as behind a transaction-mode pooler.</summary>
  /// <param name="configuration">The configuration, or null.</param>
  /// <param name="connectionName">The connection's name.</param>
  /// <returns>True only when the key is set to true.</returns>
  public static bool IsConfigured(IConfiguration? configuration, string connectionName) =>
    bool.TryParse(configuration?.GetSection(SECTION).GetSection(connectionName)[KEY], out var marked) && marked;

  /// <summary>Stops the session reset on close when the connection is marked as behind a pooler.</summary>
  /// <param name="configuration">The configuration, or null.</param>
  /// <param name="connectionName">The connection's name.</param>
  /// <param name="builder">The connection string being built.</param>
  public static void Apply(IConfiguration? configuration, string connectionName, NpgsqlConnectionStringBuilder builder) {
    ArgumentNullException.ThrowIfNull(builder);
    if (IsConfigured(configuration, connectionName)) {
      builder.NoResetOnClose = true;
    }
  }
}
