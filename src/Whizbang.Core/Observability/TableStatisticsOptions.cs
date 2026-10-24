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
  /// <summary>
  /// The <c>table_name</c> the tables past <see cref="MaxTrackedTables"/> are summed under. No table
  /// the framework reports can have this name, because every reported table's name starts with
  /// <c>wh_</c>.
  /// </summary>
  public const string OTHER_TABLES = "other";

  /// <summary>The default for <see cref="MaxTrackedTables"/>.</summary>
  public const int DEFAULT_MAX_TRACKED_TABLES = 500;

  /// <summary>
  /// The <c>event_type</c> the event types past <see cref="MaxEventTypes"/> are summed under. No stored
  /// event type can have this name, because every stored one names its assembly after a comma.
  /// </summary>
  public const string OTHER_EVENT_TYPES = "other";

  /// <summary>The default for <see cref="MaxEventTypes"/>.</summary>
  public const int DEFAULT_MAX_EVENT_TYPES = 50;

  /// <summary>The default for <see cref="EventTypeSamplePercent"/>.</summary>
  public const double DEFAULT_EVENT_TYPE_SAMPLE_PERCENT = 1;
#pragma warning restore CA1707

  /// <summary>The default for <see cref="CollectionInterval"/>: thirty seconds.</summary>
  public static readonly TimeSpan DefaultCollectionInterval = TimeSpan.FromSeconds(30);

  /// <summary>The default for <see cref="EventTypeSampleInterval"/>: fifteen minutes.</summary>
  public static readonly TimeSpan DefaultEventTypeSampleInterval = TimeSpan.FromMinutes(15);

  /// <summary>
  /// How often each instance reads the tables' sizes, rows, bloat, unused indexes and queue depths into the gauges.
  /// Each reading is one catalog statement for every table, so it costs the same however much the tables hold. Must be
  /// greater than zero.
  /// </summary>
  /// <remarks>
  /// Configuration key <c>Whizbang:TableStatistics:CollectionInterval</c>, environment variable
  /// <c>Whizbang__TableStatistics__CollectionInterval</c>. Default <c>00:00:30</c>.
  /// </remarks>
  /// <docs>operations/configuration/configuration-reference#tablestatisticsoptions</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsRegistrationTests.cs:TheCollectionInterval_BindsFromTheTableStatisticsSectionAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Observability/TableStatisticsCollectorIntervalTests.cs:TheConfiguredInterval_IsTheWaitBetweenCyclesAsync</tests>
  public TimeSpan CollectionInterval { get; set; } = DefaultCollectionInterval;

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

  /// <summary>
  /// How often the event store is sampled by event type. One instance of the fleet samples per interval;
  /// <see cref="TimeSpan.Zero"/> turns the sample off. Must not be negative.
  /// </summary>
  /// <remarks>
  /// Configuration key <c>Whizbang:TableStatistics:EventTypeSampleInterval</c>, environment variable
  /// <c>Whizbang__TableStatistics__EventTypeSampleInterval</c>. Default <c>00:15:00</c>.
  /// </remarks>
  public TimeSpan EventTypeSampleInterval { get; set; } = DefaultEventTypeSampleInterval;

  /// <summary>
  /// The share of the event store's pages one sample reads, as a percentage greater than 0 and at most 100.
  /// The cost of a sample grows with it; so does its accuracy for rare event types.
  /// </summary>
  /// <remarks>
  /// Configuration key <c>Whizbang:TableStatistics:EventTypeSamplePercent</c>, environment variable
  /// <c>Whizbang__TableStatistics__EventTypeSamplePercent</c>. Default 1.
  /// </remarks>
  public double EventTypeSamplePercent { get; set; } = DEFAULT_EVENT_TYPE_SAMPLE_PERCENT;

  /// <summary>
  /// The most event types reported by name, the largest by bytes. The rest are summed under
  /// <see cref="OTHER_EVENT_TYPES"/>, so the number of series stays bounded however many types a service
  /// declares. Must be at least 1.
  /// </summary>
  /// <remarks>
  /// Configuration key <c>Whizbang:TableStatistics:MaxEventTypes</c>, environment variable
  /// <c>Whizbang__TableStatistics__MaxEventTypes</c>. Default 50.
  /// </remarks>
  public int MaxEventTypes { get; set; } = DEFAULT_MAX_EVENT_TYPES;
}
