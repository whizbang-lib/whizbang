// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// The statement both PostgreSQL drivers read a leaving instance's status with (#2557, graceful leave): the partitions it
/// owns, in either state, when the service owns by map (zero otherwise), and the other live assignment candidates that
/// could take them. Run once a second while an instance leaves, never on a claim.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#graceful-leave</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorNamesTheClaimedEpochTests.cs:TheLeaveStatus_CountsWhatThisInstanceOwns_AndWhoElseCouldTakeItAsync</tests>
public static class PartitionLeaveSql {
  /// <summary>The statement: two integer columns, owned and other live candidates.</summary>
  /// <param name="prefix">The schema as a statement prefix, or empty.</param>
  /// <param name="instanceParameter">The leaving instance's parameter.</param>
  /// <returns>The statement.</returns>
  public static string Status(string prefix, string instanceParameter) =>
    $"SELECT (CASE WHEN {prefix}wh_partition_ownership_by_map() THEN (SELECT count(*) FROM {prefix}wh_partition_ownership o "
    + $"WHERE o.owner_instance_id = {instanceParameter}) ELSE 0 END)::int AS Owned, "
    + $"(SELECT count(*) FROM {prefix}wh_partition_assignment_candidates() c WHERE c.is_live AND c.instance_id <> {instanceParameter})::int "
    + "AS OtherLiveCandidates";
}
