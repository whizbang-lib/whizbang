// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>The strategy names a <see cref="ITableRewriter"/> is selected by.</summary>
/// <docs>operations/infrastructure/migrations#table-rewrites-run-post-ready</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/VacuumFullTableRewriterTests.cs</tests>
public static class TableRewriteStrategies {
  /// <summary>
  /// <c>VACUUM FULL</c> at boot: blocking, under an ACCESS EXCLUSIVE lock, and only when
  /// <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/> permits it.
  /// </summary>
  public const string VACUUM_FULL = "VacuumFull";
}
