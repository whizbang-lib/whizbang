// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Migration 334 (#2092): a paced backfill window and its answer's head read one type's stamped events through
/// <c>idx_event_store_type_commit_sequence</c>, and that index is never offered to a commit-sequence probe that names no
/// type, which keeps <c>idx_event_store_commit_sequence</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/334_PacedSubscriptionBackfill.sql</code-under-test>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
[Category("Shard1")]
[Category("Integration")]
public class PacedBackfillIndexTests : EFCoreTestBase {
  private const string INDEX = "idx_event_store_type_commit_sequence";

  private async Task<string> _planAsync(string sql) {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    await using (var off = connection.CreateCommand()) {
      off.CommandText = "SET enable_seqscan = off";
      await off.ExecuteNonQueryAsync();
    }
    await using var explain = connection.CreateCommand();
    explain.CommandText = "EXPLAIN " + sql;
    var lines = new List<string>();
    await using var reader = await explain.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lines.Add(reader.GetString(0));
    }
    return string.Join("\n", lines);
  }

  [Test]
  public async Task AWindowRead_UsesTheTypeCommitSequenceIndex_InOrderAsync() {
    var plan = await _planAsync(
      "SELECT es.event_id FROM wh_event_store es WHERE es.event_type = 'Contracts.Thing' "
      + "AND es.commit_sequence IS NOT NULL AND es.commit_sequence > 1000 ORDER BY es.commit_sequence LIMIT 1000");

    await Assert.That(plan).Contains(INDEX, StringComparison.Ordinal)
      .Because("a window that cannot read its type in commit order fetches and sorts the type's whole remaining history");
    await Assert.That(plan).DoesNotContain("Sort", StringComparison.Ordinal);
  }

  [Test]
  public async Task TheHeadRead_UsesTheTypeCommitSequenceIndexAsync() {
    var plan = await _planAsync(
      "SELECT max(es.commit_sequence) FROM wh_event_store es WHERE es.event_type = 'Contracts.Thing' AND es.commit_sequence IS NOT NULL");

    await Assert.That(plan).Contains(INDEX, StringComparison.Ordinal);
  }

  [Test]
  public async Task AProbeThatNamesNoType_IsNotOfferedTheIndexAsync() {
    var plan = await _planAsync(
      "SELECT 1 FROM wh_event_store es WHERE es.origin_service_id IS NULL "
      + "AND es.commit_sequence >= 1000 AND es.commit_sequence < 2000 LIMIT 1");

    await Assert.That(plan).DoesNotContain(INDEX, StringComparison.Ordinal)
      .Because("its predicate names event_type, which only a query filtering on the type implies");
  }
}
