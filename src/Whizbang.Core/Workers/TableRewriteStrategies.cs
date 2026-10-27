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
}
