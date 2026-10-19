// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Observability;

/// <summary>
/// Settings for the table statistics the <c>Whizbang.TableStatistics</c> meter reports, bound from the
/// <c>Whizbang:TableStatistics</c> configuration section.
/// </summary>
/// <remarks>
/// Every framework table and every perspective table in the service's schema is reported, and a
/// service can declare as many perspectives as it likes. The cap is what keeps the number of series a
/// metrics backend has to store from growing with the schema.
/// </remarks>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsRegistrationTests.cs</tests>
public sealed class TableStatisticsOptions {
#pragma warning disable CA1707
  /// <summary>The configuration section these settings are bound from.</summary>
  public const string SECTION = "Whizbang:TableStatistics";

  /// <summary>
  /// The <c>table_name</c> the tables past <see cref="MaxTrackedTables"/> are summed under. No table
  /// the framework reports can have this name, because every reported table's name starts with
  /// <c>wh_</c>.
  /// </summary>
  public const string OTHER_TABLES = "other";

  /// <summary>The default for <see cref="MaxTrackedTables"/>.</summary>
  public const int DEFAULT_MAX_TRACKED_TABLES = 500;
#pragma warning restore CA1707

  /// <summary>
  /// The most tables reported by name. Past this, the smallest tables are summed under
  /// <see cref="OTHER_TABLES"/>: bytes and rows are added, so every total stays right, and the bloat
  /// ratio, which cannot be added, is not reported for them. Must be at least 1.
  /// </summary>
  /// <remarks>
  /// Configuration key <c>Whizbang:TableStatistics:MaxTrackedTables</c>, environment variable
  /// <c>Whizbang__TableStatistics__MaxTrackedTables</c>. Default 500.
  /// </remarks>
  public int MaxTrackedTables { get; set; } = DEFAULT_MAX_TRACKED_TABLES;
}
