using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phase H step 6 slice 2 regression locks for symmetric perspective ownership.
///
/// Pins three behaviors:
///
/// 1. <c>store_perspective_events</c> populates <c>partition_number</c> via
///    <c>compute_partition(stream_id, partition_count)</c> at insert.
/// 2. <c>claim_orphaned_perspective_events</c> applies partition-modulo selection
///    (<c>partition_number % active_count = instance_rank</c>) for unowned streams.
/// 3. <c>claim_orphaned_perspective_events</c>' OWNER PATH (wh_active_streams pin)
///    overrides the partition-modulo branch — the registered owner always claims.
/// </summary>
/// <docs>fundamentals/work-coordinator/stream-ownership</docs>
public class PerspectiveEventsPartitionSqlTests : EFCoreTestBase {

  [Test]
  public async Task StorePerspectiveEvents_PopulatesPartitionNumberAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT record_heartbeat(@id, 'svc', 'host', 1, '{}'::jsonb)";
      cmd.Parameters.AddWithValue("id", instanceId);
      _ = await cmd.ExecuteScalarAsync();
    }

    var events = $$"""
      [{"StreamId":"{{streamId}}","PerspectiveName":"TestPerspective","EventId":"{{eventId}}"}]
      """;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT * FROM store_perspective_events(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      cmd.Parameters.AddWithValue("p", events);
      cmd.Parameters.AddWithValue("inst", instanceId);
      _ = await cmd.ExecuteScalarAsync();
    }

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT partition_number FROM wh_perspective_events WHERE stream_id = @sid";
    verify.Parameters.AddWithValue("sid", streamId);
    var partition = (int)(await verify.ExecuteScalarAsync())!;

    // Compare to expected
    await using var expected = conn.CreateCommand();
    expected.CommandText = "SELECT compute_partition(@sid, 4)";
    expected.Parameters.AddWithValue("sid", streamId);
    var expectedPartition = (int)(await expected.ExecuteScalarAsync())!;

    await Assert.That(partition).IsEqualTo(expectedPartition)
      .Because("store_perspective_events must populate partition_number via compute_partition(stream_id, partition_count)");
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_PartitionModulo_OnlyClaimsRowsMatchingRankAsync() {
    // 4 instances, partition_count=4. A stream with partition_number=2 should be claimable
    // ONLY by the instance with rank=2.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    // Insert a perspective_event row with explicit partition_number = 2, no instance_id (orphan).
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var workId = Guid.NewGuid();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"INSERT INTO wh_perspective_events
                          (event_work_id, stream_id, perspective_name, event_id,
                           partition_number, status, attempts, created_at)
                          VALUES (@work, @sid, 'TestPerspective', @eid, 2, 0, 0, NOW())";
      cmd.Parameters.AddWithValue("work", workId);
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("eid", eventId);
      await cmd.ExecuteNonQueryAsync();
    }

    var instanceMatching = Guid.NewGuid();   // will claim with rank=2
    var instanceNonMatching = Guid.NewGuid(); // will try rank=1, should not claim

    // Non-matching instance (rank=1, count=4): partition (2 % 4) = 2 != 1, no claim.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"SELECT * FROM claim_orphaned_perspective_events(@inst, NOW() + INTERVAL '5 minutes', NOW(), 500, 1, 4)";
      cmd.Parameters.AddWithValue("inst", instanceNonMatching);
      var rdr = await cmd.ExecuteReaderAsync();
      var nonMatchingRows = 0;
      while (await rdr.ReadAsync()) { nonMatchingRows++; }
      await rdr.DisposeAsync();
      await Assert.That(nonMatchingRows).IsEqualTo(0)
        .Because("partition 2 % 4 = 2 should not match instance with rank 1");
    }

    // Matching instance (rank=2, count=4): partition (2 % 4) = 2 == 2, should claim.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"SELECT * FROM claim_orphaned_perspective_events(@inst, NOW() + INTERVAL '5 minutes', NOW(), 500, 2, 4)";
      cmd.Parameters.AddWithValue("inst", instanceMatching);
      var rdr = await cmd.ExecuteReaderAsync();
      var matchingRows = 0;
      while (await rdr.ReadAsync()) { matchingRows++; }
      await rdr.DisposeAsync();
      await Assert.That(matchingRows).IsEqualTo(1)
        .Because("partition 2 % 4 = 2 should match instance with rank 2");
    }
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_OwnerPath_OverridesPartitionModuloAsync() {
    // wh_active_streams pin always wins. Instance with the wrong rank can still claim if
    // it's the registered owner.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var ownerInstance = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var workId = Guid.NewGuid();

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT record_heartbeat(@id, 'svc', 'host', 1, '{}'::jsonb)";
      cmd.Parameters.AddWithValue("id", ownerInstance);
      _ = await cmd.ExecuteScalarAsync();
    }

    // Pin ownership of the stream to ownerInstance.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at)
                          VALUES (@sid, 2, @inst, NOW())";
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("inst", ownerInstance);
      await cmd.ExecuteNonQueryAsync();
    }

    // Insert perspective_event with partition 2 — modulo would say rank=2 wins, but
    // ownerInstance has rank=0 (only one in alive_set), and the OWNER PATH should override.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"INSERT INTO wh_perspective_events
                          (event_work_id, stream_id, perspective_name, event_id,
                           partition_number, status, attempts, created_at)
                          VALUES (@work, @sid, 'TestPerspective', @eid, 2, 0, 0, NOW())";
      cmd.Parameters.AddWithValue("work", workId);
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("eid", eventId);
      await cmd.ExecuteNonQueryAsync();
    }

    // Owner claims with rank=0, count=4 — partition-modulo (2 % 4)=2 != 0, but owner path wins.
    await using var cmd2 = conn.CreateCommand();
    cmd2.CommandText = @"SELECT * FROM claim_orphaned_perspective_events(@inst, NOW() + INTERVAL '5 minutes', NOW(), 500, 0, 4)";
    cmd2.Parameters.AddWithValue("inst", ownerInstance);
    var rdr = await cmd2.ExecuteReaderAsync();
    var rows = 0;
    while (await rdr.ReadAsync()) { rows++; }
    await Assert.That(rows).IsEqualTo(1)
      .Because("owner path (wh_active_streams pin) must always win, regardless of partition-modulo rank");
  }
}
