// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Temporal;

/// <summary>
/// The Active schedules whose fire time passed longer ago than a bound and that no instance has fired yet (#2575), as
/// <see cref="IScheduleClaimer.GetOverdueSchedulesAsync"/> reads them: how many, and which is the oldest.
/// </summary>
/// <param name="Count">How many Active schedules are overdue beyond the bound; zero when none is.</param>
/// <param name="OldestAge">How long ago the oldest one was due, by the database clock; zero when none is overdue.</param>
/// <param name="OldestScheduleId">The oldest overdue schedule's id, or <c>null</c> when none is overdue.</param>
/// <param name="OldestScheduleKey">The oldest overdue schedule's developer key, or <c>null</c> when it has none.</param>
/// <docs>fundamentals/temporal/temporal-engine#a-schedule-that-does-not-fire-is-reported</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Temporal/ScheduleWorkerOverdueTests.cs</tests>
public readonly record struct ScheduleOverdueReading(
  int Count,
  TimeSpan OldestAge,
  Guid? OldestScheduleId,
  string? OldestScheduleKey) {
  /// <summary>The reading when nothing is overdue.</summary>
  public static ScheduleOverdueReading None => default;
}
