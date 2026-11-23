// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A released perspective stream goes to the live instance its partition is assigned to (#2545), which may be the
/// instance that released it. Perspective acquisition lets the stream's stored owner claim it whatever its lease, and
/// keeps every other instance out while that owner is live, so a release has to clear the stored owner itself, not only
/// its lease: otherwise the stream went straight back to the live instance that released it, whatever the assignment
/// said.
/// </summary>
/// <remarks>
/// The published partition assignment is the source of ownership: its members are the holder (rank 0) and the sibling
/// (rank 1), so partition 1 is assigned to the sibling and partition 0 to the holder. Every claim presents that
/// assignment. The holder keeps a stream of the sibling's partition by its stored owner, as an instance does with a
/// stream it acquired before the assignment changed.
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop#releasing-unstarted-work</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/563_ReleasedStreamsLeaveTheirLedgerOwner.sql</code-under-test>
[Category("Shard3")]
public class ReleasedStreamFollowsItsAssignmentSqlTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Release.Perspective";
  private static readonly Guid _holder = new("00000000-0000-0000-0000-000000000001");
  private static readonly Guid _sibling = new("ffffffff-0000-0000-0000-000000000002");

  [Test]
  public async Task AReleasedPerspectiveStream_IsClaimedNextByTheLiveInstanceItsPartitionIsAssignedToAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var assignment = await _assignAsync(conn);
    await _holdAsync(conn, _holder, stream, partition: 1);
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    // The premise: while the holder is live and the ledger names it, the sibling cannot take the stream.
    await Assert.That(await _claimAsync(conn, _sibling, assignment)).DoesNotContain(stream);

    var released = await coordinator.ReleaseUnstartedLeasesAsync(_holder, [], [stream]);
    var claimed = await _claimAsync(conn, _sibling, assignment);

    await Assert.That(released.PerspectiveReleased).IsEqualTo(1);
    await Assert.That(claimed).Contains(stream).Because("the released stream's partition is assigned to the sibling, so the sibling claims it next");
    await Assert.That(await _rowHolderAsync(conn, stream)).IsEqualTo(_sibling);
    await Assert.That(await _ledgerOwnerAsync(conn, stream)).IsEqualTo(_sibling);
  }

  [Test]
  public async Task AReleasedPerspectiveStreamWhosePartitionIsAssignedToTheReleaser_ComesBackToItAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var assignment = await _assignAsync(conn);
    await _holdAsync(conn, _holder, stream, partition: 0);
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    _ = await coordinator.ReleaseUnstartedLeasesAsync(_holder, [], [stream]);

    await Assert.That(await _ledgerOwnerAsync(conn, stream)).IsNull().Because("the release ends the ledger ownership");
    await Assert.That(await _claimAsync(conn, _sibling, assignment)).DoesNotContain(stream)
      .Because("the stream's partition is assigned to the releaser, so a release hands it to no one else");
    await Assert.That(await _claimAsync(conn, _holder, assignment)).Contains(stream)
      .Because("the stream's partition is assigned to the releaser, so the releaser claims it again");
  }

  [Test]
  public async Task AReleaseLeavesAStreamItDoesNotNameWithItsOwnerAsync() {
    var named = (Guid)TrackedGuid.New();
    var other = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var assignment = await _assignAsync(conn);
    await _holdAsync(conn, _holder, named, partition: 1);
    await _holdAsync(conn, _holder, other, partition: 1);
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

    _ = await coordinator.ReleaseUnstartedLeasesAsync(_holder, [], [named]);

    await Assert.That(await _ledgerOwnerAsync(conn, other)).IsEqualTo(_holder);
    await Assert.That(await _rowHolderAsync(conn, other)).IsEqualTo(_holder);
    await Assert.That(await _claimAsync(conn, _sibling, assignment)).DoesNotContain(other);
  }

  // --- helpers --------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>
  /// Registers both instances live and publishes the assignment [holder, sibling], the holder as assigner; returns its
  /// version, which every claim presents.
  /// </summary>
  private static async Task<(long Epoch, long Revision)> _assignAsync(NpgsqlConnection conn) {
    await _heartbeatAsync(conn, _holder);
    await _heartbeatAsync(conn, _sibling);
    long epoch;
    await using (var vote = conn.CreateCommand()) {
      vote.CommandText = "SELECT epoch FROM wh_vote_role(@role, @inst, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'";
      vote.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
      vote.Parameters.AddWithValue("inst", _holder);
      epoch = Convert.ToInt64(await vote.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
    await using var publish = conn.CreateCommand();
    publish.CommandText = "SELECT wh_publish_partition_assignment(@role, @inst, @epoch, @members, INTERVAL '90 seconds')";
    publish.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    publish.Parameters.AddWithValue("inst", _holder);
    publish.Parameters.AddWithValue(nameof(epoch), epoch);
    publish.Parameters.AddWithValue("members", new[] { _holder, _sibling });
    return (epoch, Convert.ToInt64(await publish.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
  }

  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, '{}'::jsonb, NULL, NULL, NULL, 'pooled')";
    cmd.Parameters.AddWithValue("inst", instance);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>A stream whose pending perspective row <paramref name="holder"/> leases, its ledger row naming the holder.</summary>
  private static async Task _holdAsync(NpgsqlConnection conn, Guid holder, Guid stream, int partition) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), @stream, @perspective, gen_random_uuid(), @holder, NOW() + INTERVAL '5 minutes', @partition, 0, 1, NOW());
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, lease_expiry)
      VALUES (@stream, @partition, @holder, NOW() + INTERVAL '5 minutes');";
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue("perspective", PERSPECTIVE);
    cmd.Parameters.AddWithValue(nameof(holder), holder);
    cmd.Parameters.AddWithValue(nameof(partition), partition);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<object?> _rowHolderAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_perspective_events WHERE stream_id = @stream";
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    return await cmd.ExecuteScalarAsync();
  }

  private static async Task<Guid?> _ledgerOwnerAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @stream";
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    return await cmd.ExecuteScalarAsync() is Guid owner ? owner : null;
  }

  /// <summary>
  /// Runs claim_work for <paramref name="instance"/> presenting the assignment; returns the perspective streams it holds.
  /// </summary>
  private static async Task<List<Guid>> _claimAsync(NpgsqlConnection conn, Guid instance, (long Epoch, long Revision) assignment) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT work_stream_id FROM claim_work(
      p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
      p_max_streams => 25, p_partition_count => 10000, p_lease_seconds => 300,
      p_assignment_epoch => @epoch, p_assignment_revision => @revision)
      WHERE source = 'perspective_stream'";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("epoch", assignment.Epoch);
    cmd.Parameters.AddWithValue("revision", assignment.Revision);
    var streams = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      streams.Add(reader.GetGuid(0));
    }
    return streams;
  }
}
