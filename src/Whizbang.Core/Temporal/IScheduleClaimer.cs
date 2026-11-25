// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Temporal;

/// <summary>
/// Provider seam for the temporal engine's authoritative fire. An implementation claims due Active
/// schedules this instance owns, or that no live instance holds (#2575), and, per schedule, spawns the occurrence + advances
/// <c>next_fire_at</c> + logs the run in one atomic unit (the Postgres provider calls
/// <c>wh_claim_due_schedules</c>). Returns the number of schedules fired this call.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-patterns</docs>
public interface IScheduleClaimer {
  /// <summary>
  /// Claim and fire up to <paramref name="limit"/> due schedules for this instance. Returns the count
  /// fired (0 when nothing is due). The caller drains by repeating while the count equals the limit.
  /// </summary>
  Task<int> ClaimDueSchedulesAsync(int limit, CancellationToken cancellationToken = default);

  /// <summary>
  /// The earliest upcoming <c>next_fire_at</c> among the Active schedules this instance may claim, or
  /// <c>null</c> when none are pending. Drives the in-memory timer so the worker wakes exactly at the
  /// next fire instead of at backstop latency.
  /// </summary>
  Task<DateTimeOffset?> GetNextFireTimeAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// The Active schedules, whichever instance owns them, whose fire time passed more than <paramref name="bound"/> ago
  /// (#2575): the ones no instance has fired. <see cref="ScheduleOverdueReading.None"/> when there are none, and
  /// <c>null</c> when it cannot be read (no database connection yet), which is not the same answer.
  /// </summary>
  /// <param name="bound">How far past its fire time a schedule must be to count.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The reading, or null when it is unknown.</returns>
  /// <docs>fundamentals/temporal/temporal-engine#a-schedule-that-does-not-fire-is-reported</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PgScheduleClaimerIntegrationTests.cs:GetOverdueSchedules_CountsEveryActiveScheduleOverdueBeyondTheBound_AndNamesTheOldestAsync</tests>
  Task<ScheduleOverdueReading?> GetOverdueSchedulesAsync(TimeSpan bound, CancellationToken cancellationToken = default);
}
