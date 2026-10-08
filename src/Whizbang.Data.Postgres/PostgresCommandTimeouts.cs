// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Each connection's command timeout, keyed by the connection's name the same way its connection
/// string is: <c>Whizbang:Postgres:&lt;connection&gt;:CommandTimeoutSeconds</c>, where the connection is
/// <c>db</c>, <c>db-direct</c> or <c>db-init</c> (or a named database's <c>&lt;name&gt;</c>,
/// <c>&lt;name&gt;-direct</c>, <c>&lt;name&gt;-init</c>).
/// </summary>
/// <remarks>
/// The key wins over a <c>Command Timeout</c> written in the connection string. Applied where each
/// connection is built: the pooled data source (generated registration), the notification connection,
/// the pinned worker pool, and schema initialization. Called by generated code.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#command-timeouts</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresCommandTimeoutsTests.cs</tests>
public static class PostgresCommandTimeouts {
  private const string SECTION = "Whizbang:Postgres";
  private const string KEY = "CommandTimeoutSeconds";

  /// <summary>
  /// The timeout configured for <paramref name="connectionName"/>, in seconds (0 means no limit), or
  /// null when it is not set or does not parse.
  /// </summary>
  public static int? Configured(IConfiguration? configuration, string connectionName) {
    var value = configuration?.GetSection(SECTION).GetSection(connectionName)[KEY];
    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
      ? seconds
      : null;
  }

  /// <summary>Sets the builder's <c>Command Timeout</c> when <paramref name="connectionName"/> has one configured.</summary>
  public static void Apply(IConfiguration? configuration, string connectionName, NpgsqlConnectionStringBuilder builder) {
    ArgumentNullException.ThrowIfNull(builder);
    if (Configured(configuration, connectionName) is { } seconds) {
      builder.CommandTimeout = seconds;
    }
  }

  /// <summary>
  /// <paramref name="connectionString"/> with <paramref name="connectionName"/>'s configured timeout
  /// written into it, or unchanged when none is configured.
  /// </summary>
  public static string ApplyTo(IConfiguration? configuration, string connectionName, string connectionString) {
    if (Configured(configuration, connectionName) is not { } seconds) {
      return connectionString;
    }

    return new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = seconds }.ConnectionString;
  }
}
