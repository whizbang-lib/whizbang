// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A rewind restores the model from a snapshot and replays forward from it, so the snapshot it picks
/// must lie before the triggering event in the stream's own order. Commit sequence does not give that
/// order: a row stamped after the fact carries a sequence far below rows written long before it, so
/// "the latest snapshot below this commit sequence" can name a snapshot that is LATER in the stream.
/// Resuming there skips every event in between and leaves the row permanently short of them.
/// </summary>
/// <remarks>
/// Version is the stream's own counter, unique per stream by index, and cannot invert. The commit
/// sequences here run opposite to the versions so an implementation that answered by commit sequence
/// fails the first assertion rather than passing by coincidence.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePerspectiveSnapshotStore.cs</code-under-test>
[Category("Shard3")]
public class SnapshotSelectionFollowsStreamOrderTests : EFCoreTestBase {
  private const string _perspectiveName = "StreamOrderPerspective";
  private const string _eventType = "Whizbang.Tests.StreamOrderEvent";

  [Test]
  public async Task GetLatestSnapshotBeforeEventInStreamOrder_SnapshotLaterInTheStream_IsNotSelectedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    var (first, second, third) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    await _commitAsync(connection, first, streamId);
    await _commitAsync(connection, second, streamId);
    await _commitAsync(connection, third, streamId);

    // Stream order runs first, second, third. Commit sequence runs the other way, which is what a
    // retroactive stamping pass produces and what makes commit sequence unusable as stream order.
    await _placeAsync(connection, first, version: 1, commitSequence: 500);
    await _placeAsync(connection, second, version: 2, commitSequence: 100);
    await _placeAsync(connection, third, version: 3, commitSequence: 200);

    // One snapshot, taken at the second event.
    await _snapshotAsync(connection, streamId, second, version: 2, commitSequence: 100);

    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    // Typed as the interface: the contract is what a rewind calls through, and the default
    // implementation is the "this store cannot place a snapshot" answer it has to override.
    IPerspectiveSnapshotStore store = new EFCorePerspectiveSnapshotStore(dataSource);

    var beforeFirst = await store.GetLatestSnapshotBeforeEventInStreamOrderAsync(
      streamId, _perspectiveName, first, CancellationToken.None);
    await Assert.That(beforeFirst.HasValue).IsFalse()
      .Because("the only snapshot sits at version 2, which is AFTER the first event in the stream; "
             + "restoring from it and replaying forward would never apply the first event at all. "
             + "Its commit sequence is below the first event's, so answering by commit sequence returns it");

