// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// The <see cref="TableRewriteStrategies.PG_SQUEEZE"/> strategy: rewrites a table online through pg_squeeze, which
/// copies it while it stays readable and writable and takes an exclusive lock only for the final swap, then re-measures
/// it.
/// </summary>
/// <remarks>
/// <para>
/// It is run by <see cref="OnlineTableRewriteStep"/> from the maintenance cycle, largest bloat first within
/// <see cref="MaintenanceWorkerOptions.OnlineRewriteMaxBytesPerCycle"/>, never by the boot-time <c>Rewrite</c> step.
/// Whether pg_squeeze is usable is the capability verdict's question (start fails without it when
/// <see cref="TableRewriteStrategy.PgSqueeze"/> is chosen) and the step's, which runs it only when pg_squeeze is Ready,
/// so <see cref="CanRun"/> is always true.
/// </para>
/// <para>
/// The rewrite itself is <see cref="IWorkCoordinator.SqueezeTableAsync"/>, which validates the table name, checks the
/// prerequisites pg_squeeze has (logical WAL, a free replication slot, an identity index, the role's rights) and
/// reports an unmet one instead of calling it.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#online-rewrite</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OnlineTableRewriteStepTests.cs</tests>
public sealed class PgSqueezeTableRewriter : ITableRewriter {
  /// <inheritdoc />
  public string Strategy => TableRewriteStrategies.PG_SQUEEZE;

  /// <inheritdoc />
  public bool CanRun => true;

  /// <inheritdoc />
  public string CannotRunReason => "always runnable: pg_squeeze's readiness is the capability verdict's and the online rewrite step's to check";

  /// <inheritdoc />
  public Task<double?> RewriteAsync(IWorkCoordinator coordinator, string tableName, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(coordinator);
    return coordinator.SqueezeTableAsync(tableName, cancellationToken);
  }
}
