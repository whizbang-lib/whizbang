// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using Whizbang.Core.Capabilities;

namespace Whizbang.Core.Workers;

/// <summary>
/// How the post-ready <c>Rewrite</c> step reclaims the space the bloat detector recorded
/// (<c>Whizbang:Workers:Maintenance:TableRewriteStrategy</c>), resolved against
/// <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/> by <see cref="TableRewriteStrategyResolver"/>.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#table-rewrite-strategy</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TableRewriteStrategyTests.cs</tests>
public enum TableRewriteStrategy {
  /// <summary>
  /// The default. Rewrites with <c>VACUUM FULL</c> at boot when <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/>
  /// permits it, and otherwise only detects and reports. It never starts an online rewrite, even where pg_squeeze is
  /// usable: an online rewrite is opted into with <see cref="PgSqueeze"/>.
  /// </summary>
  Auto,

  /// <summary>
  /// Online rewrite through the pg_squeeze extension, without a long exclusive lock. Chosen explicitly, it requires
  /// pg_squeeze, so a server that cannot provide it stops start with the capability verdict's fix. It is never
  /// replaced by a blocking rewrite. This version has no pg_squeeze rewriter yet: the step reports that, and requested
  /// tables stay queued.
  /// </summary>
  [RequiresPostgresExtension("pg_squeeze", Feature = "Online table rewrite")]
  PgSqueeze,

  /// <summary>
  /// Online rewrite through pg_repack, which holds an ACCESS EXCLUSIVE lock only briefly at the start and the end.
  /// Chosen explicitly, it requires the pg_repack extension, so a server that cannot provide it stops start with the
  /// capability verdict's fix. Its client program runs in process or as an external job, as
  /// <see cref="PgRepackOptions.Mode"/> says; in process the client is a probed prerequisite too.
  /// </summary>
  [RequiresPostgresExtension("pg_repack", Feature = "Online table rewrite (pg_repack)")]
  PgRepack,

  /// <summary>
  /// <c>VACUUM FULL</c> at boot, under an ACCESS EXCLUSIVE lock, when <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/>
  /// permits it.
  /// </summary>
  VacuumFull,

  /// <summary>No table is rewritten. The maintenance cycle still detects, records and reports the tables that need it.</summary>
  None,
}

/// <summary>What the <c>Rewrite</c> step does on this start: the rewriter strategy to run, or none, and why.</summary>
/// <param name="Strategy">
/// The <see cref="ITableRewriter.Strategy"/> to run (<see cref="TableRewriteStrategies"/>), or null to rewrite nothing
/// and only detect and report.
/// </param>
/// <param name="Reason">Why, in a phrase the step reports.</param>
/// <docs>operations/infrastructure/storage-maintenance#table-rewrite-strategy</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TableRewriteStrategyTests.cs</tests>
public readonly record struct TableRewritePlan(string? Strategy, string Reason);

/// <summary>
/// Resolves <see cref="TableRewriteStrategy"/> against <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/> and
/// whether pg_squeeze is usable, cell by cell of the resolution matrix.
/// </summary>
/// <remarks>
/// <list type="table">
///   <listheader><term>Strategy</term><description>Resolves to</description></listheader>
///   <item><term>Auto</term><description><c>VACUUM FULL</c> when AllowTableRewrite, otherwise nothing; never pg_squeeze (decision 1, #1671). A usable pg_squeeze is named, with how to opt in.</description></item>
///   <item><term>PgSqueeze</term><description>pg_squeeze, whatever AllowTableRewrite says (choosing it is the opt-in). When pg_squeeze is not usable the capability verdict stops start; it is never replaced by <c>VACUUM FULL</c>.</description></item>
///   <item><term>PgRepack</term><description>pg_repack, whatever AllowTableRewrite says or pg_squeeze is (choosing it is the opt-in); in process or by an external job as <see cref="PgRepackOptions.Mode"/> says.</description></item>
///   <item><term>VacuumFull</term><description><c>VACUUM FULL</c> when AllowTableRewrite, otherwise nothing.</description></item>
///   <item><term>None</term><description>Nothing.</description></item>
/// </list>
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#table-rewrite-strategy</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TableRewriteStrategyTests.cs</tests>
public static class TableRewriteStrategyResolver {
  private const string ALLOW_KEY = "Whizbang:Workers:Maintenance:AllowTableRewrite";
  private const string STRATEGY_KEY = "Whizbang:Workers:Maintenance:TableRewriteStrategy";
  private const string AT_BOOT = "VACUUM FULL at boot (" + ALLOW_KEY + " = true)";
  private const string NOT_PERMITTED = "VACUUM FULL is not permitted (" + ALLOW_KEY + " = false)";

  /// <summary>The plan for one start.</summary>
  /// <param name="strategy">The configured strategy.</param>
  /// <param name="allowTableRewrite">Whether the operator permits a blocking rewrite at boot.</param>
  /// <param name="pgSqueezeUsable">Whether pg_squeeze is usable on the databases probed.</param>
  /// <returns>The rewriter strategy to run, or none, and why.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="strategy"/> is not a declared value.</exception>
  public static TableRewritePlan Resolve(TableRewriteStrategy strategy, bool allowTableRewrite, bool pgSqueezeUsable) =>
    strategy switch {
      TableRewriteStrategy.Auto => _auto(allowTableRewrite, pgSqueezeUsable),
      TableRewriteStrategy.PgSqueeze => new(TableRewriteStrategies.PG_SQUEEZE, pgSqueezeUsable
        ? "PgSqueeze: online rewrite through pg_squeeze"
        : "PgSqueeze: pg_squeeze is not usable here, and the capability verdict stops start; VACUUM FULL is never used in its place"),
      TableRewriteStrategy.PgRepack => new(TableRewriteStrategies.PG_REPACK, "PgRepack: online rewrite through pg_repack"),
      TableRewriteStrategy.VacuumFull => allowTableRewrite
        ? new(TableRewriteStrategies.VACUUM_FULL, "VacuumFull: " + AT_BOOT)
        : new(null, "VacuumFull: " + NOT_PERMITTED),
      TableRewriteStrategy.None => new(null, "None: no table is rewritten; the maintenance cycle still records and reports those that need it"),
      _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "not a TableRewriteStrategy"),
    };

  private static TableRewritePlan _auto(bool allowTableRewrite, bool pgSqueezeUsable) {
    var plan = allowTableRewrite
      ? new TableRewritePlan(TableRewriteStrategies.VACUUM_FULL, "Auto: " + AT_BOOT)
      : new TableRewritePlan(null, "Auto detects and reports only: " + NOT_PERMITTED);
    return pgSqueezeUsable
      ? plan with { Reason = plan.Reason + "; pg_squeeze is usable, but Auto never starts an online rewrite: set " + STRATEGY_KEY + " = PgSqueeze to opt in" }
      : plan;
  }
}
