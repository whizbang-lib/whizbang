using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

// NOTE: event ids here use TrackedGuid.NewMedo().Value (the same generator Whizbang uses for message/event
// ids) rather than TrackedGuid.NewMedo().Value. Medo's UUIDv7 uses a MONOTONIC sub-millisecond counter, so ids
// created in the same millisecond sort in creation order in PostgreSQL. Raw TrackedGuid.NewMedo().Value uses RANDOM
// sub-millisecond bits, so a burst of ids sorts randomly under Postgres `uuid` comparison — which made the
// ordering-sensitive tests (GetLatestSnapshotBefore / prune) flaky. Do not swap these back to CreateVersion7().

/// <summary>
/// Integration tests for <see cref="DapperPerspectiveSnapshotStore"/> against real PostgreSQL.
/// Tests CRUD operations, pruning, and edge cases for perspective snapshot storage.
/// </summary>
[Category("Integration")]
public class DapperPerspectiveSnapshotStoreTests : IDisposable {
  private TestFixture _testBase = null!;
  private DapperPerspectiveSnapshotStore _store = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
    _store = new DapperPerspectiveSnapshotStore(_testBase.TestConnectionString);
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    GC.SuppressFinalize(this);
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _testBase.DisposeAsync();
  }

  #region CreateSnapshotAsync Tests

  [Test]
  public async Task CreateSnapshotAsync_NewSnapshot_InsertsSuccessfullyAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";
    var snapshotEventId = TrackedGuid.NewMedo().Value;
    var snapshotData = JsonDocument.Parse("""{"totalOrders": 42, "revenue": 1234.56}""");

    await _store.CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, snapshotData);

    var result = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotEventId).IsEqualTo(snapshotEventId);

    var json = result.Value.SnapshotData.RootElement;
    await Assert.That(json.GetProperty("totalOrders").GetInt32()).IsEqualTo(42);
    await Assert.That(json.GetProperty("revenue").GetDouble()).IsEqualTo(1234.56);

    result.Value.SnapshotData.Dispose();
    snapshotData.Dispose();
  }

  [Test]
  public async Task CreateSnapshotAsync_DuplicateEventId_UpsertsDataAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";
    var snapshotEventId = TrackedGuid.NewMedo().Value;

    var original = JsonDocument.Parse("""{"count": 1}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, original);

    var updated = JsonDocument.Parse("""{"count": 2}""");
    await _store.CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, updated);

    var result = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotData.RootElement.GetProperty("count").GetInt32()).IsEqualTo(2);

    result.Value.SnapshotData.Dispose();
    original.Dispose();
    updated.Dispose();
  }

  [Test]
  public async Task CreateSnapshotAsync_MultipleSnapshots_IncreasesSequenceNumberAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    for (var i = 1; i <= 3; i++) {
      var eventId = TrackedGuid.NewMedo().Value;
      var data = JsonDocument.Parse($$$"""{"batch": {{{i}}}}""");
      await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId, data);
      data.Dispose();
    }

    // Latest should be batch 3
    var result = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotData.RootElement.GetProperty("batch").GetInt32()).IsEqualTo(3);
    result.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task CreateSnapshotAsync_DifferentStreams_IsolatedAsync() {
    var stream1 = TrackedGuid.NewMedo().Value;
    var stream2 = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    var data1 = JsonDocument.Parse("""{"stream": 1}""");
    var data2 = JsonDocument.Parse("""{"stream": 2}""");
    await _store.CreateSnapshotAsync(stream1, perspectiveName, TrackedGuid.NewMedo().Value, data1);
    await _store.CreateSnapshotAsync(stream2, perspectiveName, TrackedGuid.NewMedo().Value, data2);

    var result1 = await _store.GetLatestSnapshotAsync(stream1, perspectiveName);
    var result2 = await _store.GetLatestSnapshotAsync(stream2, perspectiveName);

    await Assert.That(result1!.Value.SnapshotData.RootElement.GetProperty("stream").GetInt32()).IsEqualTo(1);
    await Assert.That(result2!.Value.SnapshotData.RootElement.GetProperty("stream").GetInt32()).IsEqualTo(2);

    result1.Value.SnapshotData.Dispose();
    result2.Value.SnapshotData.Dispose();
    data1.Dispose();
    data2.Dispose();
  }

  [Test]
  public async Task CreateSnapshotAsync_DifferentPerspectives_IsolatedAsync() {
    var streamId = TrackedGuid.NewMedo().Value;

    var data1 = JsonDocument.Parse("""{"perspective": "A"}""");
    var data2 = JsonDocument.Parse("""{"perspective": "B"}""");
    await _store.CreateSnapshotAsync(streamId, "PerspectiveA", TrackedGuid.NewMedo().Value, data1);
    await _store.CreateSnapshotAsync(streamId, "PerspectiveB", TrackedGuid.NewMedo().Value, data2);

    var resultA = await _store.GetLatestSnapshotAsync(streamId, "PerspectiveA");
    var resultB = await _store.GetLatestSnapshotAsync(streamId, "PerspectiveB");

    await Assert.That(resultA!.Value.SnapshotData.RootElement.GetProperty("perspective").GetString()).IsEqualTo("A");
    await Assert.That(resultB!.Value.SnapshotData.RootElement.GetProperty("perspective").GetString()).IsEqualTo("B");

    resultA.Value.SnapshotData.Dispose();
    resultB.Value.SnapshotData.Dispose();
    data1.Dispose();
    data2.Dispose();
  }

  [Test]
  public async Task CreateSnapshotAsync_ComplexJsonData_PreservedExactlyAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    var snapshotEventId = TrackedGuid.NewMedo().Value;
    var data = JsonDocument.Parse("""
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
    data.Dispose();
  }

  #endregion

  #region GetLatestSnapshotAsync Tests

  [Test]
  public async Task GetLatestSnapshotAsync_NoSnapshots_ReturnsNullAsync() {
    var result = await _store.GetLatestSnapshotAsync(TrackedGuid.NewMedo().Value, "NonExistentPerspective");
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetLatestSnapshotAsync_MultipleSnapshots_ReturnsLatestBySequenceAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    var eventId1 = TrackedGuid.NewMedo().Value;
    var eventId2 = TrackedGuid.NewMedo().Value;
    var eventId3 = TrackedGuid.NewMedo().Value;

    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId1, JsonDocument.Parse("""{"v": 1}"""));
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId2, JsonDocument.Parse("""{"v": 2}"""));
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId3, JsonDocument.Parse("""{"v": 3}"""));

    var result = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotEventId).IsEqualTo(eventId3);
    await Assert.That(result.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(3);

    result.Value.SnapshotData.Dispose();
  }

  #endregion

  #region GetLatestSnapshotBeforeAsync Tests

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_NoSnapshots_ReturnsNullAsync() {
    var result = await _store.GetLatestSnapshotBeforeAsync(
      TrackedGuid.NewMedo().Value, "TestPerspective", TrackedGuid.NewMedo().Value);
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_AllSnapshotsAfter_ReturnsNullAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    // Create a "before" event ID first (smaller UUID7)
    // Use Guid.Empty-like minimum to guarantee it's before any UUID7
    var beforeEventId = Guid.Parse("00000000-0000-7000-8000-000000000001");
    // Snapshot event IDs will be UUID7 (time-based, much larger)
    var eventId1 = TrackedGuid.NewMedo().Value;
    var eventId2 = TrackedGuid.NewMedo().Value;

    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId1, JsonDocument.Parse("""{"v": 1}"""));
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId2, JsonDocument.Parse("""{"v": 2}"""));

    var result = await _store.GetLatestSnapshotBeforeAsync(streamId, perspectiveName, beforeEventId);
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_MixedSnapshots_ReturnsCorrectOneAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    // Create snapshots with guaranteed increasing event IDs using delays
    var eventId1 = TrackedGuid.NewMedo().Value;
    await Task.Delay(10);
    var eventId2 = TrackedGuid.NewMedo().Value;
    await Task.Delay(10);
    var beforeEventId = TrackedGuid.NewMedo().Value; // The "late event"
    await Task.Delay(10);
    var eventId3 = TrackedGuid.NewMedo().Value;

    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId1, JsonDocument.Parse("""{"v": 1}"""));
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId2, JsonDocument.Parse("""{"v": 2}"""));
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId3, JsonDocument.Parse("""{"v": 3}"""));

    // Should return snapshot at eventId2 (latest before beforeEventId)
    var result = await _store.GetLatestSnapshotBeforeAsync(streamId, perspectiveName, beforeEventId);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotEventId).IsEqualTo(eventId2);
    await Assert.That(result.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(2);

    result.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_OnlyOneQualifies_ReturnsThatOneAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    var eventId1 = TrackedGuid.NewMedo().Value;
    await Task.Delay(10);
    var beforeEventId = TrackedGuid.NewMedo().Value;
    await Task.Delay(10);
    var eventId2 = TrackedGuid.NewMedo().Value;

    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId1, JsonDocument.Parse("""{"v": 1}"""));
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId2, JsonDocument.Parse("""{"v": 2}"""));

    var result = await _store.GetLatestSnapshotBeforeAsync(streamId, perspectiveName, beforeEventId);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.SnapshotEventId).IsEqualTo(eventId1);

    result.Value.SnapshotData.Dispose();
  }

  #endregion

  #region HasAnySnapshotAsync Tests

  [Test]
  public async Task HasAnySnapshotAsync_NoSnapshots_ReturnsFalseAsync() {
    var result = await _store.HasAnySnapshotAsync(TrackedGuid.NewMedo().Value, "TestPerspective");
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task HasAnySnapshotAsync_OneSnapshot_ReturnsTrueAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "TestPerspective";

    await _store.CreateSnapshotAsync(streamId, perspectiveName, TrackedGuid.NewMedo().Value,
      JsonDocument.Parse("""{"v": 1}"""));

    var result = await _store.HasAnySnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task HasAnySnapshotAsync_DifferentStream_ReturnsFalseAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    var otherStreamId = TrackedGuid.NewMedo().Value;

    await _store.CreateSnapshotAsync(streamId, "TestPerspective", TrackedGuid.NewMedo().Value,
      JsonDocument.Parse("""{"v": 1}"""));

    var result = await _store.HasAnySnapshotAsync(otherStreamId, "TestPerspective");
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task HasAnySnapshotAsync_DifferentPerspective_ReturnsFalseAsync() {
    var streamId = TrackedGuid.NewMedo().Value;

    await _store.CreateSnapshotAsync(streamId, "PerspectiveA", TrackedGuid.NewMedo().Value,
      JsonDocument.Parse("""{"v": 1}"""));

    var result = await _store.HasAnySnapshotAsync(streamId, "PerspectiveB");
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region PruneOldSnapshotsAsync Tests

  [Test]
  public async Task PruneOldSnapshotsAsync_FewerThanKeepCount_DeletesNoneAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    await _store.CreateSnapshotAsync(streamId, perspectiveName, TrackedGuid.NewMedo().Value,
      JsonDocument.Parse("""{"v": 1}"""));
    await _store.CreateSnapshotAsync(streamId, perspectiveName, TrackedGuid.NewMedo().Value,
      JsonDocument.Parse("""{"v": 2}"""));

    await _store.PruneOldSnapshotsAsync(streamId, perspectiveName, keepCount: 5);

    // Both should still exist
    await Assert.That(await _store.HasAnySnapshotAsync(streamId, perspectiveName)).IsTrue();
    var latest = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(latest!.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(2);
    latest.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task PruneOldSnapshotsAsync_MoreThanKeepCount_DeletesOldestAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    // Create 5 snapshots
    var eventIds = new Guid[5];
    for (var i = 0; i < 5; i++) {
      eventIds[i] = TrackedGuid.NewMedo().Value;
      await _store.CreateSnapshotAsync(streamId, perspectiveName, eventIds[i],
        JsonDocument.Parse($$$"""{"v": {{{i + 1}}}}"""));
    }

    // Keep only 2 — should delete the 3 oldest
    await _store.PruneOldSnapshotsAsync(streamId, perspectiveName, keepCount: 2);

    // Latest should still be v5
    var latest = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(latest!.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(5);
    latest.Value.SnapshotData.Dispose();

    // The oldest snapshots (before eventIds[3]) should be gone
    var beforeOldest = await _store.GetLatestSnapshotBeforeAsync(streamId, perspectiveName, eventIds[3]);
    await Assert.That(beforeOldest).IsNull();
  }

  [Test]
  public async Task PruneOldSnapshotsAsync_ExactKeepCount_DeletesNoneAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    for (var i = 0; i < 3; i++) {
      await _store.CreateSnapshotAsync(streamId, perspectiveName, TrackedGuid.NewMedo().Value,
        JsonDocument.Parse($$$"""{"v": {{{i + 1}}}}"""));
    }

    await _store.PruneOldSnapshotsAsync(streamId, perspectiveName, keepCount: 3);

    var latest = await _store.GetLatestSnapshotAsync(streamId, perspectiveName);
    await Assert.That(latest!.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(3);
    latest.Value.SnapshotData.Dispose();
  }

  [Test]
  public async Task PruneOldSnapshotsAsync_NoSnapshots_DoesNotThrowAsync() {
    // Should not throw when there are no snapshots
    await _store.PruneOldSnapshotsAsync(TrackedGuid.NewMedo().Value, "TestPerspective", keepCount: 5);
  }

  [Test]
  public async Task PruneOldSnapshotsAsync_DoesNotAffectOtherStreamsAsync() {
    var stream1 = TrackedGuid.NewMedo().Value;
    var stream2 = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    // Create 3 snapshots for each stream
    for (var i = 0; i < 3; i++) {
      await _store.CreateSnapshotAsync(stream1, perspectiveName, TrackedGuid.NewMedo().Value,
        JsonDocument.Parse($$$"""{"s": 1, "v": {{{i + 1}}}}"""));
      await _store.CreateSnapshotAsync(stream2, perspectiveName, TrackedGuid.NewMedo().Value,
        JsonDocument.Parse($$$"""{"s": 2, "v": {{{i + 1}}}}"""));
    }

    // Prune stream1 to keep 1
    await _store.PruneOldSnapshotsAsync(stream1, perspectiveName, keepCount: 1);

    // Stream2 should still have all 3
    var s2Latest = await _store.GetLatestSnapshotAsync(stream2, perspectiveName);
    await Assert.That(s2Latest!.Value.SnapshotData.RootElement.GetProperty("v").GetInt32()).IsEqualTo(3);
    s2Latest.Value.SnapshotData.Dispose();
  }

  #endregion

  #region DeleteAllSnapshotsAsync Tests

  [Test]
  public async Task DeleteAllSnapshotsAsync_WithSnapshots_RemovesAllAsync() {
    var streamId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    for (var i = 0; i < 3; i++) {
      await _store.CreateSnapshotAsync(streamId, perspectiveName, TrackedGuid.NewMedo().Value,
        JsonDocument.Parse("""{"v": 1}"""));
    }

    await _store.DeleteAllSnapshotsAsync(streamId, perspectiveName);

    var hasAny = await _store.HasAnySnapshotAsync(streamId, perspectiveName);
    await Assert.That(hasAny).IsFalse();
  }

  [Test]
  public async Task DeleteAllSnapshotsAsync_NoSnapshots_DoesNotThrowAsync() {
    await _store.DeleteAllSnapshotsAsync(TrackedGuid.NewMedo().Value, "TestPerspective");
  }

  [Test]
  public async Task DeleteAllSnapshotsAsync_DoesNotAffectOtherStreamsAsync() {
    var stream1 = TrackedGuid.NewMedo().Value;
    var stream2 = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "OrderPerspective";

    await _store.CreateSnapshotAsync(stream1, perspectiveName, TrackedGuid.NewMedo().Value,
      JsonDocument.Parse("""{"s": 1}"""));
    await _store.CreateSnapshotAsync(stream2, perspectiveName, TrackedGuid.NewMedo().Value,
      JsonDocument.Parse("""{"s": 2}"""));

    await _store.DeleteAllSnapshotsAsync(stream1, perspectiveName);

    await Assert.That(await _store.HasAnySnapshotAsync(stream1, perspectiveName)).IsFalse();
    await Assert.That(await _store.HasAnySnapshotAsync(stream2, perspectiveName)).IsTrue();
  }

  #endregion

  #region CancellationToken Tests

  [Test]
  public async Task CreateSnapshotAsync_CanceledToken_ThrowsAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    async Task Act() => await _store.CreateSnapshotAsync(
      TrackedGuid.NewMedo().Value, "Test", TrackedGuid.NewMedo().Value,
      JsonDocument.Parse("""{"v": 1}"""), cts.Token);

    await Assert.That(Act).ThrowsException();
  }

  [Test]
  public async Task GetLatestSnapshotAsync_CanceledToken_ThrowsAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    async Task Act() => await _store.GetLatestSnapshotAsync(TrackedGuid.NewMedo().Value, "Test", cts.Token);
    await Assert.That(Act).ThrowsException();
  }

  #endregion

  private sealed class TestFixture : PostgresTestBase {
    public string TestConnectionString => ConnectionString;
  }
}
