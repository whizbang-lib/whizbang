// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Npgsql;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The one place the framework's raw database commands are created, so each is attributed to the mechanism that ran
/// it: the command text leads with <c>/* whizbang:&lt;mechanism&gt; */</c>, and the statement is counted, timed and
/// its rows recorded on <see cref="DbStatementMetrics"/>.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism is <see cref="DbMechanismScope.Current"/> when the command text is set. With no scope open the text is
/// left as written and the statement is recorded as <see cref="DbMechanism.Untagged"/>, so an attribution gap shows.
/// </para>
/// <para>
/// A comment, rather than a session setting or <c>application_name</c>: it costs no extra statement, it travels with
/// the statement through a transaction-mode pooler, <c>pg_stat_statements</c> ignores it when it computes a query id
/// (so it adds no statistics entries), and with a closed set of mechanisms the driver prepares at most one text per
/// statement per mechanism. <c>application_name</c> also has a job already: the liveness connection's name is what
/// joins an instance to its session, which is why that connection never uses this factory.
/// </para>
/// </remarks>
/// <docs>operations/observability/database-attribution</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Observability/WhizbangCommandAttributionTests.cs</tests>
public static class WhizbangCommand {
  /// <summary>Creates an attributed command on <paramref name="connection"/>.</summary>
  /// <param name="connection">The connection the command runs on.</param>
  /// <returns>The command.</returns>
  public static NpgsqlCommand CreateWhizbangCommand(this NpgsqlConnection connection) {
    ArgumentNullException.ThrowIfNull(connection);
    return new AttributedNpgsqlCommand(connection);
  }

  /// <summary>Creates an attributed command on a connection held as its base type, always an Npgsql one.</summary>
  /// <param name="connection">The connection the command runs on.</param>
  /// <returns>The command.</returns>
  public static DbCommand CreateWhizbangCommand(this DbConnection connection) =>
    ((NpgsqlConnection)connection).CreateWhizbangCommand();

  /// <summary>
  /// Creates an attributed command on <paramref name="connection"/> with the coordinator's command budget
  /// (<see cref="CoordinatorCommandTimeout"/>).
  /// </summary>
  /// <param name="connection">The connection the command runs on.</param>
  /// <returns>The command.</returns>
  public static NpgsqlCommand CreateCoordinatorCommand(this NpgsqlConnection connection) =>
    CoordinatorCommandTimeout.Apply(connection.CreateWhizbangCommand());

  /// <summary>
  /// Prepends <paramref name="mechanism"/>'s comment to <paramref name="sql"/>, unless the text already leads with a
  /// framework comment.
  /// </summary>
  /// <param name="sql">The statement text.</param>
  /// <param name="mechanism">The mechanism to attribute it to.</param>
  /// <returns>The attributed text.</returns>
  internal static string Annotate(string sql, DbMechanism mechanism) =>
    sql.StartsWith(COMMENT_LEAD, StringComparison.Ordinal) ? sql : mechanism.SqlComment + sql;

  /// <summary>How every framework comment begins.</summary>
  internal const string COMMENT_LEAD = "/* whizbang:";

  /// <summary>
  /// The attributed command. The text is annotated as it is set (the setter is virtual, so every caller reaches it
  /// whatever type it holds the command as), and the statement is recorded when the command is disposed, which is
  /// the one point every execution path (reader, scalar, non-query) passes: the rows are the driver's per-statement
  /// row counts by then, and the duration runs from the text being set.
  /// </summary>
  private sealed class AttributedNpgsqlCommand(NpgsqlConnection connection) : NpgsqlCommand(null, connection) {
    private DbMechanism _mechanism = DbMechanism.Untagged;
    private long _startedAt;
    private bool _pending;

    [AllowNull]
    public override string CommandText {
      get => base.CommandText;
      set {
        // A command given new text after it ran is a second statement: the first is recorded first.
        _recordIfExecuted();
        var mechanism = DbMechanismScope.Current;
        _mechanism = mechanism ?? DbMechanism.Untagged;
        base.CommandText = mechanism is null || value is null ? value : Annotate(value, mechanism);
        _startedAt = TimeProvider.System.GetTimestamp();
        _pending = true;
      }
    }

    protected override void Dispose(bool disposing) {
      _recordIfExecuted();
      base.Dispose(disposing);
    }

    private void _recordIfExecuted() {
      // The driver fills Statements when it executes the text; a command never executed has none and records nothing.
      // Pending makes each text count once, however often the command is disposed.
#pragma warning disable CS0618 // Statements carries the per-statement row counts this records; the batch API is not what the caller used
      var statements = Statements;
#pragma warning restore CS0618
      if (!_pending || statements.Count == 0) {
        return;
      }
      _pending = false;
      long rows = 0;
      foreach (var statement in statements) {
        rows += (long)statement.Rows;
      }
      DbStatementMetrics.Record(_mechanism, TimeProvider.System.GetElapsedTime(_startedAt), rows);
    }
  }
}
