using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

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
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var snapshotEventId = Guid.CreateVersion7();
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
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var snapshotEventId = Guid.CreateVersion7();

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
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    for (var i = 1; i <= 3; i++) {
      var eventId = Guid.CreateVersion7();
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
    var stream1 = Guid.CreateVersion7();
    var stream2 = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    var data1 = JsonDocument.Parse("""{"stream": 1}""");
    var data2 = JsonDocument.Parse("""{"stream": 2}""");
    await _store.CreateSnapshotAsync(stream1, perspectiveName, Guid.CreateVersion7(), data1);
    await _store.CreateSnapshotAsync(stream2, perspectiveName, Guid.CreateVersion7(), data2);

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
    var streamId = Guid.CreateVersion7();

    var data1 = JsonDocument.Parse("""{"perspective": "A"}""");
    var data2 = JsonDocument.Parse("""{"perspective": "B"}""");
    await _store.CreateSnapshotAsync(streamId, "PerspectiveA", Guid.CreateVersion7(), data1);
    await _store.CreateSnapshotAsync(streamId, "PerspectiveB", Guid.CreateVersion7(), data2);

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
    var streamId = Guid.CreateVersion7();
    var snapshotEventId = Guid.CreateVersion7();
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
      Guid.CreateVersion7(), "TestPerspective", Guid.CreateVersion7());
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_AllSnapshotsAfter_ReturnsNullAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    // Create a "before" event ID first (smaller UUID7)
    // Use Guid.Empty-like minimum to guarantee it's before any UUID7
    var beforeEventId = Guid.Parse("00000000-0000-7000-8000-000000000001");
    // Snapshot event IDs will be UUID7 (time-based, much larger)
    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();

    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId1, JsonDocument.Parse("""{"v": 1}"""));
    await _store.CreateSnapshotAsync(streamId, perspectiveName, eventId2, JsonDocument.Parse("""{"v": 2}"""));

    var result = await _store.GetLatestSnapshotBeforeAsync(streamId, perspectiveName, beforeEventId);
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetLatestSnapshotBeforeAsync_MixedSnapshots_ReturnsCorrectOneAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    // Create snapshots with guaranteed increasing event IDs using delays
    var eventId1 = Guid.CreateVersion7();
    await Task.Delay(10);
    var eventId2 = Guid.CreateVersion7();
    await Task.Delay(10);
    var beforeEventId = Guid.CreateVersion7(); // The "late event"
    await Task.Delay(10);
    var eventId3 = Guid.CreateVersion7();

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
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    var eventId1 = Guid.CreateVersion7();
    await Task.Delay(10);
    var beforeEventId = Guid.CreateVersion7();
    await Task.Delay(10);
    var eventId2 = Guid.CreateVersion7();

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
    var result = await _store.HasAnySnapshotAsync(Guid.CreateVersion7(), "TestPerspective");
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task HasAnySnapshotAsync_OneSnapshot_ReturnsTrueAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "TestPerspective";

    await _store.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(),
      JsonDocument.Parse("""{"v": 1}"""));

    var result = await _store.HasAnySnapshotAsync(streamId, perspectiveName);
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task HasAnySnapshotAsync_DifferentStream_ReturnsFalseAsync() {
    var streamId = Guid.CreateVersion7();
    var otherStreamId = Guid.CreateVersion7();

    await _store.CreateSnapshotAsync(streamId, "TestPerspective", Guid.CreateVersion7(),
      JsonDocument.Parse("""{"v": 1}"""));

    var result = await _store.HasAnySnapshotAsync(otherStreamId, "TestPerspective");
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task HasAnySnapshotAsync_DifferentPerspective_ReturnsFalseAsync() {
    var streamId = Guid.CreateVersion7();

    await _store.CreateSnapshotAsync(streamId, "PerspectiveA", Guid.CreateVersion7(),
      JsonDocument.Parse("""{"v": 1}"""));

    var result = await _store.HasAnySnapshotAsync(streamId, "PerspectiveB");
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region PruneOldSnapshotsAsync Tests

  [Test]
  public async Task PruneOldSnapshotsAsync_FewerThanKeepCount_DeletesNoneAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    await _store.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(),
      JsonDocument.Parse("""{"v": 1}"""));
    await _store.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(),
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
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    // Create 5 snapshots
    var eventIds = new Guid[5];
    for (var i = 0; i < 5; i++) {
      eventIds[i] = Guid.CreateVersion7();
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
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    for (var i = 0; i < 3; i++) {
      await _store.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(),
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
    await _store.PruneOldSnapshotsAsync(Guid.CreateVersion7(), "TestPerspective", keepCount: 5);
  }

  [Test]
  public async Task PruneOldSnapshotsAsync_DoesNotAffectOtherStreamsAsync() {
    var stream1 = Guid.CreateVersion7();
    var stream2 = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    // Create 3 snapshots for each stream
    for (var i = 0; i < 3; i++) {
      await _store.CreateSnapshotAsync(stream1, perspectiveName, Guid.CreateVersion7(),
        JsonDocument.Parse($$$"""{"s": 1, "v": {{{i + 1}}}}"""));
      await _store.CreateSnapshotAsync(stream2, perspectiveName, Guid.CreateVersion7(),
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
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    for (var i = 0; i < 3; i++) {
      await _store.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(),
        JsonDocument.Parse("""{"v": 1}"""));
    }

    await _store.DeleteAllSnapshotsAsync(streamId, perspectiveName);

    var hasAny = await _store.HasAnySnapshotAsync(streamId, perspectiveName);
    await Assert.That(hasAny).IsFalse();
  }

  [Test]
  public async Task DeleteAllSnapshotsAsync_NoSnapshots_DoesNotThrowAsync() {
    await _store.DeleteAllSnapshotsAsync(Guid.CreateVersion7(), "TestPerspective");
  }

  [Test]
  public async Task DeleteAllSnapshotsAsync_DoesNotAffectOtherStreamsAsync() {
    var stream1 = Guid.CreateVersion7();
    var stream2 = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";

    await _store.CreateSnapshotAsync(stream1, perspectiveName, Guid.CreateVersion7(),
      JsonDocument.Parse("""{"s": 1}"""));
    await _store.CreateSnapshotAsync(stream2, perspectiveName, Guid.CreateVersion7(),
      JsonDocument.Parse("""{"s": 2}"""));

    await _store.DeleteAllSnapshotsAsync(stream1, perspectiveName);

    await Assert.That(await _store.HasAnySnapshotAsync(stream1, perspectiveName)).IsFalse();
    await Assert.That(await _store.HasAnySnapshotAsync(stream2, perspectiveName)).IsTrue();
  }

  #endregion

  #region CancellationToken Tests

  [Test]
  public async Task CreateSnapshotAsync_CancelledToken_ThrowsAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    async Task Act() => await _store.CreateSnapshotAsync(
      Guid.CreateVersion7(), "Test", Guid.CreateVersion7(),
      JsonDocument.Parse("""{"v": 1}"""), cts.Token);

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

  private sealed class TestFixture : PostgresTestBase {
    public string TestConnectionString => ConnectionString;
  }
}