    var beforeThird = await store.GetLatestSnapshotBeforeEventInStreamOrderAsync(
      streamId, _perspectiveName, third, CancellationToken.None);
    // HasValue, not IsNotNull: a nullable tuple boxes, and IsNotNull reads the box rather than the
    // tuple, so it passes on a null result and the test proves nothing.
    await Assert.That(beforeThird.HasValue).IsTrue()
      .Because("the snapshot at version 2 is genuinely before the third event, so a rewind to the "
             + "third event may restore from it and replay the rest. A selection that never returns "
             + "anything is not a fix, it is a permanent full replay");
    await Assert.That(beforeThird!.Value.SnapshotEventId).IsEqualTo(second);
    await Assert.That(beforeThird.Value.SnapshotVersion).IsEqualTo(2);
  }

  [Test]
  public async Task GetLatestSnapshotBeforeEventInStreamOrder_SnapshotRecordingNoVersion_IsNotSelectedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
    await _commitAsync(connection, first, streamId);
    await _commitAsync(connection, second, streamId);
    await _placeAsync(connection, first, version: 1, commitSequence: 10);
    await _placeAsync(connection, second, version: 2, commitSequence: 20);

    // A snapshot written before the version was recorded. Nothing can place it in the stream.
    await _snapshotAsync(connection, streamId, first, version: null, commitSequence: 10);

    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    // Typed as the interface: the contract is what a rewind calls through, and the default
    // implementation is the "this store cannot place a snapshot" answer it has to override.
    IPerspectiveSnapshotStore store = new EFCorePerspectiveSnapshotStore(dataSource);

    var selected = await store.GetLatestSnapshotBeforeEventInStreamOrderAsync(
      streamId, _perspectiveName, second, CancellationToken.None);
    await Assert.That(selected.HasValue).IsFalse()
      .Because("a snapshot that records no version cannot be placed in the stream, so the honest answer "
             + "is that there is no usable snapshot. That costs a fuller replay and reproduces the row; "
             + "guessing it is early enough silently skips events, and a read model short of events has "
             + "nothing pending to repair it");
  }

  [Test]
  public async Task GetLatestSnapshotBeforeEventInStreamOrder_EventTheStoreCannotPlace_FallsBackToEventIdOrderAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    // Nothing is committed, so neither the snapshot's event nor the triggering event has a row in the
    // event store: this models a host whose events live somewhere this store cannot see.
    var streamId = Guid.NewGuid();
    // Written out rather than generated: two UUIDv7 values minted in the same millisecond can land in
    // either order, and this test is about which of two orders the store uses, not about which way a
    // race fell. These two compare the one way in every order a database has.
    var earlier = Guid.Parse("00000000-0000-7000-8000-000000000001");
    var trigger = Guid.Parse("00000000-0000-7000-8000-000000000002");
    await _snapshotAsync(connection, streamId, earlier, version: null, commitSequence: 1);

    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    IPerspectiveSnapshotStore store = new EFCorePerspectiveSnapshotStore(dataSource);

    var selected = await store.GetLatestSnapshotBeforeEventInStreamOrderAsync(
      streamId, _perspectiveName, trigger, CancellationToken.None);
    await Assert.That(selected.HasValue).IsTrue()
      .Because("a store that keeps no version for the event keeps none for any event, so it has no "
             + "retroactive stamping to invert against versions either. Event id order is the only order "
             + "it has and is as sound here as it ever was. Refusing would replay every rewind from event "
             + "zero for a store that never had the defect");
    await Assert.That(selected!.Value.SnapshotEventId).IsEqualTo(earlier);
    await Assert.That(selected.Value.SnapshotVersion).IsNull()
      .Because("the snapshot genuinely records no version, and the result says so rather than inventing one");
  }

  /// <summary>
  /// The rewind's snapshot floor carries how far an archive of the stream reaches when one covers the snapshot, so the
  /// rewind reads through the archive from it; a snapshot nothing archived carries no mark (#1738).
  /// </summary>
  /// <docs>fundamentals/events/archive-store#rewind-past-an-archived-snapshot</docs>
  [Test]
  public async Task GetSnapshotFloor_CarriesTheArchiveMark_WhenAnArchiveCoversTheSnapshotAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var streamId = Guid.NewGuid();
    var (first, second, third) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    await _commitAsync(connection, first, streamId);
    await _commitAsync(connection, second, streamId);
    await _commitAsync(connection, third, streamId);
    await _placeAsync(connection, first, version: 1, commitSequence: 1);
    await _placeAsync(connection, second, version: 2, commitSequence: 2);
    await _placeAsync(connection, third, version: 3, commitSequence: 3);
    await _snapshotAsync(connection, streamId, second, version: 2, commitSequence: 2);
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    IPerspectiveSnapshotStore store = new EFCorePerspectiveSnapshotStore(dataSource);

    var unmarked = await store.GetSnapshotFloorAsync(streamId, _perspectiveName, third, CancellationToken.None);
    await using (var mark = connection.CreateCommand()) {
      mark.CommandText = "UPDATE wh_perspective_snapshots SET archived_through_version = 2 WHERE stream_id = @sid";
      mark.Parameters.AddWithValue("sid", streamId);
      await mark.ExecuteNonQueryAsync();
    }
    var marked = await store.GetSnapshotFloorAsync(streamId, _perspectiveName, third, CancellationToken.None);

    await Assert.That(unmarked!.SnapshotEventId).IsEqualTo(second);
    await Assert.That(unmarked.SnapshotVersion).IsEqualTo(2);
    await Assert.That(unmarked.ArchivedThroughVersion).IsNull().Because("the control: nothing archived covers the snapshot");
    await Assert.That(marked!.ArchivedThroughVersion).IsEqualTo(2L);
    await Assert.That(marked.SnapshotData.RootElement.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Object);
    await Assert.That(await store.GetSnapshotFloorAsync(streamId, _perspectiveName, first, CancellationToken.None)).IsNull()
      .Because("the floor follows the stream order the selection always used: no snapshot sits before the first event");
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _commitAsync(NpgsqlConnection connection, Guid eventId, Guid streamId) {
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}",
        "service_name": "test",
        "host_name": "test-host",
        "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}",
          "Destination": "out-topic",
          "MessageType": "{{_eventType}}",
          "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": true,
          "Flags": 0
        }]
      }
      """);
    _ = await call.ExecuteScalarAsync();
  }

  /// <summary>Places an event in the stream at an explicit version, with a commit sequence of its own.</summary>
  private static async Task _placeAsync(NpgsqlConnection connection, Guid eventId, int version, long commitSequence) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "UPDATE wh_event_store SET version = @v, commit_sequence = @cs WHERE event_id = @id";
    cmd.Parameters.AddWithValue("v", version);
    cmd.Parameters.AddWithValue("cs", commitSequence);
    cmd.Parameters.AddWithValue("id", eventId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _snapshotAsync(
      NpgsqlConnection connection, Guid streamId, Guid snapshotEventId, int? version, long commitSequence) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText =
      "INSERT INTO wh_perspective_snapshots "
      + "(stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_commit_sequence, snapshot_version) "
      + "VALUES (@sid, @pname, @eid, '{}'::jsonb, 1, @cs, @v)";
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("pname", _perspectiveName);
    cmd.Parameters.AddWithValue("eid", snapshotEventId);
    cmd.Parameters.AddWithValue("cs", commitSequence);
    cmd.Parameters.AddWithValue("v", (object?)version ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
  }
}
