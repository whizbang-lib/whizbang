// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>The strategy names a <see cref="ITableRewriter"/> is selected by.</summary>
/// <docs>operations/infrastructure/migrations#table-rewrites-run-post-ready-under-the-maintainer-duty</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/VacuumFullTableRewriterTests.cs</tests>
public static class TableRewriteStrategies {
  /// <summary>
  /// <c>VACUUM FULL</c> at boot: blocking, under an ACCESS EXCLUSIVE lock, and only when
  /// <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/> permits it.
  /// </summary>
  public const string VACUUM_FULL = "VacuumFull";

  /// <summary>
  /// Online rewrite through pg_squeeze, selected by <see cref="TableRewriteStrategy.PgSqueeze"/> and run by
  /// <see cref="PgSqueezeTableRewriter"/> from the maintenance cycle.
  /// </summary>
  public const string PG_SQUEEZE = "PgSqueeze";

  /// <summary>
  /// Online rewrite through the pg_repack client, selected by <see cref="TableRewriteStrategy.PgRepack"/>; run by
  /// <see cref="PgRepackTableRewriter"/> in process, or by an external job.
  /// </summary>
  public const string PG_REPACK = "PgRepack";

  /// <summary>
  /// The plan of <see cref="TableRewriteStrategy.Online"/>: the maintenance cycle picks pg_squeeze, pg_repack or a
  /// blue-green rebuild per cycle, as the server allows. No rewriter is registered under it.
  /// </summary>
  public const string ONLINE = "Online";

  /// <summary>
  /// A blue-green rebuild of a perspective table, run by <see cref="BlueGreenTableRewriter"/> from the maintenance cycle
  /// under <see cref="TableRewriteStrategy.Online"/> when no online extension is usable (#2301).
  /// </summary>
  public const string BLUE_GREEN = "BlueGreenRebuild";
}
