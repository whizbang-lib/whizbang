using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// The <c>Rewrite</c> step: performs the table rewrites migrations and the runtime bloat detector
/// have recorded — the blocking, space-reclaiming <c>VACUUM FULL</c> that cannot run inside a
/// migration transaction. Post-ready and fleet-exclusive by declaration: it requires the
/// <see cref="StartupDuties.MAINTAINER"/> duty (one instance rewrites), non-holders
/// <see cref="NonHolderBehavior.Skip"/> (nobody blocks on a <c>VACUUM FULL</c> — the entire
/// reason it is non-blocking and deliberately unbounded, because a half-finished rewrite is
/// worse than a slow one).
/// </summary>
/// <remarks>
/// Previously this work ran on the <em>runtime</em> maintenance cadence, taking an ACCESS
/// EXCLUSIVE lock mid-traffic; the pipeline gives it the window it should always have had.
/// The maintenance cycle now only detects and records. Execution stays behind the same operator
/// permission (<see cref="MaintenanceWorkerOptions.AllowTableRewrite"/>) — the framework cannot
/// know how large a consumer's table is, and taking that lock unattended must be opted into.
/// A request is cleared only after the ratio is confirmed to have dropped; an interrupted or
/// ineffective rewrite stays queued for the next boot instead of being silently forgotten.
/// </remarks>
/// <docs>proposals/startup-pipeline#capabilities</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/TableRewriteStartupStepTests.cs</tests>
public sealed partial class TableRewriteStartupStep : IStartupStep {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly MaintenanceWorkerOptions _options;
  private readonly ILogger<TableRewriteStartupStep> _logger;

  /// <summary>Creates the step over the scope factory the coordinator resolves from.</summary>
  public TableRewriteStartupStep(
      IServiceScopeFactory scopeFactory,
      IOptions<MaintenanceWorkerOptions>? options = null,
      ILogger<TableRewriteStartupStep>? logger = null) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    _scopeFactory = scopeFactory;
    _options = options?.Value ?? new MaintenanceWorkerOptions();
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TableRewriteStartupStep>.Instance;
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
    if (!_options.AllowTableRewrite) {
      return new StartupStepReport(StartupStepOutcome.Skipped,
        "table rewrites not permitted (MaintenanceWorkerOptions.AllowTableRewrite)");
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
      var after = await coordinator.RewriteTableAsync(candidate.TableName, cancellationToken).ConfigureAwait(false);
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
