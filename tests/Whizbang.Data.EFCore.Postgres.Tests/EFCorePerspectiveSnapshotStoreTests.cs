using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="EFCorePerspectiveSnapshotStore"/> against real PostgreSQL.
/// Mirrors the Dapper snapshot store tests using the EFCore test base.
/// </summary>
[Category("Integration")]
public class EFCorePerspectiveSnapshotStoreTests : EFCoreTestBase {
  private EFCorePerspectiveSnapshotStore _store = null!;

  [Before(Test)]
  public async Task TestSetupAsync() {
    // Build NpgsqlDataSource from test connection string
    var dataSource = NpgsqlDataSource.Create(ConnectionString);
    _store = new EFCorePerspectiveSnapshotStore(dataSource);
    await Task.CompletedTask;
  }

  #region CreateSnapshotAsync Tests

  [Test]
  public async Task CreateSnapshotAsync_NewSnapshot_InsertsSuccessfullyAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var snapshotEventId = Guid.CreateVersion7();
    using var snapshotData = JsonDocument.Parse("""{"totalOrders": 42, "revenue": 1234.56}""");

    await _store.CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, snapshotData);

    var result = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotEventId).IsEqualTo(snapshotEventId);

    var json = result.Value.SnapshotData.RootElement;
    await Assert.That(json.GetProperty("totalOrders").GetInt32()).IsEqualTo(42);
    await Assert.That(json.GetProperty("revenue").GetDouble()).IsEqualTo(1234.56);
    result.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task CreateSnapshotAsync_DuplicateEventId_UpsertsDataAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var snapshotEventId = Guid.CreateVersion7();

    using var original = JsonDocument.Parse("""{"count": 1}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, original);

    using var updated = JsonDocument.Parse("""{"count": 2}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, updated);

    var result = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotData.RootElement.GetProperty("count").GetInt32()).IsEqualTo(2);
    result.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task CreateSnapshotAsync_MultipleSnapshots_LatestReturnedAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    for (var i = 1; i <= 3; i++) {
      var eventId = Guid.CreateVersion7();
      using var data = JsonDocument.Parse($$$"""{"batch": {{{i}}}}""");
      await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId, data);
    }

    var result = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotData.RootElement.GetProperty("batch").GetInt32()).IsEqualTo(3);
    result.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task CreateSnapshotAsync_DifferentStreams_IsolatedAsync() {
    var stream1 = Guid.CreateVersion7();
    var stream2 = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    using var data1 = JsonDocument.Parse("""{"stream": 1}""");
    using var data2 = JsonDocument.Parse("""{"stream": 2}""");
    await _store.CreateSnapshotAsync(stream1, perspectiveName, Guid.CreateVersion7(), data1);
    await _store.CreateSnapshotAsync(stream2, perspectiveName, Guid.CreateVersion7(), data2);

    var result1 = await _store.GetLatestSnapshotAsync(stream1, perspectiveName);
    var result2 = await _store.GetLatestSnapshotAsync(stream2, perspectiveName);

    await Assert.That(result1!.Value.SnapshotData.RootElement.GetProperty("stream").GetInt32()).IsEqualTo(1);
    await Assert.That(result2!.Value.SnapshotData.RootElement.GetProperty("stream").GetInt32()).IsEqualTo(2);
    result1.Value.SnapshotData.Dispose();
    result2.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task CreateSnapshotAsync_DifferentPerspectives_IsolatedAsync() {
    var streamId = Guid.CreateVersion7();

    using var data1 = JsonDocument.Parse("""{"perspective": "A"}""");
    using var data2 = JsonDocument.Parse("""{"perspective": "B"}""");
    await _store.CreateSnapshotAsync(streamId, "PerspectiveA", Guid.CreateVersion7(), data1);
    await _store.CreateSnapshotAsync(streamId, "PerspectiveB", Guid.CreateVersion7(), data2);

    var resultA = await _store.GetLatestSnapshotAsync(streamId, "PerspectiveA");
    var resultB = await _store.GetLatestSnapshotAsync(streamId, "PerspectiveB");

    await Assert.That(resultA!.Value.SnapshotData.RootElement.GetProperty("perspective").GetString()).IsEqualTo("A");
    await Assert.That(resultB!.Value.SnapshotData.RootElement.GetProperty("perspective").GetString()).IsEqualTo("B");
    resultA.Value.SnapshotData.Dispose();
    resultB.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task CreateSnapshotAsync_ComplexJsonData_PreservedExactlyAsync() {
    var streamId = Guid.CreateVersion7();
    var snapshotEventId = Guid.CreateVersion7();
    using var data = JsonDocument.Parse("""
      {
        "items": [{"sku": "ABC-123", "quantity": 5}, {"sku": "DEF-456", "quantity": 10}],
        "metadata": {"version": 3, "tags": ["urgent", "priority"]},
        "nullableField": null,
        "nested": {"deep": {"value": true}}
      }
    """);

    await _store.CreateSnapshotAsync(streamId, "TestPerspective", snapshotEventId, data);
    var result = await _store.GetLatestSnapshotAsync(streamId, "TestPerspective");

    var root = result!.Value.SnapshotData.RootElement;
    await Assert.That(root.GetProperty("items").GetArrayLength()).IsEqualTo(2);
    await Assert.That(root.GetProperty("items")[0].GetProperty("sku").GetString()).IsEqualTo("ABC-123");
    await Assert.That(root.GetProperty("metadata").GetProperty("tags").GetArrayLength()).IsEqualTo(2);
    await Assert.That(root.GetProperty("nullableField").ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(root.GetProperty("nested").GetProperty("deep").GetProperty("value").GetBoolean()).IsTrue();
    result.Value.SnapshotData.Dispose();
  }

  #endregion

  #region GetLatestSnapshotAsync Tests

  [Test]
  public async Task GetLatestSnapshotAsync_NoSnapshots_ReturnsNullAsync() {
    var result = await _store.GetLatestSnapshotAsync(Guid.CreateVersion7(), "NonExistentPerspective");
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetLatestSnapshotAsync_MultipleSnapshots_ReturnsLatestBySequenceAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();
    var eventId3 = Guid.CreateVersion7();

    using var d1 = JsonDocument.Parse("""{"v": 1}""");
    using var d2 = JsonDocument.Parse("""{"v": 2}""");
    using var d3 = JsonDocument.Parse("""{"v": 3}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId1, d1);
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId2, d2);
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId3, d3);

    var result = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotEventId).IsEqualTo(eventId3);
    await Assert.That(result.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(3);
    result.Value.SnapshotData.Dispose();
  }

  #endregion

  #region GetLatestSnapshotBeforeAsync Tests

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_ReturnsSnapshotBeforeEventAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    var eventId1 = Guid.CreateVersion7();
    await Task.Delay(10);
    var eventId2 = Guid.CreateVersion7();
    await Task.Delay(10);
    var eventId3 = Guid.CreateVersion7();

    using var data1 = JsonDocument.Parse("""{"v": 1}""");
    using var data2 = JsonDocument.Parse("""{"v": 2}""");
    using var data3 = JsonDocument.Parse("""{"v": 3}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId1, data1);
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId2, data2);
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId3, data3);

    // Get latest before eventId3 — should return eventId2's snapshot
    var result = await _store.GetLatestSnapshotBeforeAsync(streamId, perspectiveName, eventId3);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotEventId).IsEqualTo(eventId2);
    await Assert.That(result.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(2);
    result.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_NoQualifyingSnapshot_ReturnsNullAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    var eventId1 = Guid.CreateVersion7();
    using var data = JsonDocument.Parse("""{"v": 1}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId1, data);

    // Ask for snapshot before eventId1 — nothing qualifies
    var result = await _store.GetLatestSnapshotBeforeAsync(streamId, perspectiveName, eventId1);
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_EmptyStore_ReturnsNullAsync() {
    var result = await _store.GetLatestSnapshotBeforeAsync(Guid.CreateVersion7(), "NonExistent", Guid.CreateVersion7());
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_AllSnapshotsAfter_ReturnsNullAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    var beforeEventId = Guid.Parse("00000000-0000-7000-8000-000000000001");
    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();

    using var d1 = JsonDocument.Parse("""{"v": 1}""");
    using var d2 = JsonDocument.Parse("""{"v": 2}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId1, d1);
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId2, d2);

    var result = await _store.GetLatestSnapshotBeforeAsync(streamId, perspectiveName, beforeEventId);
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_MixedSnapshots_ReturnsCorrectOneAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    var eventId1 = Guid.CreateVersion7();
    await Task.Delay(10);
    var eventId2 = Guid.CreateVersion7();
    await Task.Delay(10);
    var beforeEventId = Guid.CreateVersion7();
    await Task.Delay(10);
    var eventId3 = Guid.CreateVersion7();

    using var d1 = JsonDocument.Parse("""{"v": 1}""");
    using var d2 = JsonDocument.Parse("""{"v": 2}""");
    using var d3 = JsonDocument.Parse("""{"v": 3}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId1, d1);
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId2, d2);
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId3, d3);

    var result = await _store.GetLatestSnapshotBeforeAsync(streamId, perspectiveName, beforeEventId);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotEventId).IsEqualTo(eventId2);
    await Assert.That(result.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(2);
    result.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_OnlyOneQualifies_ReturnsThatOneAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    var eventId1 = Guid.CreateVersion7();
    await Task.Delay(10);
    var beforeEventId = Guid.CreateVersion7();
    await Task.Delay(10);
    var eventId2 = Guid.CreateVersion7();

    using var d1 = JsonDocument.Parse("""{"v": 1}""");
    using var d2 = JsonDocument.Parse("""{"v": 2}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId1, d1);
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId2, d2);

    var result = await _store.GetLatestSnapshotBeforeAsync(streamId, perspectiveName, beforeEventId);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotEventId).IsEqualTo(eventId1);
    result.Value.SnapshotData.Dispose();
  }

  #endregion

  #region HasAnySnapshotAsync Tests

  [Test]
  public async Task HasAnySnapshotAsync_WithSnapshots_ReturnsTrueAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    using var data = JsonDocument.Parse("""{"v": 1}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(), data);

    var result = await _store.HasAnySnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task HasAnySnapshotAsync_NoSnapshots_ReturnsFalseAsync() {
    var result = await _store.HasAnySnapshotAsync(Guid.CreateVersion7(), "NonExistent");
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task HasAnySnapshotAsync_DifferentStream_ReturnsFalseAsync() {
    var streamId = Guid.CreateVersion7();
    var otherStreamId = Guid.CreateVersion7();

    using var data = JsonDocument.Parse("""{"v": 1}""");
    await _store.CreateSnapshotAsync(streamId, "TestPerspective", Guid.CreateVersion7(), data);

    var result = await _store.HasAnySnapshotAsync(otherStreamId, "TestPerspective");
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task HasAnySnapshotAsync_DifferentPerspective_ReturnsFalseAsync() {
    var streamId = Guid.CreateVersion7();

    using var data = JsonDocument.Parse("""{"v": 1}""");
    await _store.CreateSnapshotAsync(streamId, "PerspectiveA", Guid.CreateVersion7(), data);

    var result = await _store.HasAnySnapshotAsync(streamId, "PerspectiveB");
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region PruneOldSnapshotsAsync Tests

  [Test]
  public async Task PruneOldSnapshotsAsync_KeepsNewestAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    // Create 5 snapshots with delays to ensure UUID7 ordering
    var eventIds = new Guid[5];
    for (var i = 0; i < 5; i++) {
      eventIds[i] = Guid.CreateVersion7();
      using var data = JsonDocument.Parse($$$"""{"v": {{{i + 1}}}}""");
      await _store.CreateSnapshotAsync(streamId, perspectiveName, eventIds[i], data);
      if (i < 4) {
        await Task.Delay(10);
      }
    }

    // Prune, keeping 2
    await _store.PruneOldSnapshotsAsync(streamId, perspectiveName, keepCount: 2);

    // Only latest 2 should remain
    var latest = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(latest).IsNotNull();
    await Assert.That(latest!.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(5);
    latest.Value.SnapshotData.Dispose();

    // Snapshot at eventId3 (3rd from end) should be gone
    var old = await _store.GetLatestSnapshotBeforeAsync(streamId, perspectiveName, eventIds[3]);
    await Assert.That(old).IsNull()
      .Because("Snapshots before index 3 should have been pruned");
  }

  [Test]
  public async Task PruneOldSnapshotsAsync_FewerThanKeepCount_DeletesNoneAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    using var d1 = JsonDocument.Parse("""{"v": 1}""");
    using var d2 = JsonDocument.Parse("""{"v": 2}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(), d1);
    await _store.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(), d2);

    await _store.PruneOldSnapshotsAsync(streamId, perspectiveName, keepCount: 5);

    await Assert.That(await _store.HasAnySnapshotAsync(streamId, perspectiveName)).IsTrue();
    var latest = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(latest!.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(2);
    latest.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task PruneOldSnapshotsAsync_ExactKeepCount_DeletesNoneAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    for (var i = 0; i < 3; i++) {
      using var data = JsonDocument.Parse($$$"""{"v": {{{i + 1}}}}""");
      await _store.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(), data);
    }

    await _store.PruneOldSnapshotsAsync(streamId, perspectiveName, keepCount: 3);

    var latest = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(latest!.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(3);
    latest.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task PruneOldSnapshotsAsync_NoSnapshots_DoesNotThrowAsync() {
    await _store.PruneOldSnapshotsAsync(Guid.CreateVersion7(), "TestPerspective", keepCount: 5);
  }

  [Test]
  public async Task PruneOldSnapshotsAsync_DoesNotAffectOtherStreamsAsync() {
    var stream1 = Guid.CreateVersion7();
    var stream2 = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    for (var i = 0; i < 3; i++) {
      using var d1 = JsonDocument.Parse($$$"""{"s": 1, "v": {{{i + 1}}}}""");
      using var d2 = JsonDocument.Parse($$$"""{"s": 2, "v": {{{i + 1}}}}""");
      await _store.CreateSnapshotAsync(stream1, perspectiveName, Guid.CreateVersion7(), d1);
      await _store.CreateSnapshotAsync(stream2, perspectiveName, Guid.CreateVersion7(), d2);
    }

    await _store.PruneOldSnapshotsAsync(stream1, perspectiveName, keepCount: 1);

    var s2Latest = await _store.GetLatestSnapshotAsync(stream2, perspectiveName);
    await Assert.That(s2Latest!.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(3);
    s2Latest.Value.SnapshotData.Dispose();
  }

  #endregion

  #region DeleteAllSnapshotsAsync Tests

  [Test]
  public async Task DeleteAllSnapshotsAsync_RemovesAllAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    for (var i = 0; i < 3; i++) {
      using var data = JsonDocument.Parse("""{"v": 1}""");
      await _store.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(), data);
    }

    await _store.DeleteAllSnapshotsAsync(streamId, perspectiveName);

    var result = await _store.HasAnySnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task DeleteAllSnapshotsAsync_DifferentStreams_OnlyDeletesTargetAsync() {
    var stream1 = Guid.CreateVersion7();
    var stream2 = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    using var data = JsonDocument.Parse("""{"v": 1}""");
    await _store.CreateSnapshotAsync(stream1, perspectiveName, Guid.CreateVersion7(), data);
    await _store.CreateSnapshotAsync(stream2, perspectiveName, Guid.CreateVersion7(), data);

    await _store.DeleteAllSnapshotsAsync(stream1, perspectiveName);

    await Assert.That(await _store.HasAnySnapshotAsync(stream1, perspectiveName)).IsFalse();
    await Assert.That(await _store.HasAnySnapshotAsync(stream2, perspectiveName)).IsTrue();
  }

  [Test]
  public async Task DeleteAllSnapshotsAsync_NoSnapshots_DoesNotThrowAsync() {
    await _store.DeleteAllSnapshotsAsync(Guid.CreateVersion7(), "TestPerspective");
  }

  #endregion

  #region CancellationToken Tests

  [Test]
  public async Task CreateSnapshotAsync_CancelledToken_ThrowsAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    using var data = JsonDocument.Parse("""{"v": 1}""");
    async Task Act() => await _store.CreateSnapshotAsync(
      Guid.CreateVersion7(), "Test", Guid.CreateVersion7(), data, cts.Token);

    await Assert.That(Act).ThrowsException();
  }

  [Test]
  public async Task GetLatestSnapshotAsync_CancelledToken_ThrowsAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    async Task Act() => await _store.GetLatestSnapshotAsync(Guid.CreateVersion7(), "Test", cts.Token);
    await Assert.That(Act).ThrowsException();
  }

  #endregion
}
