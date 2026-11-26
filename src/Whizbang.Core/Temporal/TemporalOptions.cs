// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Temporal;

/// <summary>
/// Options for the temporal engine's <see cref="ScheduleWorker"/>. The worker fires due schedules on a
/// <c>ScheduleDueSignal</c> doorbell (the fast path) and reconciles on the backstop interval (catches
/// missed notifies / no-NOTIFY drivers / rebalance staleness).
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public sealed class TemporalOptions {
  /// <summary>Killswitch — when false the worker registers but never fires (ops maintenance window).</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>How often the worker reconciles due schedules absent a doorbell. Default 5 s.</summary>
  public int BackstopIntervalMilliseconds { get; set; } = 5_000;

  /// <summary>Max schedules claimed per call; the worker drains in batches of this size. Default 100.</summary>
  public int ClaimBatchLimit { get; set; } = 100;

  /// <summary>Outbox lease granted to a spawned occurrence for its first publish attempt. Default 300 s.</summary>
  public int LeaseDurationSeconds { get; set; } = 300;

  /// <summary>
  /// How long past its fire time an Active schedule may stay unfired before the worker reports it (#2575): a warning
  /// naming the oldest such schedule and the <see cref="TemporalMetrics"/> gauges. Default 60 s (twelve backstop
  /// intervals). Zero or less turns the check off.
  /// </summary>
  /// <docs>fundamentals/temporal/temporal-engine#a-schedule-that-does-not-fire-is-reported</docs>
  public int OverdueWarningMilliseconds { get; set; } = 60_000;
}
