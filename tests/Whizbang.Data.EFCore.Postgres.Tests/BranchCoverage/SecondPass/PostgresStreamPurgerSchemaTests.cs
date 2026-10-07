// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// The stream purge with and without a schema in one class: with none it resolves the tables through
/// the connection's search path, with one it qualifies every name by it, so a schema that does not
/// exist fails instead of quietly purging the search path's tables.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresStreamPurger.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class PostgresStreamPurgerSchemaTests : EFCoreTestBase {

  [Test]
  [Timeout(60000)]
  public async Task DryRun_WithoutAndWithTheSchema_CountsTheSameRowsAsync(CancellationToken cancellationToken) {
    var stream = Guid.CreateVersion7();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using (var seed = new NpgsqlCommand(@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      VALUES (@s, 0, @i, NOW(), NOW());", conn)) {
      seed.Parameters.AddWithValue("s", stream);
      seed.Parameters.AddWithValue("i", Guid.NewGuid());
      await seed.ExecuteNonQueryAsync(cancellationToken);
    }

    var searchPath = await PostgresStreamPurger.RunAsync(conn, schema: null, _dryRun(stream), cancellationToken: cancellationToken);
    var qualified = await PostgresStreamPurger.RunAsync(conn, schema: "public", _dryRun(stream), cancellationToken: cancellationToken);

    await Assert.That(searchPath.Totals["wh_active_streams"]).IsEqualTo(1L);
    await Assert.That(qualified.Totals["wh_active_streams"]).IsEqualTo(1L)
      .Because("the same tables, named through their schema, hold the same row");
  }

  [Test]
  [Timeout(60000)]
  public async Task Run_WithASchemaThatDoesNotExist_FailsAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    await Assert.That(async () => await PostgresStreamPurger.RunAsync(
        conn, schema: "no_such_schema", _dryRun(Guid.CreateVersion7()), cancellationToken: cancellationToken))
      .Throws<PostgresException>()
      .Because("every name is qualified by the schema given, so a missing schema cannot fall back to the search path");
  }

  private static StreamPurgeRequest _dryRun(Guid stream) => new() {
    StreamIds = [stream],
    RequestedBy = "operator-1",
    Reason = "counting first",
    DryRun = true,
  };
}
