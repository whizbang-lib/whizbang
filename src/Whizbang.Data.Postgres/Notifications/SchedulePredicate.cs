// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// #2575: the one SQL predicate the provider's schedule reads use to ask whether an instance may claim a schedule, the
/// same rule <c>wh_claim_due_schedules</c> applies (migration 601). In map mode the owner of the schedule's partition
/// decides (<c>wh_i_own</c>); otherwise the stream's stored owner, or any instance when no live instance holds the
/// stream (<c>wh_schedule_owner_admits</c>). The switch and the live instances are uncorrelated subqueries, so each is
/// read once per statement. The statement aliases <c>wh_schedules</c> as <c>sc</c> and LEFT JOINs
/// <c>wh_active_streams</c> as <c>s</c>. The claim's <c>COALESCE(sc.partition_number, compute_partition(...))</c> is
/// <c>sc.partition_number</c> here: the column is NOT NULL.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine#which-instance-fires-a-schedule</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapClaimSqlTests.Schedules.cs</tests>
internal static class SchedulePredicate {
  /// <summary>The placeholder the caller replaces with its own instance-id parameter.</summary>
  internal const string INSTANCE_PARAMETER = "{instance}";

  /// <summary>The predicate, with <see cref="INSTANCE_PARAMETER"/> standing for the instance id.</summary>
  internal const string OWNED_BY_INSTANCE = @"CASE WHEN (SELECT wh_partition_ownership_by_map())
             THEN wh_i_own(sc.partition_number, {instance}) IS NOT NULL
             ELSE wh_schedule_owner_admits(
               s.assigned_instance_id, {instance}, ARRAY(SELECT l.instance_id FROM wh_live_instance_ids() l)) END";
}
