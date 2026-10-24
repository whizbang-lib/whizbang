// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// The <see cref="TableRewriteStrategies.VACUUM_FULL"/> strategy: rewrites a table with <c>VACUUM FULL</c>,
/// which blocks reads and writes on it under an ACCESS EXCLUSIVE lock for the rewrite's duration, then
/// re-measures it.
/// </summary>
/// <remarks>
/// It runs only when <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/> is true: the framework
/// cannot know how large a consumer's table is, and taking that lock unattended must be opted into. The
/// rewrite itself is <see cref="IWorkCoordinator.RewriteTableAsync"/>, which validates the table name,
/// runs on the schema-initialization connection when one is configured, and measures the ratio afterwards.
/// </remarks>
/// <docs>operations/infrastructure/migrations#table-rewrites-run-post-ready</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/VacuumFullTableRewriterTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableRewriteJourneyE2ETests.cs</tests>
public sealed class VacuumFullTableRewriter : ITableRewriter {
  private readonly MaintenanceWorkerOptions _options;

  /// <summary>Creates the rewriter over the maintenance options that hold the operator's permission.</summary>
  /// <param name="options">The maintenance options; <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/> gates the rewrite.</param>
  public VacuumFullTableRewriter(IOptions<MaintenanceWorkerOptions> options) {
    ArgumentNullException.ThrowIfNull(options);
    _options = options.Value;
  }

  /// <inheritdoc />
  public string Strategy => TableRewriteStrategies.VACUUM_FULL;

  /// <inheritdoc />
  public bool CanRun => _options.AllowTableRewrite;

  /// <inheritdoc />
  public string CannotRunReason => "table rewrites not permitted (MaintenanceWorkerOptions.AllowTableRewrite)";

  /// <inheritdoc />
  public Task<double?> RewriteAsync(IWorkCoordinator coordinator, string tableName, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(coordinator);
    return coordinator.RewriteTableAsync(tableName, cancellationToken);
  }
}
