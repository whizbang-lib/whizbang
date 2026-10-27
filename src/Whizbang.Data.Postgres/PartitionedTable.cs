// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// A framework table the partition manager keeps: its name, the column it is partitioned by (a timestamp, or a
/// time-ordered uuid), the length of one partition, and how long its rows are kept.
/// </summary>
/// <remarks>
/// <para>
/// A table declares itself by registering one of these (<see cref="PartitionedTables.AddPartitionedTable(IServiceCollection, PartitionedTable)"/>); the
/// maintenance step of each driver reads every registration, so a table added later needs no change to the manager.
/// A plain table is converted once (<c>wh_partition_convert</c>); a table created partitioned is maintained as it is.
/// </para>
/// <para>
/// Retention is fixed (<see cref="RetainedFor"/>), read every cycle from a <c>wh_settings</c> key holding a number
/// of days (<see cref="RetainedBySetting"/>), so an operator's window applies to the partition drop exactly as it
/// applied to the row sweep, or absent (<see cref="RetainedIndefinitely"/>), which keeps every partition.
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

  /// <summary>
  /// The column the table is partitioned by: a <c>timestamptz</c>, or a <c>uuid</c> holding time-ordered (version 7)
  /// ids, whose day boundaries the manager writes as the first id of each day.
  /// </summary>
  public string PartitionColumn { get; }

  /// <summary>The span of one partition: whole hours, typically a day or a week.</summary>
  public TimeSpan Interval { get; }

  /// <summary>
  /// A fixed retention window, or <see langword="null"/> when it is read from <see cref="RetentionSettingKey"/> or,
  /// with no key either, when every partition is kept.
  /// </summary>
  public TimeSpan? Retention { get; }

  /// <summary>The <c>wh_settings</c> key holding the window in days, or <see langword="null"/> for a fixed or absent window.</summary>
  public string? RetentionSettingKey { get; }

  /// <summary>The window in days when <see cref="RetentionSettingKey"/> has no row.</summary>
  public int RetentionDefaultDays { get; }

  /// <summary>A table whose partitions are kept for a fixed window.</summary>
  /// <param name="table">The table's name.</param>
  /// <param name="partitionColumn">The <c>timestamptz</c> or time-ordered <c>uuid</c> column it is partitioned by.</param>
  /// <param name="interval">The span of one partition.</param>
  /// <param name="retention">How long a partition is kept after its range ends.</param>
  /// <returns>The declaration.</returns>
  public static PartitionedTable RetainedFor(string table, string partitionColumn, TimeSpan interval, TimeSpan retention) {
    ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.Zero);
    return new PartitionedTable(table, partitionColumn, interval, retention, null, 0);
  }

  /// <summary>A table whose window is the number of days in a <c>wh_settings</c> key.</summary>
  /// <param name="table">The table's name.</param>
  /// <param name="partitionColumn">The <c>timestamptz</c> or time-ordered <c>uuid</c> column it is partitioned by.</param>
  /// <param name="interval">The span of one partition.</param>
  /// <param name="settingKey">The <c>wh_settings</c> key holding the window in days.</param>
  /// <param name="defaultDays">The window when the key has no row.</param>
  /// <returns>The declaration.</returns>
  public static PartitionedTable RetainedBySetting(string table, string partitionColumn, TimeSpan interval, string settingKey, int defaultDays) {
    ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);
    ArgumentOutOfRangeException.ThrowIfNegative(defaultDays);
    return new PartitionedTable(table, partitionColumn, interval, null, settingKey, defaultDays);
  }

  /// <summary>
  /// A table whose partitions are created ahead but never dropped: the manager is passed no window. For a table whose
  /// row sweep an operator has turned off, so partitioning keeps what the sweep kept.
  /// </summary>
  /// <param name="table">The table's name.</param>
  /// <param name="partitionColumn">The <c>timestamptz</c> or time-ordered <c>uuid</c> column it is partitioned by.</param>
  /// <param name="interval">The span of one partition.</param>
  /// <returns>The declaration.</returns>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PartitionedTableTests.cs:AnIndefiniteWindow_CarriesNoWindowAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionMaintenanceStepTests.cs:AnIndefiniteWindow_DropsNothingAsync</tests>
  public static PartitionedTable RetainedIndefinitely(string table, string partitionColumn, TimeSpan interval) =>
    new(table, partitionColumn, interval, null, null, 0);
}

