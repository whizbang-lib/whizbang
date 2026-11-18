// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;

namespace Whizbang.Core.Workers;

/// <summary>
/// The retention categories of the operational records cleanup and integrity keep about their own work (#2354). Each
/// category rests for <see cref="OperationalRetentionOptions.Default"/> unless it has its own override.
/// </summary>
/// <docs>operations/infrastructure/operational-retention#categories</docs>
public enum OperationalCategory {
  /// <summary>Repair ledger rows and repair attempts, once closed.</summary>
  RepairLedger = 0,

  /// <summary>The origin rewrite log (<c>wh_integrity_origin_rewrites</c>).</summary>
  RewriteLog = 1,

  /// <summary>Delivery, answer and handled records.</summary>
  Deliveries = 2,

  /// <summary>Drift, divergence and other reports.</summary>
  Reports = 3,

  /// <summary>Pair halt history and instance exit history.</summary>
  PairHistory = 4,

  /// <summary>Results of re-check runs.</summary>
  RecheckRuns = 5,

  /// <summary>Finished queue rows.</summary>
  Queues = 6,

  /// <summary>Per-run maintenance records and logs.</summary>
  MaintenanceRuns = 7,

  /// <summary>
  /// The per-stream detail of what cuts carried (#2363): the carried part of a stream's digest bucket, kept so a recent
  /// cut can be investigated and the bucket re-checked, then cleared. The bucket's digest and the epochs' removed rows
  /// stay.
  /// </summary>
  CarriedDetail = 8,
}

/// <summary>
/// How long operational records rest before the maintenance cycle removes them (<c>Whizbang:OperationalRetention</c>,
/// #2354).
/// </summary>
/// <remarks>
/// <para>
/// Cleanup and integrity record their own work: ledgers, logs, attempt and delivery records, reports and finished queue
/// rows. They are kept long enough to investigate, then removed, so the bookkeeping does not grow with the system. Only
/// state that correctness depends on (seals, epochs, frontiers, stream floors, current pair state, cursors) is kept
/// indefinitely; the table registry says which is which, and a guard test fails when a table is in neither.
/// </para>
/// <para>
/// A row is removed only when it is past its category's resting period <em>and</em> closed: an open ledger bucket, an
/// in-flight delivery or a request still waiting is state until it resolves. Retention of telemetry itself (logs,
/// traces, metrics in the observability backend) is not configured here.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/operational-retention</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OperationalRetentionOptionsTests.cs</tests>
public sealed class OperationalRetentionOptions {
  /// <summary>
  /// Remove operational records past their resting period (<c>Whizbang:OperationalRetention:Enabled</c>, default true).
  /// </summary>
  /// <docs>operations/infrastructure/operational-retention#configuration</docs>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// The resting period of every category without its own (<c>Whizbang:OperationalRetention:Default</c>, default 14 days).
  /// Must be positive.
  /// </summary>
  /// <docs>operations/infrastructure/operational-retention#configuration</docs>
  public TimeSpan Default { get; set; } = TimeSpan.FromDays(14);

  /// <summary>The <see cref="OperationalCategory.RepairLedger"/> resting period (<c>Whizbang:OperationalRetention:RepairLedger</c>); null for <see cref="Default"/>.</summary>
  /// <docs>operations/infrastructure/operational-retention#categories</docs>
  public TimeSpan? RepairLedger { get; set; }

  /// <summary>The <see cref="OperationalCategory.RewriteLog"/> resting period (<c>Whizbang:OperationalRetention:RewriteLog</c>); null for <see cref="Default"/>.</summary>
  /// <docs>operations/infrastructure/operational-retention#categories</docs>
  public TimeSpan? RewriteLog { get; set; }

  /// <summary>The <see cref="OperationalCategory.Deliveries"/> resting period (<c>Whizbang:OperationalRetention:Deliveries</c>); null for <see cref="Default"/>.</summary>
  /// <docs>operations/infrastructure/operational-retention#categories</docs>
  public TimeSpan? Deliveries { get; set; }

  /// <summary>The <see cref="OperationalCategory.Reports"/> resting period (<c>Whizbang:OperationalRetention:Reports</c>); null for <see cref="Default"/>.</summary>
  /// <docs>operations/infrastructure/operational-retention#categories</docs>
  public TimeSpan? Reports { get; set; }

  /// <summary>The <see cref="OperationalCategory.PairHistory"/> resting period (<c>Whizbang:OperationalRetention:PairHistory</c>); null for <see cref="Default"/>.</summary>
  /// <docs>operations/infrastructure/operational-retention#categories</docs>
  public TimeSpan? PairHistory { get; set; }

  /// <summary>The <see cref="OperationalCategory.RecheckRuns"/> resting period (<c>Whizbang:OperationalRetention:RecheckRuns</c>); null for <see cref="Default"/>.</summary>
  /// <docs>operations/infrastructure/operational-retention#categories</docs>
  public TimeSpan? RecheckRuns { get; set; }

  /// <summary>The <see cref="OperationalCategory.Queues"/> resting period (<c>Whizbang:OperationalRetention:Queues</c>); null for <see cref="Default"/>.</summary>
  /// <docs>operations/infrastructure/operational-retention#categories</docs>
  public TimeSpan? Queues { get; set; }

