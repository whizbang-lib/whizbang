// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// #2463, the store's half of the tip snapshot: a perspective that applies both ephemeral and durable events keeps one
/// tip beside its floors. The tip replaces the previous tip, is kept only above the newest floor, never demotes a floor
/// written at its event, and is never returned by the floor lookup; pruning counts floors alone and removes a tip at or
/// below the newest floor; a late event removes a tip at or above it. The Dapper store is held to the same rules
/// (DapperTipSnapshotStoreTests).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#the-tip-snapshot</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePerspectiveSnapshotStore.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/TipSnapshotSql.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class TipSnapshotStoreTests : EFCoreTestBase {
  private const string PERSPECTIVE = "TipSnapshotPerspective";

  [Test]
  public async Task ATip_ReplacesThePreviousTip_AndLeavesTheFloorAsync() {
    var (store, stream, events) = await _streamAsync(4);
    await _floorAsync(store, stream, events[0]);

    await _tipAsync(store, stream, events[1]);
    await _tipAsync(store, stream, events[2]);

    await Assert.That(await _kindsAsync(stream)).IsEquivalentTo(["1:floor", "3:tip"])
      .Because("the stream keeps one tip, the newest, and a tip never touches a floor");
  }

  [Test]
  public async Task ATip_AtOrBelowTheNewestFloor_IsNotKept_AndNeverDemotesTheFloorAsync() {
    var (store, stream, events) = await _streamAsync(4);
    await _floorAsync(store, stream, events[2]);

    await _tipAsync(store, stream, events[1]);
    await _tipAsync(store, stream, events[2]);

    await Assert.That(await _kindsAsync(stream)).IsEquivalentTo(["3:floor"])
      .Because("bodies at or below the floor may be reaped, so a replay from a tip there could cross them");
  }

  [Test]
  public async Task ATip_AtAnEventTheStoreCannotPlace_IsNotKeptAsync() {
    var (store, stream, _) = await _streamAsync(1);

    await _tipAsync(store, stream, TrackedGuid.New().Value);

    await Assert.That(await _kindsAsync(stream)).IsEmpty()
      .Because("a tip with no stream version cannot be ordered against the floor or a late event");
  }

  [Test]
  public async Task AFloorWrittenAtTheTipsEvent_TakesItsPlaceAsync() {
    var (store, stream, events) = await _streamAsync(4);
    await _tipAsync(store, stream, events[2]);

    await _floorAsync(store, stream, events[2]);

    await Assert.That(await _kindsAsync(stream)).IsEquivalentTo(["3:floor"])
      .Because("the reap-boundary bootstrap's snapshot is a floor wherever it lands");
  }

  [Test]
  public async Task TheFloorLookup_NeverReturnsATip_WhileTheRewindsLookupDoesAsync() {
    var (store, stream, events) = await _streamAsync(4);
    await _floorAsync(store, stream, events[0]);
    await _tipAsync(store, stream, events[2]);

    var newest = await store.GetSnapshotFloorAsync(stream, PERSPECTIVE, events[3]);
    var floor = await store.GetFloorSnapshotAsync(stream, PERSPECTIVE, events[3]);
    var latest = await store.GetLatestSnapshotFloorAsync(stream, PERSPECTIVE);

    await Assert.That(newest!.SnapshotEventId).IsEqualTo(events[2]).Because("a rewind restores the newest snapshot before it");
    await Assert.That(newest.IsTip).IsTrue();
    await Assert.That(floor!.SnapshotEventId).IsEqualTo(events[0])
      .Because("the floor lookup, which the bootstrap and a refused tip read, sees floors alone");
    await Assert.That(floor.IsTip).IsFalse();
    await Assert.That(latest!.IsTip).IsTrue().Because("the newest snapshot says what it is too");
    var unplaced = await store.GetFloorSnapshotAsync(stream, PERSPECTIVE, TrackedGuid.New().Value);
    await Assert.That(unplaced!.SnapshotEventId).IsEqualTo(events[0])
      .Because("before an event the store cannot place, the floors are ordered by event id, and a tip is still not one");
  }

  [Test]
  public async Task Prune_CountsFloorsAlone_AndRemovesATipAtOrBelowTheNewestFloorAsync() {
    var (store, stream, events) = await _streamAsync(4);
    await _floorAsync(store, stream, events[0]);
    await _tipAsync(store, stream, events[3]);
    await _floorAsync(store, stream, events[1]);

    await store.PruneOldSnapshotsAsync(stream, PERSPECTIVE, keepCount: 1);

    await Assert.That(await _kindsAsync(stream)).IsEquivalentTo(["2:floor", "4:tip"])
      .Because("the tip is not one of the floors the retention counts, and it sits above the newest floor");

    await _floorAsync(store, stream, events[3]);
    await store.PruneOldSnapshotsAsync(stream, PERSPECTIVE, keepCount: 1);

    await Assert.That(await _kindsAsync(stream)).IsEquivalentTo(["4:floor"]);
    await _tipAsync(store, stream, events[2]);
    await _floorAsync(store, stream, events[1]);
    await store.PruneOldSnapshotsAsync(stream, PERSPECTIVE, keepCount: 5);
    await Assert.That(await _kindsAsync(stream)).IsEquivalentTo(["2:floor", "4:floor"]).Because("the control: no tip was kept");
  }

  [Test]
  public async Task Prune_RemovesATipAtOrBelowAFloorWrittenAboveItAsync() {
    var (store, stream, events) = await _streamAsync(4);
    await _tipAsync(store, stream, events[1]);
    await _floorAsync(store, stream, events[2]);

    await store.PruneOldSnapshotsAsync(stream, PERSPECTIVE, keepCount: 1);

    await Assert.That(await _kindsAsync(stream)).IsEquivalentTo(["3:floor"])
      .Because("a tip the new floor passed can never be restored: the bodies between them may be reaped");
  }

  [Test]
  public async Task ALateEvent_RemovesATipAtOrAboveIt_AndKeepsOneBelowIt_AndEveryFloorAsync() {
    var (store, stream, events) = await _streamAsync(4);
    await _floorAsync(store, stream, events[2]);
    await _tipAsync(store, stream, events[3]);

    await store.DeleteTipSnapshotFromAsync(stream, PERSPECTIVE, events[3]);
    await Assert.That(await _kindsAsync(stream)).IsEquivalentTo(["3:floor"])
      .Because("a tip at the late event's position was taken without it");

    await _tipAsync(store, stream, events[3]);
    var later = await _appendAsync(stream, version: 5);
    await store.DeleteTipSnapshotFromAsync(stream, PERSPECTIVE, later);
    await Assert.That(await _kindsAsync(stream)).IsEquivalentTo(["3:floor", "4:tip"]).Because("a tip below the late event holds it");

    await store.DeleteTipSnapshotFromAsync(stream, PERSPECTIVE, events[0]);
    await Assert.That(await _kindsAsync(stream)).IsEquivalentTo(["3:floor"]).Because("a late event never removes a floor");
  }

  [Test]
  public async Task ALateEventTheStoreCannotPlace_RemovesTheTipAsync() {
    var (store, stream, events) = await _streamAsync(2);
    await _tipAsync(store, stream, events[1]);

    await store.DeleteTipSnapshotFromAsync(stream, PERSPECTIVE, TrackedGuid.New().Value);

    await Assert.That(await _kindsAsync(stream)).IsEmpty()
      .Because("a tip that cannot be judged against the late event is given up, the safe direction");
  }

  // ---------------------------------------------------------------------------------------------

  private async Task<(IPerspectiveSnapshotStore Store, Guid Stream, Guid[] Events)> _streamAsync(int count) {
    var stream = TrackedGuid.New().Value;
    var events = new Guid[count];
    for (var i = 0; i < count; i++) {
      events[i] = await _appendAsync(stream, version: i + 1);
    }
    var dataSource = NpgsqlDataSource.Create(ConnectionString);
    return (new EFCorePerspectiveSnapshotStore(dataSource), stream, events);
  }

  private async Task<Guid> _appendAsync(Guid stream, int version) {
    var id = TrackedGuid.New().Value;
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence)
      VALUES (@id, @stream, @stream, 'TipSnapshotAggregate', 'Whizbang.Tests.TipSnapshotEvent', @version, @version)
      """;
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue(nameof(version), version);
    await cmd.ExecuteNonQueryAsync();
    return id;
  }

  private static async Task _floorAsync(IPerspectiveSnapshotStore store, Guid stream, Guid at) {
    using var data = JsonDocument.Parse("""{"kind":"floor"}""");
    await store.CreateSnapshotAsync(stream, PERSPECTIVE, at, null, data, collectivesThroughSequence: null);
  }

  private static async Task _tipAsync(IPerspectiveSnapshotStore store, Guid stream, Guid at) {
    using var data = JsonDocument.Parse("""{"kind":"tip"}""");
    await store.CreateTipSnapshotAsync(stream, PERSPECTIVE, at, null, data, collectivesThroughSequence: 7);
  }

  // Each snapshot of the stream as "version:kind", in version order.
  private async Task<List<string>> _kindsAsync(Guid stream) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
      SELECT snapshot_version, is_tip FROM wh_perspective_snapshots
      WHERE stream_id = @s AND perspective_name = @p ORDER BY snapshot_version
      """;
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", PERSPECTIVE);
    var kinds = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      kinds.Add(string.Create(CultureInfo.InvariantCulture, $"{reader.GetInt32(0)}:{(reader.GetBoolean(1) ? "tip" : "floor")}"));
    }
    return kinds;
  }
}