/// <summary>The framework's partitioned tables, and the way a table registers.</summary>
/// <docs>operations/infrastructure/partitioned-ledgers#how-a-table-registers</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PartitionedTableTests.cs</tests>
internal static class PartitionedTables {
  private const string LIFECYCLE_COMPLETIONS = "wh_lifecycle_completions";
  private const string EVENT_ID = "event_id";
  private static readonly TimeSpan _day = TimeSpan.FromDays(1);

  /// <summary>
  /// <c>wh_log</c>: weekly by <c>logged_at</c>, kept for <c>log_retention_days</c> (default 14), the window the row
  /// sweep applied before.
  /// </summary>
  public static PartitionedTable Log { get; } =
    PartitionedTable.RetainedBySetting("wh_log", "logged_at", TimeSpan.FromDays(7), "log_retention_days", 14);

  /// <summary>
  /// <c>wh_signals</c>: daily by <c>created_at</c>, kept for the durable-signal window
  /// (<see cref="WhizbangNotificationOptions.SignalRetention"/>), the window the signal sweep applies. The key becomes
  /// <c>(id, created_at)</c> on conversion.
  /// </summary>
  /// <param name="retention">The durable-signal window.</param>
  /// <returns>The declaration.</returns>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PartitionedTableTests.cs:TheSignals_AreDailyByCreatedAt_KeptForTheDurableSignalWindowAsync</tests>
  public static PartitionedTable Signals(TimeSpan retention) =>
    PartitionedTable.RetainedFor("wh_signals", "created_at", _day, retention);

  /// <summary>
  /// <c>wh_lifecycle_completions</c>: daily by <c>event_id</c> range (event ids are time-ordered, so a day is a range
  /// of ids), kept for <see cref="MaintenanceWorkerOptions.LifecycleCompletionRetentionDays"/>. A window of zero or
  /// less turns the completion sweep off, so it keeps every partition too.
  /// </summary>
  /// <param name="retentionDays">The completion window in days; zero or less keeps every partition.</param>
  /// <returns>The declaration.</returns>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PartitionedTableTests.cs:TheLifecycleCompletions_AreDailyByEventId_KeptForTheirRetentionDaysAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PartitionedTableTests.cs:LifecycleCompletionsWithNoWindow_AreKeptIndefinitelyAsync</tests>
  public static PartitionedTable LifecycleCompletions(int retentionDays) =>
    retentionDays > 0
      ? PartitionedTable.RetainedFor(LIFECYCLE_COMPLETIONS, EVENT_ID, _day, TimeSpan.FromDays(retentionDays))
      : PartitionedTable.RetainedIndefinitely(LIFECYCLE_COMPLETIONS, EVENT_ID, _day);

  /// <summary>
  /// Registers the framework's partitioned tables: <c>wh_log</c>, and <c>wh_signals</c> and
  /// <c>wh_lifecycle_completions</c> with the windows their options carry. Each driver calls this once per schema.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PartitionedTableTests.cs:TheFrameworkTables_TakeTheirWindowsFromTheConfiguredOptionsAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PartitionedTableTests.cs:TheFrameworkTables_WithNothingConfigured_UseTheOptionDefaultsAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PgPartmanPartitionMaintenanceTests.cs:TheFrameworkTables_RegisterOneAnnouncerAsync</tests>
  public static IServiceCollection AddFrameworkPartitionedTables(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    services.AddOptions();
    // #1752: one per process, so the cycle says its partition-management choice once and again only when it changes.
    services.TryAddSingleton<PartitionStrategyAnnouncer>();
    return services
      .AddPartitionedTable(Log)
      .AddPartitionedTable(sp => Signals(sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>().Value.SignalRetention))
      .AddPartitionedTable(sp =>
        LifecycleCompletions(sp.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.LifecycleCompletionRetentionDays));
  }

  /// <summary>
  /// Registers a table whose declaration is built from the container when the manager first reads it, for a table
  /// whose window comes from an option.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="table">Builds the table's declaration.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PartitionedTableTests.cs:AddPartitionedTable_FromTheContainer_BuildsTheDeclarationOnResolveAsync</tests>
  public static IServiceCollection AddPartitionedTable(this IServiceCollection services, Func<IServiceProvider, PartitionedTable> table) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(table);
    return services.AddSingleton(table);
  }

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