  /// <summary>The <see cref="OperationalCategory.MaintenanceRuns"/> resting period (<c>Whizbang:OperationalRetention:MaintenanceRuns</c>); null for <see cref="Default"/>.</summary>
  /// <docs>operations/infrastructure/operational-retention#categories</docs>
  public TimeSpan? MaintenanceRuns { get; set; }

  /// <summary>The <see cref="OperationalCategory.CarriedDetail"/> resting period (<c>Whizbang:OperationalRetention:CarriedDetail</c>); null for <see cref="Default"/>.</summary>
  /// <docs>operations/infrastructure/operational-retention#categories</docs>
  public TimeSpan? CarriedDetail { get; set; }

  /// <summary>
  /// Rows removed per table per batch (<c>Whizbang:OperationalRetention:BatchSize</c>, default 1,000), so one statement
  /// never holds locks or writes WAL in proportion to a backlog. Must be at least 1.
  /// </summary>
  /// <docs>operations/infrastructure/operational-retention#configuration</docs>
  public int BatchSize { get; set; } = 1000;

  /// <summary>
  /// Batches one maintenance cycle runs at most (<c>Whizbang:OperationalRetention:MaxBatchesPerCycle</c>, default 10). A
  /// backlog drains over several cycles instead of holding one. Must be at least 1.
  /// </summary>
  /// <docs>operations/infrastructure/operational-retention#configuration</docs>
  public int MaxBatchesPerCycle { get; set; } = 10;

  /// <summary>The resting period of a category: its own override, else <see cref="Default"/>.</summary>
  /// <param name="category">The category.</param>
  /// <returns>The resting period.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/OperationalRetentionOptionsTests.cs:TheDefaultAndACategoryOverride_BindFromConfigurationAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/OperationalRetentionOptionsTests.cs:ACategoryThisReleaseDoesNotDefine_RestsForTheDefault_NotAnotherCategorysOverrideAsync</tests>
  public TimeSpan RetentionFor(OperationalCategory category) => _override(category) ?? Default;

  /// <summary>Every category with its resting period.</summary>
  /// <returns>One entry per category.</returns>
  public IReadOnlyDictionary<OperationalCategory, TimeSpan> RetentionByCategory() {
    var retention = new Dictionary<OperationalCategory, TimeSpan>();
    foreach (var category in Enum.GetValues<OperationalCategory>()) {
      retention[category] = RetentionFor(category);
    }
    return retention;
  }

  internal TimeSpan? _override(OperationalCategory category) => category switch {
    OperationalCategory.RepairLedger => RepairLedger,
    OperationalCategory.RewriteLog => RewriteLog,
    OperationalCategory.Deliveries => Deliveries,
    OperationalCategory.Reports => Reports,
    OperationalCategory.PairHistory => PairHistory,
    OperationalCategory.RecheckRuns => RecheckRuns,
    OperationalCategory.Queues => Queues,
    OperationalCategory.CarriedDetail => CarriedDetail,
    OperationalCategory.MaintenanceRuns => MaintenanceRuns,
    // A value this release does not define has no override of its own, so it rests for the default.
    _ => null,
  };
}

/// <summary>
/// Rejects a non-positive resting period, a non-positive category override and a batch bound below one, at host start,
/// naming the configuration key (#2354).
/// </summary>
/// <docs>operations/infrastructure/operational-retention#configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OperationalRetentionOptionsTests.cs</tests>
public sealed class OperationalRetentionOptionsValidator : IValidateOptions<OperationalRetentionOptions> {
  private const string SECTION = "Whizbang:OperationalRetention";

  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, OperationalRetentionOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    List<string> failures = [];
    if (options.Default <= TimeSpan.Zero) {
      failures.Add($"{SECTION}:Default must be positive (was {options.Default}); a record that rests for no time is removed as it is written.");
    }
    foreach (var category in Enum.GetValues<OperationalCategory>()) {
      if (options._override(category) is { } value && value <= TimeSpan.Zero) {
        failures.Add($"{SECTION}:{category} must be positive when set (was {value}); leave it unset to use {SECTION}:Default.");
      }
    }
    if (options.BatchSize < 1) {
      failures.Add($"{SECTION}:BatchSize must be at least 1 (was {options.BatchSize}); a batch below one removes nothing.");
    }
    if (options.MaxBatchesPerCycle < 1) {
      failures.Add($"{SECTION}:MaxBatchesPerCycle must be at least 1 (was {options.MaxBatchesPerCycle}); a cycle below one batch removes nothing.");
    }
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }
}

/// <summary>What one batch of the operational retention sweep did to one Rolling table (#2354).</summary>
/// <param name="Category">The table's retention category.</param>
/// <param name="Table">The table.</param>
/// <param name="Expired">Rows the batch removed.</param>
/// <param name="Remaining">Rolling rows left in the table after the batch (open rows, which are state, are not counted).</param>
/// <param name="OldestAge">The age of the oldest Rolling row left, or null when none is left.</param>
/// <docs>operations/infrastructure/operational-retention#metrics</docs>
public sealed record OperationalRetentionSweepResult(
  OperationalCategory Category, string Table, int Expired, long Remaining, TimeSpan? OldestAge);
