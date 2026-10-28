// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// The <c>Rewrite</c> step: performs the table rewrites migrations and the runtime bloat detector
/// have recorded, through the <see cref="ITableRewriter"/> of the strategy
/// <see cref="TableRewriteStrategyResolver"/> resolves from <see cref="MaintenanceWorkerOptions.TableRewriteStrategy"/>
/// and <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/>. The framework's rewriter is
/// <see cref="VacuumFullTableRewriter"/>: the blocking, space-reclaiming <c>VACUUM FULL</c>
/// that cannot run inside a migration transaction. Post-ready and fleet-exclusive by declaration: it requires the
/// <see cref="StartupDuties.MAINTAINER"/> duty (one instance rewrites), non-holders
/// <see cref="NonHolderBehavior.Skip"/> (nobody blocks on a <c>VACUUM FULL</c> — the entire
/// reason it is non-blocking and deliberately unbounded, because a half-finished rewrite is
/// worse than a slow one).
/// </summary>
/// <remarks>
/// Previously this work ran on the <em>runtime</em> maintenance cadence, taking an ACCESS
/// EXCLUSIVE lock mid-traffic; the pipeline gives it the window it should always have had.
/// The maintenance cycle detects and records. A strategy that resolves to no rewrite, or to a strategy
/// with no registered rewriter, skips with the resolution as its reason, and so does a rewriter that cannot run.
/// <see cref="TableRewriteStrategy.PgSqueeze"/>, and <see cref="TableRewriteStrategy.PgRepack"/> with the client run in
/// process, are left to the maintenance cycle (<see cref="OnlineTableRewriteStep"/>), which rewrites online within a
/// per-cycle byte budget; pg_repack in <see cref="PgRepackMode.External"/> mode is skipped with the reason its rewriter
/// gives, because an operator's job runs it. <c>VACUUM FULL</c> stays behind the operator permission
/// (<see cref="MaintenanceWorkerOptions.AllowTableRewrite"/>), because the framework cannot know how
/// large a consumer's table is, and taking that lock unattended must be opted into.
/// A request is cleared only after the ratio is confirmed to have dropped; an interrupted or
/// ineffective rewrite stays queued for the next boot instead of being silently forgotten.
/// </remarks>
/// <docs>operations/startup/capabilities-and-duties</docs>
/// <docs>operations/infrastructure/migrations#table-rewrites-run-post-ready-under-the-maintainer-duty</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/TableRewriteStartupStepTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Startup/TableRewriteStartupStepCoverageTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableRewriteJourneyE2ETests.cs</tests>
public sealed partial class TableRewriteStartupStep : IStartupStep {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ITableRewriter[] _rewriters;
  private readonly IOptions<MaintenanceWorkerOptions> _options;
  private readonly ICapabilityRegistry? _capabilities;
  private readonly ILogger<TableRewriteStartupStep> _logger;

  /// <summary>Creates the step over the scope factory the coordinator resolves from.</summary>
  /// <param name="scopeFactory">The scope factory the work coordinator is resolved from.</param>
  /// <param name="rewriters">The registered rewriters; the one whose <see cref="ITableRewriter.Strategy"/> the strategy resolves to runs.</param>
  /// <param name="options">The maintenance options: <see cref="MaintenanceWorkerOptions.TableRewriteStrategy"/> and <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/>.</param>
  /// <param name="capabilities">
  /// The capability registry, which says whether pg_squeeze is usable, or null when the host registers none. With
  /// one, the step runs after the <c>Capabilities</c> step that probes it.
  /// </param>
  /// <param name="logger">The step's logger.</param>
  public TableRewriteStartupStep(
      IServiceScopeFactory scopeFactory,
      IEnumerable<ITableRewriter> rewriters,
      IOptions<MaintenanceWorkerOptions> options,
      ICapabilityRegistry? capabilities,
      ILogger<TableRewriteStartupStep> logger) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(rewriters);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(logger);
    _scopeFactory = scopeFactory;
    _rewriters = [.. rewriters];
    _options = options;
    _capabilities = capabilities;
    _logger = logger;
    Descriptor = new() {
      Name = FrameworkStartupSteps.REWRITE,
      DependsOn = capabilities is null
        ? [FrameworkStartupSteps.MIGRATE]
        : [FrameworkStartupSteps.MIGRATE, CapabilitiesStartupStep.STEP_NAME],
      RequiredCapability = StartupDuties.MAINTAINER,
      NonHolderBehavior = NonHolderBehavior.Skip,
      Blocking = false,
    };
  }

  /// <inheritdoc />
  public StartupStepDescriptor Descriptor { get; }

  /// <inheritdoc />
  public async ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
    var options = _options.Value;
    var plan = TableRewriteStrategyResolver.Resolve(options.TableRewriteStrategy, options.AllowTableRewrite, _pgSqueezeUsable());
    if (plan.Strategy is null) {
      return new StartupStepReport(StartupStepOutcome.Skipped, plan.Reason);
    }
    if (string.Equals(plan.Strategy, TableRewriteStrategies.PG_SQUEEZE, StringComparison.Ordinal) || PgRepackClient.RunsInProcess(options)) {
      // An online rewrite Whizbang runs itself is budgeted per maintenance cycle (OnlineTableRewriteStep); run here it
      // would be unbounded. pg_repack in External mode falls through: its rewriter cannot run, and says why.
      return new StartupStepReport(StartupStepOutcome.Skipped,
        $"{plan.Reason}; the maintenance cycle performs it, largest bloat first, up to {ONLINE_BUDGET_KEY} bytes per cycle");
    }
    var rewriter = Array.Find(_rewriters, r => string.Equals(r.Strategy, plan.Strategy, StringComparison.Ordinal));
    if (rewriter is null) {
      return new StartupStepReport(StartupStepOutcome.Skipped,
        $"{plan.Reason}; no table rewriter is registered for it, so requested tables stay queued");
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

  /// <summary>Whether pg_squeeze is usable (Ready or Provisionable) on every database the registry probes it on.</summary>
  private bool _pgSqueezeUsable() {
    if (_capabilities is null) {
      return false;
    }
    var squeeze = new CapabilityId(RequiresPostgresExtensionAttribute.PROVIDER, PG_SQUEEZE_EXTENSION);
    var statuses = _capabilities.Statuses.Where(s => s.Descriptor.Id == squeeze).ToList();
    return statuses.Count > 0 && statuses.TrueForAll(s => s.IsUsable);
  }

  private const string PG_SQUEEZE_EXTENSION = "pg_squeeze";
  private const string ONLINE_BUDGET_KEY = "Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle";

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Rewrite step: {Table} rewritten, bloat {Before}x -> {After}x")]
  static partial void LogRewriteDone(ILogger logger, string table, double before, double after);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Rewrite step: {Table} rewrite did not reduce bloat ({Before}x -> {After}x); request left queued")]
  static partial void LogRewriteIneffective(ILogger logger, string table, double before, double after);
}
