// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// The <c>Rewrite</c> step: performs the table rewrites migrations and the runtime bloat detector
/// have recorded, through the <see cref="ITableRewriter"/> of the selected strategy. The framework's
/// strategy is <see cref="VacuumFullTableRewriter"/>: the blocking, space-reclaiming <c>VACUUM FULL</c>
/// that cannot run inside a migration transaction. Post-ready and fleet-exclusive by declaration: it requires the
/// <see cref="StartupDuties.MAINTAINER"/> duty (one instance rewrites), non-holders
/// <see cref="NonHolderBehavior.Skip"/> (nobody blocks on a <c>VACUUM FULL</c> — the entire
/// reason it is non-blocking and deliberately unbounded, because a half-finished rewrite is
/// worse than a slow one).
/// </summary>
/// <remarks>
/// Previously this work ran on the <em>runtime</em> maintenance cadence, taking an ACCESS
/// EXCLUSIVE lock mid-traffic; the pipeline gives it the window it should always have had.
/// The maintenance cycle now only detects and records. A rewriter that cannot run is not called, and
/// its reason is the step's: <c>VACUUM FULL</c> stays behind the operator permission
/// (<see cref="MaintenanceWorkerOptions.AllowTableRewrite"/>), because the framework cannot know how
/// large a consumer's table is, and taking that lock unattended must be opted into.
/// A request is cleared only after the ratio is confirmed to have dropped; an interrupted or
/// ineffective rewrite stays queued for the next boot instead of being silently forgotten.
/// </remarks>
/// <docs>operations/startup/capabilities-and-duties</docs>
/// <docs>operations/infrastructure/migrations#table-rewrites-run-post-ready</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/TableRewriteStartupStepTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Startup/TableRewriteStartupStepCoverageTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableRewriteJourneyE2ETests.cs</tests>
public sealed partial class TableRewriteStartupStep : IStartupStep {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ITableRewriter[] _rewriters;
  private readonly string _strategy;
  private readonly ILogger<TableRewriteStartupStep> _logger;

  /// <summary>Creates the step over the scope factory the coordinator resolves from.</summary>
  /// <param name="scopeFactory">The scope factory the work coordinator is resolved from.</param>
  /// <param name="rewriters">The registered rewriters; the one whose <see cref="ITableRewriter.Strategy"/> is <paramref name="strategy"/> runs.</param>
  /// <param name="strategy">The selected strategy name, for example <see cref="TableRewriteStrategies.VACUUM_FULL"/>.</param>
  /// <param name="logger">The step's logger.</param>
  public TableRewriteStartupStep(
      IServiceScopeFactory scopeFactory,
      IEnumerable<ITableRewriter> rewriters,
      string strategy,
      ILogger<TableRewriteStartupStep> logger) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(rewriters);
    ArgumentException.ThrowIfNullOrWhiteSpace(strategy);
    _scopeFactory = scopeFactory;
    _rewriters = [.. rewriters];
    _strategy = strategy;
    _logger = logger;
  }

  /// <inheritdoc />
  public StartupStepDescriptor Descriptor { get; } = new() {
    Name = FrameworkStartupSteps.REWRITE,
    DependsOn = [FrameworkStartupSteps.MIGRATE],
    RequiredCapability = StartupDuties.MAINTAINER,
    NonHolderBehavior = NonHolderBehavior.Skip,
    Blocking = false,
  };

  /// <inheritdoc />
  public async ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
    var rewriter = Array.Find(_rewriters, r => string.Equals(r.Strategy, _strategy, StringComparison.Ordinal));
    if (rewriter is null) {
      return new StartupStepReport(StartupStepOutcome.Skipped,
        $"no table rewriter registered for strategy '{_strategy}'");
    }
    if (!rewriter.CanRun) {
      return new StartupStepReport(StartupStepOutcome.Skipped, rewriter.CannotRunReason);
    }

    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      return new StartupStepReport(StartupStepOutcome.Skipped, "no work coordinator registered");
    }

    // Candidates are re-measured at call time, so a request recorded for an already-rewritten
    // table yields nothing rather than an expensive no-op.
    var candidates = await coordinator.GetTablesNeedingRewriteAsync(cancellationToken).ConfigureAwait(false);
    if (candidates.Count == 0) {
      return new StartupStepReport(StartupStepOutcome.Skipped, "no rewrites owed");
    }

    var rewritten = 0;
    var leftQueued = 0;
    foreach (var candidate in candidates) {
      cancellationToken.ThrowIfCancellationRequested();
      var after = await rewriter.RewriteAsync(coordinator, candidate.TableName, cancellationToken).ConfigureAwait(false);
      if (after is null || after >= candidate.BloatRatio) {
        // No improvement — leave the request in place so the next boot retries, and say so,
        // rather than clearing it and reporting success we did not achieve.
        leftQueued++;
        LogRewriteIneffective(_logger, candidate.TableName, candidate.BloatRatio, after ?? -1);
        continue;
      }
      rewritten++;
      LogRewriteDone(_logger, candidate.TableName, candidate.BloatRatio, after.Value);
      if (candidate.Requested) {
        await coordinator.ClearTableRewriteRequestAsync(candidate.TableName, cancellationToken).ConfigureAwait(false);
      }
    }

    return new StartupStepReport(StartupStepOutcome.Completed,
      leftQueued == 0
        ? $"rewrote {rewritten} table(s)"
        : $"rewrote {rewritten} table(s); {leftQueued} ineffective, left queued for the next boot");
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Rewrite step: {Table} rewritten, bloat {Before}x -> {After}x")]
  static partial void LogRewriteDone(ILogger logger, string table, double before, double after);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Rewrite step: {Table} rewrite did not reduce bloat ({Before}x -> {After}x); request left queued")]
  static partial void LogRewriteIneffective(ILogger logger, string table, double before, double after);
}
