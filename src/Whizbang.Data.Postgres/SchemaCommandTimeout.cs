// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Globalization;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The command timeout schema initialization uses: the initialization connection string's own
/// <c>Command Timeout</c> when it sets one, otherwise <see cref="DEFAULT_SECONDS"/>.
/// </summary>
/// <remarks>
/// The initialization string (<c>ConnectionStrings:&lt;name&gt;-init</c>) exists only for schema work,
/// so a timeout written into it can only mean that. Without one, schema work keeps its own ten
/// minutes and never inherits the application's query timeout: a table rewrite legitimately runs for
/// minutes, and a schema pass that gives up half way leaves the service unable to start. Called by
/// the generated schema initializer.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#command-timeouts</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaCommandTimeoutTests.cs</tests>
public static class SchemaCommandTimeout {
#pragma warning disable CA1707 // project convention: public consts use UPPER_CASE with underscores
  /// <summary>Ten minutes: the schema budget when the initialization string sets none.</summary>
  public const int DEFAULT_SECONDS = 600;
#pragma warning restore CA1707

  private static readonly string[] _keywords = ["Command Timeout", "CommandTimeout"];

  /// <summary>
  /// The timeout for schema commands, in seconds; 0 (set explicitly) means no limit.
  /// </summary>
  /// <param name="initConnectionString">The initialization connection string, or null when none is configured.</param>
  public static int Resolve(string? initConnectionString) {
    if (string.IsNullOrWhiteSpace(initConnectionString)) {
      return DEFAULT_SECONDS;
    }

    // DbConnectionStringBuilder holds only the keys the string actually sets, unlike the Npgsql
    // builder, which reports a default for every keyword.
    var builder = new DbConnectionStringBuilder();
    try {
      builder.ConnectionString = initConnectionString;
    } catch (ArgumentException) {
      return DEFAULT_SECONDS;
    }

    foreach (var keyword in _keywords) {
      if (builder.TryGetValue(keyword, out var value)
          && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
          && seconds >= 0) {
        return seconds;
      }
    }

    return DEFAULT_SECONDS;
  }
}
