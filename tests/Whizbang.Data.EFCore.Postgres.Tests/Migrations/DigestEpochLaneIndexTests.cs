using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// That the digest-epoch probes for a foreign lane can be served by an index.
/// </summary>
/// <remarks>
/// <para>
/// Epoch closure (migration 092) probes each foreign lane with
/// <c>origin_service_id = X AND origin_commit_sequence BETWEEN ... AND created_at >= ...</c>. The
/// event store had no index over those columns, so each probe was a parallel sequential scan of
/// the whole table, about twenty per maintenance tick. Under a bulk load that was two backends
/// scanning a multi-million-row table continuously for the length of the load.
/// </para>
/// <para>
/// The planner prefers a sequential scan on a small table whatever the indexes, so the test asks
/// the question the other way round: with sequential scans made prohibitively expensive, an index
/// that can serve the probe is chosen, and one that cannot leaves the planner with no choice but
/// the scan it was told to avoid.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Shard1")]
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class DigestEpochLaneIndexTests : EFCoreTestBase {
  private const string INDEX = "idx_event_store_origin_lane";

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

  /// <summary>The index the probes need exists, partial on the rows that have a foreign lane.</summary>
  [Test]
  public async Task TheLaneIndexExistsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT indexdef FROM pg_indexes WHERE tablename = 'wh_event_store' AND indexname = @name";
    cmd.Parameters.AddWithValue("name", INDEX);
    var definition = await cmd.ExecuteScalarAsync() as string;

    await Assert.That(definition).IsNotNull().Because("the lane probe has no other index to use");
    await Assert.That(definition!).Contains("(origin_service_id, origin_commit_sequence", StringComparison.Ordinal);
    await Assert.That(definition!).Contains("WHERE (origin_service_id IS NOT NULL)", StringComparison.Ordinal)
      .Because("own-lane rows are served by the commit_sequence index and would only widen this one");
  }

  /// <summary>The per-lane probe shape from migration 092 is served by the lane index.</summary>
  [Test]
  public async Task TheLaneProbeUsesTheIndexAsync() {
    var plan = await _planAsync(
      "SELECT 1 FROM wh_event_store es WHERE es.origin_service_id = '11111111-1111-1111-1111-111111111111' "
      + "AND es.origin_commit_sequence >= 1000 AND es.origin_commit_sequence < 2000 "
      + "AND es.created_at >= NOW() - INTERVAL '1 hour' LIMIT 1");

    await Assert.That(plan).Contains(INDEX, StringComparison.Ordinal)
      .Because("a probe the planner cannot serve from an index scans the event store whole, once per lane per epoch");
    await Assert.That(plan).DoesNotContain("Seq Scan", StringComparison.Ordinal);
  }

  /// <summary>The own-lane probe keeps using the commit_sequence index; the new one does not replace it.</summary>
  [Test]
  public async Task TheOwnLaneProbeStillUsesTheCommitSequenceIndexAsync() {
    var plan = await _planAsync(
      "SELECT 1 FROM wh_event_store es WHERE es.origin_service_id IS NULL "
      + "AND es.commit_sequence >= 1000 AND es.commit_sequence < 2000 "
      + "AND es.created_at >= NOW() - INTERVAL '1 hour' LIMIT 1");

    await Assert.That(plan).Contains("idx_event_store_commit_sequence", StringComparison.Ordinal);
  }
}
