// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Data.Postgres;

/// <summary>
/// A framework table the partition manager keeps: its name, the timestamp column it is partitioned by, the length
/// of one partition, and how long its rows are kept.
/// </summary>
/// <remarks>
/// <para>
/// A table declares itself by registering one of these (<see cref="PartitionedTables.AddPartitionedTable"/>); the
/// maintenance step of each driver reads every registration, so a table added later needs no change to the manager.
/// A plain table is converted once (<c>wh_partition_convert</c>); a table created partitioned is maintained as it is.
/// </para>
/// <para>
/// Retention is either fixed (<see cref="RetainedFor"/>) or read every cycle from a <c>wh_settings</c> key holding
/// a number of days (<see cref="RetainedBySetting"/>), so an operator's window applies to the partition drop exactly
/// as it applied to the row sweep.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/partitioned-ledgers#how-a-table-registers</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PartitionedTableTests.cs</tests>
internal sealed record PartitionedTable {
  private PartitionedTable(string table, string partitionColumn, TimeSpan interval, TimeSpan? retention, string? retentionSettingKey,
      int retentionDefaultDays) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    ArgumentException.ThrowIfNullOrWhiteSpace(partitionColumn);
    if (interval < TimeSpan.FromHours(1) || interval.Ticks % TimeSpan.TicksPerHour != 0) {
      throw new ArgumentOutOfRangeException(nameof(interval), interval, "A partition interval is a whole number of hours, at least one.");
    }
    Table = table;
    PartitionColumn = partitionColumn;
    Interval = interval;
    Retention = retention;
    RetentionSettingKey = retentionSettingKey;
    RetentionDefaultDays = retentionDefaultDays;
  }

  /// <summary>The table's name, without a schema; it is looked up in the service's schema.</summary>
  public string Table { get; }

  /// <summary>The <c>timestamptz</c> column the table is partitioned by.</summary>
  public string PartitionColumn { get; }

  /// <summary>The span of one partition: whole hours, typically a day or a week.</summary>
  public TimeSpan Interval { get; }

  /// <summary>A fixed retention window, or <see langword="null"/> when it is read from <see cref="RetentionSettingKey"/>.</summary>
  public TimeSpan? Retention { get; }

  /// <summary>The <c>wh_settings</c> key holding the window in days, or <see langword="null"/> for a fixed window.</summary>
  public string? RetentionSettingKey { get; }

  /// <summary>The window in days when <see cref="RetentionSettingKey"/> has no row.</summary>
  public int RetentionDefaultDays { get; }

  /// <summary>A table whose partitions are kept for a fixed window.</summary>
  /// <param name="table">The table's name.</param>
  /// <param name="partitionColumn">The <c>timestamptz</c> column it is partitioned by.</param>
  /// <param name="interval">The span of one partition.</param>
  /// <param name="retention">How long a partition is kept after its range ends.</param>
  /// <returns>The declaration.</returns>
  public static PartitionedTable RetainedFor(string table, string partitionColumn, TimeSpan interval, TimeSpan retention) {
    ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.Zero);
    return new PartitionedTable(table, partitionColumn, interval, retention, null, 0);
  }

  /// <summary>A table whose window is the number of days in a <c>wh_settings</c> key.</summary>
  /// <param name="table">The table's name.</param>
  /// <param name="partitionColumn">The <c>timestamptz</c> column it is partitioned by.</param>
  /// <param name="interval">The span of one partition.</param>
  /// <param name="settingKey">The <c>wh_settings</c> key holding the window in days.</param>
  /// <param name="defaultDays">The window when the key has no row.</param>
  /// <returns>The declaration.</returns>
  public static PartitionedTable RetainedBySetting(string table, string partitionColumn, TimeSpan interval, string settingKey, int defaultDays) {
    ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);
    ArgumentOutOfRangeException.ThrowIfNegative(defaultDays);
    return new PartitionedTable(table, partitionColumn, interval, null, settingKey, defaultDays);
  }

  /// <summary>A table whose partitions are never dropped.</summary>
  /// <param name="table">The table's name.</param>
  /// <param name="partitionColumn">The column it is partitioned by.</param>
  /// <param name="interval">The span of one partition.</param>
  /// <returns>The declaration.</returns>
  public static PartitionedTable RetainedIndefinitely(string table, string partitionColumn, TimeSpan interval) =>
    throw new NotImplementedException();
}

/// <summary>The framework's partitioned tables, and the way a table registers.</summary>
/// <docs>operations/infrastructure/partitioned-ledgers#how-a-table-registers</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PartitionedTableTests.cs</tests>
internal static class PartitionedTables {
  /// <summary>
  /// <c>wh_log</c>: weekly by <c>logged_at</c>, kept for <c>log_retention_days</c> (default 14), the window the row
  /// sweep applied before.
  /// </summary>
  public static PartitionedTable Log { get; } =
    PartitionedTable.RetainedBySetting("wh_log", "logged_at", TimeSpan.FromDays(7), "log_retention_days", 14);

  /// <summary><c>wh_signals</c>: daily by <c>created_at</c>, kept for the durable-signal window.</summary>
  /// <param name="retention">The durable-signal window.</param>
  /// <returns>The declaration.</returns>
  public static PartitionedTable Signals(TimeSpan retention) => throw new NotImplementedException();

  /// <summary><c>wh_lifecycle_completions</c>: daily by <c>event_id</c>, kept for the completion window.</summary>
  /// <param name="retentionDays">The completion window in days; zero or less keeps every partition.</param>
  /// <returns>The declaration.</returns>
  public static PartitionedTable LifecycleCompletions(int retentionDays) => throw new NotImplementedException();

  /// <summary>Registers the framework's partitioned tables.</summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  public static IServiceCollection AddFrameworkPartitionedTables(this IServiceCollection services) =>
    throw new NotImplementedException();

  /// <summary>Registers a table whose declaration is built from the container.</summary>
  /// <param name="services">The service collection.</param>
  /// <param name="table">Builds the table's declaration.</param>
  /// <returns>The service collection.</returns>
  public static IServiceCollection AddPartitionedTable(this IServiceCollection services, Func<IServiceProvider, PartitionedTable> table) =>
    throw new NotImplementedException();

  /// <summary>
  /// Registers <paramref name="table"/> with the partition manager. Registering the same declaration twice is harmless:
  /// the manager reads each distinct declaration once.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="table">The table's declaration.</param>
  /// <returns>The service collection.</returns>
  public static IServiceCollection AddPartitionedTable(this IServiceCollection services, PartitionedTable table) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(table);
    return services.AddSingleton(table);
  }
}
