// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Whizbang.Core.Workers;

/// <summary>
/// The built-in partition manager, under <c>Whizbang:Workers:Maintenance:Partitioning</c>: whether the maintenance
/// cycle converts the framework's time-ordered tables to time partitions and keeps their partitions, and how far ahead
/// it creates them.
/// </summary>
/// <remarks>
/// <para>
/// Off by default until the conversion has soaked. While it is off nothing is converted, created or dropped, and
/// every table keeps its row-by-row retention. Once on, each registered table (<c>wh_log</c> weekly;
/// <c>wh_signals</c> and <c>wh_lifecycle_completions</c> daily) is converted once, partitions are created <see cref="CreateAhead"/> intervals forward, and a partition whose whole
/// range is past the table's retention window is dropped. Only the instance holding the maintainer duty does this.
/// </para>
/// <para>
/// Published to <c>wh_settings</c> as <c>partition_maintenance_enabled</c> every cycle, in both directions, so the
/// row sweep knows to leave a partitioned table to the partition drop, and takes it back when this is switched off.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/partitioned-ledgers#enabling-it</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PartitioningOptionsTests.cs</tests>
public sealed class MaintenancePartitioningOptions {
  /// <summary>The number of intervals created ahead unless configured otherwise.</summary>
  public const int DEFAULT_CREATE_AHEAD = 3;

  private const string SECTION = "Whizbang:Workers:Maintenance:Partitioning";

  /// <summary>
  /// Whether the maintenance cycle converts and maintains partitioned tables. Default <see langword="false"/>.
  /// </summary>
  public bool Enabled { get; set; }

  /// <summary>
  /// How many intervals past the current one each cycle makes sure exist, so a writer always finds its partition even
  /// when maintenance stalls for a while. Default <see cref="DEFAULT_CREATE_AHEAD"/>; at least 1.
  /// </summary>
  public int CreateAhead { get; set; } = DEFAULT_CREATE_AHEAD;

  /// <summary>
  /// Who creates partitions (<c>Whizbang:Workers:Maintenance:Partitioning:Strategy</c>). Default
  /// <see cref="PartitionManagementStrategy.Auto"/>: pg_partman where it is already Ready, the built-in manager
  /// otherwise. Expired partitions are always dropped by the built-in manager.
  /// </summary>
  /// <docs>operations/infrastructure/partitioned-ledgers#pg-partman</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PartitionManagementStrategyTests.cs:TheSetting_DefaultsToAuto_AndBindsFromConfigurationAsync</tests>
  public PartitionManagementStrategy Strategy { get; set; } = PartitionManagementStrategy.Auto;

  /// <summary>Configuration errors in the partitioning options, by key.</summary>
  /// <param name="partitioning">The bound options.</param>
  /// <returns>One message per invalid value; empty when the options are valid.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PartitioningOptionsTests.cs:CreateAheadBelowOne_IsRejectedByNameAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PartitioningOptionsTests.cs:NullPartitioning_IsRejectedAsync</tests>
  internal static List<string> Validate(MaintenancePartitioningOptions? partitioning) {
    if (partitioning is null) {
      return [$"{SECTION} must not be null."];
    }
    return partitioning.CreateAhead >= 1
      ? []
      : [$"{SECTION}:{nameof(CreateAhead)} must be 1 or greater (was {partitioning.CreateAhead.ToString(CultureInfo.InvariantCulture)}); "
         + "with no partition ahead of the current one, rows written after the current interval ends wait in the default partition."];
  }
}
