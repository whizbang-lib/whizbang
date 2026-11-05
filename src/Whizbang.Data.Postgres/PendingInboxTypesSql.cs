// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// The one SQL both Postgres work coordinators use to list the message types of the pending, unleased inbox rows: the
/// rows <c>purge_orphan_inbox</c> may delete, with the same predicate (#2066). The orphan-inbox janitor asks the receive
/// boundary's keep decision about each, so a type kept with no consumer (an absorbed namespace) is never purged.
/// </summary>
/// <docs>operations/workers/orphan-inbox-janitor</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorLifecycleAndJanitorTests.cs:GetPendingInboxMessageTypesAsync_ReturnsTheDistinctTypesOfPendingUnleasedRowsAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperWorkCoordinatorWithDataTests.cs:GetPendingInboxMessageTypesAsync_ReturnsTheDistinctTypesOfPendingRowsAsync</tests>
public static class PendingInboxTypesSql {
  /// <summary>Builds the query for a schema prefix (<c>""</c> or <c>"schema."</c>).</summary>
  /// <param name="schemaPrefix">Schema prefix including the trailing dot, or empty.</param>
  /// <returns>The ready-to-execute query; one <c>message_type</c> column.</returns>
  public static string Query(string schemaPrefix) =>
    "SELECT DISTINCT i.message_type::TEXT FROM " + schemaPrefix + "wh_inbox i JOIN " + schemaPrefix
    + "wh_inbox_state ist ON ist.message_id = i.message_id WHERE ist.processed_at IS NULL AND ist.instance_id IS NULL";
}
