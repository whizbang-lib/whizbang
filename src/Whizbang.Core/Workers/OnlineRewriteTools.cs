// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Capabilities;

namespace Whizbang.Core.Workers;

/// <summary>
/// The two online rewrite tools Whizbang runs itself, what each needs Ready, and the choice between them: pg_squeeze
/// when it can run, else pg_repack with its client in process. One choice for every caller: the maintenance cycle under
/// <see cref="TableRewriteStrategy.Online"/> and <see cref="TableRewriteStrategy.Auto"/>, and the boot-time
/// <c>Rewrite</c> step, which must not take a table with <c>VACUUM FULL</c> that <see cref="TableRewriteStrategy.Auto"/>
/// gives to an online tool (#2319).
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#table-rewrite-strategy</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OnlineTableRewriteStepTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Startup/TableRewriteStartupStepTests.cs</tests>
internal static class OnlineRewriteTools {
  /// <summary>A capability a tool needs Ready, with how a log names it and where it is probed.</summary>
  internal sealed record Prerequisite(CapabilityId Id, string Label, string ProbedOn);

  /// <summary>An online tool: the rewriter it runs, the name a log gives it, and the capabilities it needs Ready.</summary>
  internal sealed record Tool(string RewriterStrategy, string Name, Prerequisite[] Prerequisites);

  /// <summary>The first usable tool and its rewriter, or why neither tool can run.</summary>
  internal sealed record Choice(Tool? Tool, ITableRewriter? Rewriter, string? SqueezeWhy, string? RepackWhy);

  internal static readonly Tool PgSqueeze = new(TableRewriteStrategies.PG_SQUEEZE, "pg_squeeze", [
    new(new CapabilityId(RequiresPostgresExtensionAttribute.PROVIDER, "pg_squeeze"), "pg_squeeze", "any database"),
  ]);

  internal static readonly Tool PgRepack = new(TableRewriteStrategies.PG_REPACK, "pg_repack", [
    new(new CapabilityId(RequiresPostgresExtensionAttribute.PROVIDER, "pg_repack"), "pg_repack", "any database"),
    new(PgRepackClient.Capability, "the pg_repack client (" + PgRepackClient.Capability + ")", "this host"),
  ]);

  /// <summary>The rewriter registered under the tool's strategy, or null.</summary>
  internal static ITableRewriter? RewriterFor(Tool tool, IReadOnlyList<ITableRewriter> rewriters) =>
    rewriters.FirstOrDefault(r => string.Equals(r.Strategy, tool.RewriterStrategy, StringComparison.Ordinal));

  /// <summary>Why a capability the tool needs is not Ready everywhere the registry probed it, or null when all are.</summary>
  internal static string? NotReadyReason(Tool tool, ICapabilityRegistry? capabilities) {
    if (capabilities is null) {
      return $"no capability registry is registered, so whether {tool.Name} is Ready is not known";
    }
    foreach (var prerequisite in tool.Prerequisites) {
      var statuses = capabilities.Statuses.Where(s => s.Descriptor.Id == prerequisite.Id).ToArray();
      if (statuses.Length == 0) {
        return $"{prerequisite.Label} has not been probed on {prerequisite.ProbedOn}";
      }
      if (Array.Find(statuses, s => s.State != CapabilityState.Ready) is { } notReady) {
        return $"{prerequisite.Label} is {notReady.State} on '{notReady.Scope}', not Ready";
      }
    }
    return null;
  }

  /// <summary>Why the tool cannot run: a capability not Ready, or no rewriter registered for it; null when it can.</summary>
  internal static string? CannotRunReason(Tool tool, ICapabilityRegistry? capabilities, IReadOnlyList<ITableRewriter> rewriters) =>
    NotReadyReason(tool, capabilities)
      ?? (RewriterFor(tool, rewriters) is null ? $"no table rewriter is registered for {tool.RewriterStrategy}" : null);

  /// <summary>pg_squeeze when it can run, else pg_repack in process when it can, else neither with both reasons.</summary>
  internal static Choice Choose(
      ICapabilityRegistry? capabilities, MaintenanceWorkerOptions settings, IReadOnlyList<ITableRewriter> rewriters) {
    if (CannotRunReason(PgSqueeze, capabilities, rewriters) is not { } squeezeWhy) {
      return new(PgSqueeze, RewriterFor(PgSqueeze, rewriters), null, null);
    }
    var repackWhy = settings.TableRewrite.PgRepack.Mode != PgRepackMode.InProcess
      ? "its client runs as an external job, which Whizbang does not start"
      : CannotRunReason(PgRepack, capabilities, rewriters);
    return repackWhy is null
      ? new(PgRepack, RewriterFor(PgRepack, rewriters), squeezeWhy, null)
      : new(null, null, squeezeWhy, repackWhy);
  }

  /// <summary>
  /// The online tool <see cref="TableRewriteStrategy.Auto"/> resolves to on this host, as a rewriter strategy name, or
  /// null when neither can run (and Auto keeps its blocking-or-report behavior). Null for every other strategy.
  /// </summary>
  internal static string? AutoTool(
      ICapabilityRegistry? capabilities, MaintenanceWorkerOptions settings, IReadOnlyList<ITableRewriter> rewriters) =>
    settings.TableRewriteStrategy == TableRewriteStrategy.Auto
      ? Choose(capabilities, settings, rewriters).Tool?.RewriterStrategy
      : null;
}
