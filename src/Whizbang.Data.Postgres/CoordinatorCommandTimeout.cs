// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The command budget a work coordinator owns for its own SQL, shared by the EF Core and Dapper
/// coordinators so the two cannot drift. Applied to every command a coordinator creates, so a consumer's
/// connection-string <c>Command Timeout</c> can never cancel a commit batch: a cancelled commit loses its
/// completions, the rows re-claim as lease expiries, and the poison gate throttles the drain to one row
/// per cycle.
/// </summary>
/// <remarks>
/// A timeout belongs to the connection it applies to. Application queries keep the pooled connection
/// string's <c>Command Timeout</c>; the coordinator's own SQL does not, because the consequence of
/// cancelling it is not a failed query but a stalled drain. The budget is therefore set per command
/// rather than on the connection: a coordinator call may run on a pinned connection the application
/// also uses, and rewriting that connection's timeout would change the application's queries too.
/// </remarks>
/// <docs>fundamentals/work-coordinator/configuration-reference#command-timeout</docs>
/// <tests>tests/Whizbang.Data.Postgres.Tests/CoordinatorCommandTimeoutTests.cs</tests>
public static class CoordinatorCommandTimeout {
  /// <summary>
  /// Three minutes. The longest a coordinator batch (handler results, composite fan-outs) has been observed
  /// to run legitimately under a bulk-import backlog is ~30 s; this leaves headroom while still letting a
  /// genuinely hung command fail rather than wait forever. The EF context uses the same value.
  /// </summary>
  public const int SECONDS = 180;

  /// <summary>
  /// Applies the budget to a freshly created command, overriding whatever the connection string carried.
  /// Returns the same command so it composes at the creation site.
  /// </summary>
  /// <typeparam name="TCommand">The command type, preserved so the call composes.</typeparam>
  /// <param name="command">The command to give the budget to.</param>
  /// <returns>The same command.</returns>
  public static TCommand Apply<TCommand>(TCommand command) where TCommand : DbCommand {
    ArgumentNullException.ThrowIfNull(command);
    command.CommandTimeout = SECONDS;
    return command;
  }
}
