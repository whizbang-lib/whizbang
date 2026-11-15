// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2354: the operational retention sweep on the Dapper driver, with the contract the EF Core driver keeps
/// (<c>OperationalRetentionSweepSqlTests</c>). The maintenance cycle runs on either driver, so the Dapper host must sweep
/// too, or its operational records would grow without bound.
/// </summary>
/// <docs>operations/infrastructure/operational-retention</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.RetentionSweeps.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorRetentionSql.cs</code-under-test>
[Category("Integration")]
public class DapperOperationalRetentionTests : PostgresTestBase {
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Deliberate: the interface is what the maintenance cycle calls through, so an unimplemented member reaches its default here as it does there.")]
  private IWorkCoordinator _coordinator() =>
    new DapperWorkCoordinator(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task TheSweep_RunsOnTheDapperDriverAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.ExecuteAsync("""
      DELETE FROM wh_integrity_origin_rewrites;
      INSERT INTO wh_integrity_origin_rewrites (generation, rewritten_from, rewritten_at)
      VALUES (1, 0, NOW() - INTERVAL '40 days'), (2, 0, NOW() - INTERVAL '1 day');
      """);

    var results = await _coordinator().SweepOperationalRetentionAsync(
      new Dictionary<OperationalCategory, TimeSpan> {
        [OperationalCategory.RewriteLog] = TimeSpan.FromDays(14),
        [OperationalCategory.Queues] = TimeSpan.FromDays(14),
      }, batchSize: 100);

    var log = results.Single(r => r.Table == "wh_integrity_origin_rewrites");
    await Assert.That(log.Category).IsEqualTo(OperationalCategory.RewriteLog);
    await Assert.That(log.Expired).IsEqualTo(1).Because("the generation older than the resting period goes, as on EF Core");
    await Assert.That(log.Remaining).IsEqualTo(1L);
    await Assert.That(log.OldestAge!.Value).IsGreaterThan(TimeSpan.FromHours(23));
    var queues = results.Single(r => r.Table == "wh_archive_restore_requests");
    await Assert.That(queues.Remaining).IsEqualTo(0L);
    await Assert.That(queues.OldestAge).IsNull().Because("an empty table has no oldest row");
  }
}
