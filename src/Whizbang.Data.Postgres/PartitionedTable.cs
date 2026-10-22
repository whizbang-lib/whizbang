// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Data.Postgres;

/// <summary>Stub (RED).</summary>
internal sealed record PartitionedTable {
  public string Table { get; } = "";
  public string PartitionColumn { get; } = "";
  public TimeSpan Interval { get; }
  public TimeSpan? Retention { get; }
  public string? RetentionSettingKey { get; }
  public int RetentionDefaultDays { get; }

  public static PartitionedTable RetainedFor(string table, string partitionColumn, TimeSpan interval, TimeSpan retention) =>
    throw new NotImplementedException();

  public static PartitionedTable RetainedBySetting(string table, string partitionColumn, TimeSpan interval, string settingKey, int defaultDays) =>
    throw new NotImplementedException();
}

/// <summary>Stub (RED).</summary>
internal static class PartitionedTables {
  public static PartitionedTable Log => throw new NotImplementedException();

  public static IServiceCollection AddPartitionedTable(this IServiceCollection services, PartitionedTable table) =>
    throw new NotImplementedException();
}
