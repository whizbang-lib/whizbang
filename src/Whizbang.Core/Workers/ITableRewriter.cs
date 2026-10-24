// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// One way of rewriting a bloated framework table to give back the space it holds but cannot use.
/// The <c>Rewrite</c> startup step offers each table the bloat detector recorded to the rewriter whose
/// <see cref="Strategy"/> is selected, and clears the table's request only when the ratio the rewriter
/// returns shows the rewrite worked.
/// </summary>
/// <remarks>
/// <para>
/// The framework registers <see cref="VacuumFullTableRewriter"/>, the blocking <c>VACUUM FULL</c> that
/// takes an ACCESS EXCLUSIVE lock for the table's duration. Online strategies plug in here as further
/// implementations, each under its own strategy name.
/// </para>
/// <para>
/// A rewriter that reports <see cref="CanRun"/> false is not called, and the step reports
/// <see cref="CannotRunReason"/> as why it skipped.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#table-rewrites-run-post-ready-under-the-maintainer-duty</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/VacuumFullTableRewriterTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Startup/TableRewriteStartupStepTests.cs</tests>
public interface ITableRewriter {
  /// <summary>The strategy name this rewriter is selected by, for example <see cref="TableRewriteStrategies.VACUUM_FULL"/>.</summary>
  string Strategy { get; }

  /// <summary>Whether this rewriter may run on this host now.</summary>
  bool CanRun { get; }

  /// <summary>Why the rewriter cannot run, reported when <see cref="CanRun"/> is false.</summary>
  string CannotRunReason { get; }

  /// <summary>
  /// Rewrites <paramref name="tableName"/> and returns its bloat ratio measured afterwards, or null when
  /// the table was not rewritten (for example, the connection's role does not own it).
  /// </summary>
  /// <param name="coordinator">The work coordinator of the scope the step runs in.</param>
  /// <param name="tableName">A framework table offered by <see cref="IWorkCoordinator.GetTablesNeedingRewriteAsync"/>.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The re-measured bloat ratio, or null when nothing was rewritten.</returns>
  Task<double?> RewriteAsync(IWorkCoordinator coordinator, string tableName, CancellationToken cancellationToken);
}
