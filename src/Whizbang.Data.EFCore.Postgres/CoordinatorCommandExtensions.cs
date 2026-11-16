// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The EF Core coordinator's view of the shared command budget
/// (<see cref="Whizbang.Data.Postgres.CoordinatorCommandTimeout"/>), kept so this package's call sites read
/// the same as before. The budget itself lives beside SchemaCommandTimeout in Whizbang.Data.Postgres,
/// shared with the Dapper coordinator so the two cannot drift. Applied to every raw command the coordinator
/// creates and to its EF context alike, so a consumer's connection-string <c>Command Timeout</c> can never
/// cancel a commit batch: a cancelled commit loses its completions, the rows re-claim as lease expiries,
/// and the poison gate throttles the drain to one row per cycle.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference#command-timeout</docs>
internal static class CoordinatorCommandExtensions {
  /// <summary>
  /// Three minutes. The longest a coordinator batch (handler results, composite fan-outs) has been observed
  /// to run legitimately under a bulk-import backlog is ~30 s; this leaves headroom while still letting a
  /// genuinely hung command fail rather than wait forever. The EF context uses the same value.
  /// </summary>
  internal const int COORDINATOR_COMMAND_TIMEOUT_SECONDS =
    Whizbang.Data.Postgres.CoordinatorCommandTimeout.SECONDS;

  /// <summary>
  /// Creates a coordinator command on <paramref name="connection"/>: attributed to the mechanism that runs it
  /// (<see cref="Whizbang.Data.Postgres.WhizbangCommand"/>) and given the coordinator's command budget, overriding
  /// whatever the connection string carried.
  /// </summary>
  /// <param name="connection">The coordinator's connection, always an Npgsql one.</param>
  /// <returns>The command.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorCommandTimeoutSqlTests.cs</tests>
  internal static Npgsql.NpgsqlCommand CreateCoordinatorCommand(this DbConnection connection) =>
    Whizbang.Data.Postgres.WhizbangCommand.CreateCoordinatorCommand((Npgsql.NpgsqlConnection)connection);
}
